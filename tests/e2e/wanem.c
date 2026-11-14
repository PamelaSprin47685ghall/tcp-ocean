/* wanem.c - user-space WAN emulator for the TBTun v2 testbed, used when the kernel has no sch_netem
 * (e.g. minimal container kernels).  One TUN device in the router namespace; policy routing sends every
 * forwarded packet from the two veths into the TUN; wanem delays / drops / rate-limits it and writes it
 * back; the kernel then forwards it to its destination (packets arriving from the TUN use the main table).
 *
 * Per direction (keyed by destination IPv4: 10.200.1.0/24 = "to near" (downlink), else "to far" (uplink)):
 *
 *   Two-stage service (v2):
 *     1. Per-5-tuple readiness (optional). The tuple may burst up to tuple_bucket_bytes, then proceeds
 *        at tuple_mbit. Credit is virtual time: next_free may sit up to burst_ns in the past. Charging
 *        does not clamp that time up to "now" (that used to cancel the burst).
 *        tuple_mode=shaper (default): a packet that exceeds the bucket waits.
 *        tuple_mode=policer: a packet that exceeds the bucket is dropped (drop_tuple).
 *     2. Packets whose per-flow ready time has been reached compete for the shared directional link
 *        (serialisation at rate_mbit). A packet that is not yet flow-ready does not reserve the link
 *        and cannot block other ready flows. Then + OWD + jitter.
 *
 *   loss_point=before (default): iid / Gilbert-Elliott / outage applied at ingress, before either stage
 *     (dropped packets do not consume per-tuple or shared service).
 *   loss_point=after: the same loss is applied after the packet has been assigned a shared-link slot
 *     (it consumed bottleneck service, then may be dropped).
 *
 * Write path: TUN is non-blocking. out counts only successful writes (write_ok). EAGAIN parks the
 * packet on a bounded pending-write queue (write_retry); the queue overflowing, or a hard write error,
 * is write_error_drop and records an error class. SIGTERM/SIGINT flush final stats (end queue depths).
 *
 * Config: key=value lines in a file, re-read on SIGHUP.  Stats: written to <stats> every second and
 * at exit. drop_synack=N drops the first N TCP SYN-ACKs towards near (a handshake retransmit on demand).
 * Deterministic: loss RNG is SplitMix64 seeded from config "seed" (default 1).
 * Build: gcc -O2 -o wanem wanem.c -lm
 *        gcc -O2 -DWANEM_SCHED_TEST -o test_wanem wanem.c -lm
 * Run: wanem <tun-name> <config> <stats>
 */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <linux/if.h>
#include <linux/if_tun.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <time.h>
#include <unistd.h>

#define WANEM_VERSION "2"

enum { MODE_SHAPER = 0, MODE_POLICER = 1 };
enum { LOSS_BEFORE = 0, LOSS_AFTER = 1 };

typedef struct {
    double owd_ms, jitter_ms, rate_mbit, loss, ge_p, ge_r, ge_1h, ge_1k, out_ms, out_every_s;
    long limit; uint64_t seed; long drop_synack;
    double tuple_mbit; long tuple_bucket_bytes;
    int tuple_mode;   /* MODE_SHAPER or MODE_POLICER */
    int loss_point;   /* LOSS_BEFORE or LOSS_AFTER */
    long write_queue; /* pending-write cap (EAGAIN) */
} cfg_t;

typedef struct {
    double tx_free_ns;
    int ge_bad;
    uint64_t in, out, drop_loss, drop_out, drop_limit, drop_tuple;
    uint64_t write_attempt, write_ok, write_retry, write_error_drop;
} dir_t;

typedef struct {
    uint64_t t_ns;      /* flow_ready (wait heap) or write time (due heap) */
    uint32_t len;
    uint32_t seq;
    unsigned char flags; /* bit0: drop after the shared-link slot (drop_synack, after) */
    unsigned char *buf;
} pkt_t;

typedef struct { pkt_t *a; long n, cap; } heap_t;

#define TCAP 65536
typedef struct {
    uint32_t sip, dip;
    uint16_t sport, dport;
    uint8_t proto, used;
    uint64_t next_free_ns, last_seen_ns;
} tuple_t;

typedef struct {
    unsigned char *buf;
    uint32_t len;
    uint32_t seq;
} wpending_t;

#define WQ_MAX 65536

static cfg_t C;
static dir_t D[2];
static volatile sig_atomic_t reload = 1;
static volatile sig_atomic_t ending = 0;
static heap_t wait_h, due_h;          /* wait: flow-ready; due: release/write time */
static uint64_t rng, seqc;
static tuple_t *Ttab;
static uint64_t tuple_active, tuple_shaped;
static wpending_t *wq;
static long wqn, wq_cap, wq_hi;
static int write_err_last;
static uint64_t write_err_eagain, write_err_intr, write_err_other;

static uint64_t sm64(void) { uint64_t z = (rng += 0x9E3779B97F4A7C15ULL); z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL; return z ^ (z >> 31); }
static double u01(void) { return (sm64() >> 11) * (1.0 / 9007199254740992.0); }
static double nrm(void) { double a = u01() + 1e-12, b = u01(); return __builtin_sqrt(-2 * __builtin_log(a)) * __builtin_cos(6.283185307179586 * b); }
static uint64_t now_ns(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (uint64_t)t.tv_sec * 1000000000ULL + t.tv_nsec; }
static void on_hup(int s) { (void)s; reload = 1; }
static void on_term(int s) { (void)s; ending = 1; }

static int less(const pkt_t *a, const pkt_t *b) { return a->t_ns < b->t_ns || (a->t_ns == b->t_ns && a->seq < b->seq); }
static void hgrow(heap_t *h) { if (h->n == h->cap) { h->cap = h->cap ? h->cap * 2 : 4096; h->a = realloc(h->a, (size_t)h->cap * sizeof(pkt_t)); } }
static void hpush(heap_t *h, pkt_t p) {
    hgrow(h);
    long i = h->n++;
    h->a[i] = p;
    while (i > 0) { long q = (i - 1) / 2; if (!less(&h->a[i], &h->a[q])) break; pkt_t t = h->a[i]; h->a[i] = h->a[q]; h->a[q] = t; i = q; }
}
static pkt_t hpop(heap_t *h) {
    pkt_t r = h->a[0];
    h->a[0] = h->a[--h->n];
    long i = 0;
    for (;;) {
        long l = 2 * i + 1, m = i;
        if (l < h->n && less(&h->a[l], &h->a[m])) m = l;
        if (l + 1 < h->n && less(&h->a[l + 1], &h->a[m])) m = l + 1;
        if (m == i) break;
        pkt_t t = h->a[i]; h->a[i] = h->a[m]; h->a[m] = t; i = m;
    }
    return r;
}
static long queued_total(void) { return wait_h.n + due_h.n + wqn; }

static uint32_t thash(uint32_t sip, uint32_t dip, uint16_t sp, uint16_t dp, uint8_t proto) {
    uint64_t x = ((uint64_t)sip << 32) ^ dip ^ ((uint64_t)sp << 16) ^ dp ^ ((uint64_t)proto << 8);
    x *= 0x9E3779B97F4A7C15ULL;
    return (uint32_t)(x ^ (x >> 32));
}

static int parse_tuple(const unsigned char *p, int n, uint32_t *sip, uint32_t *dip,
                       uint16_t *sp, uint16_t *dp, uint8_t *proto) {
    if (n < 20 || (p[0] >> 4) != 4) return -1;
    int ihl = (p[0] & 15) * 4;
    if (ihl < 20 || n < ihl + 4) return -1;
    if ((((p[6] << 8) | p[7]) & 0x1fff) != 0) return -1; /* fragmented: no ports */
    *proto = p[9];
    if (*proto != 6 && *proto != 17) return -1;
    memcpy(sip, p + 12, 4);
    memcpy(dip, p + 16, 4);
    memcpy(sp, p + ihl, 2);
    memcpy(dp, p + ihl + 2, 2);
    return 0;
}

static tuple_t *tuple_get(uint32_t sip, uint32_t dip, uint16_t sp, uint16_t dp, uint8_t proto, uint64_t tn) {
    if (!Ttab) {
        Ttab = calloc(TCAP, sizeof *Ttab);
        if (!Ttab) return NULL;
    }
    uint32_t h = thash(sip, dip, sp, dp, proto);
    int free_i = -1;
    for (int p = 0; p < TCAP; p++) {
        uint32_t i = (h + (uint32_t)p) % TCAP;
        tuple_t *e = &Ttab[i];
        if (!e->used) { if (free_i < 0) free_i = (int)i; break; }
        if (e->sip == sip && e->dip == dip && e->sport == sp && e->dport == dp && e->proto == proto)
            return e;
        if (tn > e->last_seen_ns && tn - e->last_seen_ns > 60ULL * 1000000000ULL) {
            if (free_i < 0) free_i = (int)i;
        }
    }
    if (free_i < 0) return NULL;
    tuple_t *e = &Ttab[free_i];
    if (e->used) tuple_active--;
    memset(e, 0, sizeof *e);
    e->used = 1; e->sip = sip; e->dip = dip; e->sport = sp; e->dport = dp; e->proto = proto;
    tuple_active++;
    return e;
}

/* Per-tuple virtual-time leaky bucket.
 * next_free may be in the past (accumulated credit), floored at now - burst_ns.
 * Charge from that virtual time, not from max(next_free, now): clamping to now cancelled the burst.
 * Returns 0 and writes the earliest time this packet may enter the shared link;
 * returns -1 if a policer drops it (drop_tuple already counted). */
static int tuple_accept(const unsigned char *pkt, int n, uint64_t tn, uint64_t *ready_out) {
    uint32_t sip, dip; uint16_t sp, dp; uint8_t proto;
    if (C.tuple_mbit <= 0) { *ready_out = tn; return 0; }
    if (parse_tuple(pkt, n, &sip, &dip, &sp, &dp, &proto) < 0) { *ready_out = tn; return 0; }
    tuple_t *e = tuple_get(sip, dip, sp, dp, proto, tn);
    if (!e) { *ready_out = tn; return 0; }
    e->last_seen_ns = tn;
    double rate_bps = C.tuple_mbit * 1e6;
    uint64_t burst_ns = 0;
    if (C.tuple_bucket_bytes > 0 && rate_bps > 0)
        burst_ns = (uint64_t)(C.tuple_bucket_bytes * 8.0 / rate_bps * 1e9);
    uint64_t floor_ns = (tn > burst_ns) ? tn - burst_ns : 0;
    if (e->next_free_ns < floor_ns) e->next_free_ns = floor_ns;
    uint64_t virt = e->next_free_ns; /* may be < tn — that is the unused burst credit */
    if (C.tuple_mode == MODE_POLICER) {
        /* Drop when the packet does not fit in the tokens currently on hand.
         * The shaper would instead send immediately and push next_free into the future. */
        double tokens = 0;
        if (virt <= tn && rate_bps > 0)
            tokens = (double)(tn - virt) * rate_bps / 8.0 / 1e9;
        if (tokens + 1e-9 < (double)n) return -1;
    }
    uint64_t ser = (rate_bps > 0) ? (uint64_t)(n * 8.0 / rate_bps * 1e9) : 0;
    e->next_free_ns = virt + ser;
    if (virt > tn) {
        tuple_shaped++;
        *ready_out = virt;
    } else {
        *ready_out = tn;
    }
    return 0;
}

static int lost(dir_t *d, uint64_t t) {
    if (C.out_every_s > 0 && C.out_ms > 0) {
        double per = C.out_every_s * 1e9, ph = (double)(t % (uint64_t)per);
        if (ph >= per - C.out_ms * 1e6) { d->drop_out++; return 1; }
    }
    if (C.ge_p > 0 || C.ge_r > 0) {
        if (!d->ge_bad) { if (u01() < C.ge_p) d->ge_bad = 1; } else { if (u01() < C.ge_r) d->ge_bad = 0; }
        double pl = d->ge_bad ? C.ge_1h : C.ge_1k;
        if (u01() < pl) { d->drop_loss++; return 1; }
        return 0;
    }
    if (C.loss > 0 && u01() < C.loss) { d->drop_loss++; return 1; }
    return 0;
}

/* Assign a shared-link slot to a flow-ready packet. Does not advance the link for
 * packets that are not yet ready: the caller only hands over packets with ready <= now
 * (or, in the offline test, ready <= the virtual 'now'). */
static uint64_t link_assign(dir_t *d, uint64_t ready, int n, uint64_t now) {
    uint64_t start = ready;
    if (d->tx_free_ns > (double)start) start = (uint64_t)d->tx_free_ns;
    if (start < now) start = now;
    double ser = (C.rate_mbit > 0) ? (n * 8.0 / (C.rate_mbit * 1e6) * 1e9) : 0;
    d->tx_free_ns = (double)start + ser;
    return (uint64_t)d->tx_free_ns;
}

static uint64_t add_delay(uint64_t depart, uint64_t now) {
    double rel = (double)depart + C.owd_ms * 1e6 + (C.jitter_ms > 0 ? nrm() * C.jitter_ms * 1e6 : 0);
    if (rel < (double)now) rel = (double)now;
    if (rel < 0) rel = 0;
    return (uint64_t)rel;
}

/* Move every flow-ready packet onto the shared link, then onto the due (write) heap. */
static void promote_ready(uint64_t now) {
    while (wait_h.n && wait_h.a[0].t_ns <= now) {
        pkt_t p = hpop(&wait_h);
        int di = (int)(p.seq >> 31);
        dir_t *d = &D[di];
        uint64_t depart = link_assign(d, p.t_ns, (int)p.len, now);
        if ((p.flags & 1) || (C.loss_point == LOSS_AFTER && lost(d, now))) {
            if (p.flags & 1) d->drop_loss++;
            free(p.buf);
            continue;
        }
        p.t_ns = add_delay(depart, now);
        hpush(&due_h, p);
    }
}

static void note_write_err(int err) {
    write_err_last = err;
    if (err == EAGAIN || err == EWOULDBLOCK) write_err_eagain++;
    else if (err == EINTR) write_err_intr++;
    else write_err_other++;
}

static const char *err_class(void) {
    if (write_err_other) return "other";
    if (write_err_intr && !write_err_eagain) return "eintr";
    if (write_err_eagain) return "eagain";
    if (write_err_last == 0) return "none";
    return "other";
}

static void wq_push(unsigned char *buf, uint32_t len, uint32_t seq) {
    if (wqn == wq_cap) {
        long cap = wq_cap ? wq_cap * 2 : 64;
        if (cap > WQ_MAX) cap = WQ_MAX;
        if (cap <= wq_cap) { free(buf); return; }
        wpending_t *n = realloc(wq, (size_t)cap * sizeof *wq);
        if (!n) { free(buf); return; }
        wq = n; wq_cap = cap;
    }
    wq[wqn].buf = buf; wq[wqn].len = len; wq[wqn].seq = seq; wqn++;
    if (wqn > wq_hi) wq_hi = wqn;
}

/* Try to write one packet. Returns 1 if consumed (ok or dropped), 0 if parked on EAGAIN. */
static int write_one(int fd, unsigned char *buf, uint32_t len, uint32_t seq) {
    int di = (int)(seq >> 31);
    dir_t *d = &D[di];
    d->write_attempt++;
    ssize_t w = write(fd, buf, len);
    if (w == (ssize_t)len) {
        d->write_ok++;
        d->out++;
        free(buf);
        return 1;
    }
    int err = errno;
    if (w < 0 && (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)) {
        d->write_retry++;
        note_write_err(err);
        if (wqn >= C.write_queue) {
            d->write_error_drop++;
            free(buf);
            return 1;
        }
        wq_push(buf, len, seq);
        return 0;
    }
    note_write_err(err);
    d->write_error_drop++;
    free(buf);
    return 1;
}

static void flush_pending(int fd) {
    long dst = 0;
    for (long i = 0; i < wqn; i++) {
        int di = (int)(wq[i].seq >> 31);
        dir_t *d = &D[di];
        d->write_attempt++;
        ssize_t w = write(fd, wq[i].buf, wq[i].len);
        if (w == (ssize_t)wq[i].len) {
            d->write_ok++;
            d->out++;
            free(wq[i].buf);
            continue;
        }
        int err = errno;
        if (w < 0 && (err == EAGAIN || err == EWOULDBLOCK || err == EINTR)) {
            d->write_retry++;
            note_write_err(err);
            wq[dst++] = wq[i];
            continue;
        }
        note_write_err(err);
        d->write_error_drop++;
        free(wq[i].buf);
    }
    wqn = dst;
}

static void emit_due(int fd, uint64_t t) {
    if (wqn) flush_pending(fd);
    while (due_h.n && due_h.a[0].t_ns <= t) {
        pkt_t p = hpop(&due_h);
        if (!write_one(fd, p.buf, p.len, p.seq))
            break; /* EAGAIN: parked; later dues wait so writes stay in order */
    }
}

static void write_stats(const char *path) {
    FILE *s = fopen(path, "w");
    if (!s) return;
    for (int i = 0; i < 2; i++) {
        fprintf(s, "%s in %llu out %llu drop_loss %llu drop_outage %llu drop_limit %llu drop_tuple %llu"
                    " write_attempt %llu write_ok %llu write_retry %llu write_error_drop %llu\n",
                i ? "to_near" : "to_far",
                (unsigned long long)D[i].in, (unsigned long long)D[i].out,
                (unsigned long long)D[i].drop_loss, (unsigned long long)D[i].drop_out,
                (unsigned long long)D[i].drop_limit, (unsigned long long)D[i].drop_tuple,
                (unsigned long long)D[i].write_attempt, (unsigned long long)D[i].write_ok,
                (unsigned long long)D[i].write_retry, (unsigned long long)D[i].write_error_drop);
    }
    fprintf(s, "queued %ld wait %ld due %ld write_pending %ld write_pending_hi %ld"
                " tuples %llu shaped %llu write_err_class %s write_err_last %d"
                " write_err_eagain %llu write_err_intr %llu write_err_other %llu wanem_version %s"
                " tuple_mode %s loss_point %s write_queue %ld\n",
            queued_total(), wait_h.n, due_h.n, wqn, wq_hi,
            (unsigned long long)tuple_active, (unsigned long long)tuple_shaped,
            err_class(), write_err_last,
            (unsigned long long)write_err_eagain, (unsigned long long)write_err_intr,
            (unsigned long long)write_err_other, WANEM_VERSION,
            C.tuple_mode == MODE_POLICER ? "policer" : "shaper",
            C.loss_point == LOSS_AFTER ? "after" : "before",
            C.write_queue);
    fclose(s);
}

static void load(const char *path) {
    cfg_t c = { 200, 0, 1000, 0, 0, 0, 0, 0, 0, 0, 100000, 1, 0, 0, 32000, MODE_SHAPER, LOSS_BEFORE, 4096 };
    FILE *f = fopen(path, "r"); char line[256], k[64], val[128];
    if (f) {
        while (fgets(line, sizeof line, f)) {
            char *s = line;
            while (*s == ' ' || *s == '\t') s++;
            if (*s == '#' || *s == '\n' || *s == '\0') continue;
            if (sscanf(s, "%63[^=]=%127s", k, val) != 2) continue;
            if (!strcmp(k, "tuple_mode")) {
                c.tuple_mode = !strcmp(val, "policer") ? MODE_POLICER : MODE_SHAPER;
            } else if (!strcmp(k, "loss_point")) {
                c.loss_point = !strcmp(val, "after") ? LOSS_AFTER : LOSS_BEFORE;
            } else {
                double v = atof(val);
                if (!strcmp(k, "owd_ms")) c.owd_ms = v;
                else if (!strcmp(k, "jitter_ms")) c.jitter_ms = v;
                else if (!strcmp(k, "rate_mbit")) c.rate_mbit = v;
                else if (!strcmp(k, "loss")) c.loss = v;
                else if (!strcmp(k, "ge_p")) c.ge_p = v;
                else if (!strcmp(k, "ge_r")) c.ge_r = v;
                else if (!strcmp(k, "ge_1h")) c.ge_1h = v;
                else if (!strcmp(k, "ge_1k")) c.ge_1k = v;
                else if (!strcmp(k, "outage_ms")) c.out_ms = v;
                else if (!strcmp(k, "outage_every_s")) c.out_every_s = v;
                else if (!strcmp(k, "limit")) c.limit = (long)v;
                else if (!strcmp(k, "seed")) c.seed = (uint64_t)v;
                else if (!strcmp(k, "drop_synack")) c.drop_synack = (long)v;
                else if (!strcmp(k, "tuple_mbit")) c.tuple_mbit = v;
                else if (!strcmp(k, "tuple_bucket_bytes")) c.tuple_bucket_bytes = (long)v;
                else if (!strcmp(k, "write_queue")) c.write_queue = (long)v > 0 ? (long)v : 4096;
            }
        }
        fclose(f);
    }
    if (c.write_queue > WQ_MAX) c.write_queue = WQ_MAX;
    C = c; rng = c.seed * 0x2545F4914F6CDD1DULL;
    fprintf(stderr, "wanem %s: owd %.1f ms jitter %.1f rate %.0f Mbit loss %.4f ge(%.3f,%.3f,%.3f,%.3f) "
                    "outage %.0f ms/%.1f s limit %ld seed %llu tuple %.3f Mbit bucket %ld B "
                    "tuple_mode %s loss_point %s write_queue %ld\n",
            WANEM_VERSION, C.owd_ms, C.jitter_ms, C.rate_mbit, C.loss, C.ge_p, C.ge_r, C.ge_1h, C.ge_1k,
            C.out_ms, C.out_every_s, C.limit, (unsigned long long)C.seed,
            C.tuple_mbit, C.tuple_bucket_bytes,
            C.tuple_mode == MODE_POLICER ? "policer" : "shaper",
            C.loss_point == LOSS_AFTER ? "after" : "before",
            C.write_queue);
}

#ifdef WANEM_SCHED_TEST
/* Offline schedule tests (no TUN). See the review's 2-flow example and the burst/policer cases. */
static int failures;

static void reset_sched(void) {
    memset(D, 0, sizeof D);
    while (wait_h.n) { pkt_t p = hpop(&wait_h); free(p.buf); }
    while (due_h.n) { pkt_t p = hpop(&due_h); free(p.buf); }
    if (Ttab) memset(Ttab, 0, TCAP * sizeof *Ttab);
    tuple_active = tuple_shaped = seqc = 0;
}

/* Minimal IPv4+TCP packet with a unique source port identifying the flow. */
static unsigned char *fake_pkt(int flow, int len) {
    unsigned char *p = calloc(1, (size_t)len);
    p[0] = 0x45;
    p[2] = (unsigned char)(len >> 8); p[3] = (unsigned char)len;
    p[9] = 6;
    p[12] = 10; p[13] = 200; p[14] = 1; p[15] = 2;
    p[16] = 10; p[17] = 200; p[18] = 2; p[19] = 2;
    p[20] = (unsigned char)((0xC000 + flow) >> 8);
    p[21] = (unsigned char)(0xC000 + flow);
    p[22] = 0x20; p[23] = 0xFB; /* dst 8443 */
    return p;
}

static void ingest(int flow, int len, uint64_t arrival, int di) {
    unsigned char *buf = fake_pkt(flow, len);
    uint64_t ready = arrival;
    if (tuple_accept(buf, len, arrival, &ready) < 0) {
        D[di].drop_tuple++;
        free(buf);
        return;
    }
    if (queued_total() >= C.limit) { D[di].drop_limit++; free(buf); return; }
    pkt_t p;
    p.t_ns = ready;
    p.len = (uint32_t)len;
    p.seq = (uint32_t)((seqc++ & 0x7FFFFFFF) | ((uint32_t)di << 31));
    p.flags = 0;
    p.buf = buf;
    hpush(&wait_h, p);
}

/* Collect due-heap write times after promoting up to 'now'. owd is left at 0 in tests. */
static int collect_due(uint64_t now, uint64_t *out, int maxn) {
    promote_ready(now);
    int k = 0;
    /* copy in heap order by popping */
    heap_t saved = {0};
    while (due_h.n && k < maxn) {
        pkt_t p = hpop(&due_h);
        out[k++] = p.t_ns;
        hpush(&saved, p);
    }
    while (saved.n) hpush(&due_h, hpop(&saved));
    return k;
}

static void expect(const char *name, int cond) {
    if (!cond) { fprintf(stderr, "FAIL %s\n", name); failures++; }
    else fprintf(stderr, "ok   %s\n", name);
}

int main(void) {
    /* Shared 1 Gbit, per-flow 4 Mbit, 32 kB burst, no delay (we assert on depart times). */
    memset(&C, 0, sizeof C);
    C.rate_mbit = 1000;
    C.tuple_mbit = 4;
    C.tuple_bucket_bytes = 32000;
    C.tuple_mode = MODE_SHAPER;
    C.loss_point = LOSS_BEFORE;
    C.limit = 100000;
    C.write_queue = 4096;
    C.owd_ms = 0;
    C.jitter_ms = 0;

    /* Review example: two flows, two 1500 B packets each, same arrival (t=0).
     * Per-flow spacing = 1500*8/4e6 = 3000 us. Shared ser = 1500*8/1e9 = 12 us.
     * A2 and B2 must not be released at the same instant: they serialize 12 us apart. */
    reset_sched();
    ingest(0, 1500, 0, 0); /* A1 */
    ingest(0, 1500, 0, 0); /* A2 */
    ingest(1, 1500, 0, 0); /* B1 */
    ingest(1, 1500, 0, 0); /* B2 */
    /* At t=0 only A1 and B1 are flow-ready. */
    uint64_t early[8]; int n0 = collect_due(0, early, 8);
    expect("t=0 promotes exactly 2 (A1,B1)", n0 == 2);
    expect("A1/B1 serialized 12 us apart", n0 == 2 && early[1] - early[0] == 12000);

    uint64_t late[8]; int n1 = collect_due(3000000ULL, late, 8);
    /* due heap still holds A1,B1 plus A2,B2 */
    expect("t=3000 us: 4 packets due", n1 == 4);
    /* The two later departures are A2,B2: 3000 us and 3012 us (plus any leftover link time,
     * which is only 24 us from A1/B1, well under 3000 us). */
    uint64_t a2 = late[2], b2 = late[3];
    expect("A2/B2 not simultaneous", a2 != b2);
    uint64_t gap = a2 > b2 ? a2 - b2 : b2 - a2;
    expect("A2/B2 share-link gap is 12 us", gap == 12000);
    /* depart = start + 12 us serialisation; start is 3000 us (link was free after 24 us). */
    expect("A2 departs at 3000+12 us (not pre-charged)", (a2 < b2 ? a2 : b2) == 3012000ULL);
    /* And the not-yet-ready A2 did not push B1 out: B1 departed at 12 us, not at 3000 us. */
    expect("B1 not blocked by not-ready A2", early[1] == 24000 || early[1] == 12000);

    /* Burst credit: after long idle, next_free is floored at now-burst; several packets
     * go immediately (ready=now) as long as they fit in 32 kB. 21*1500=31500 < 32000. */
    reset_sched();
    const uint64_t t_idle = 10ULL * 1000000000ULL; /* 10 s later */
    int immediate = 0;
    for (int i = 0; i < 21; i++) ingest(0, 1500, t_idle, 0);
    promote_ready(t_idle);
    expect("21 x 1500 B fit in 32 kB burst and are ready at once", wait_h.n == 0 && due_h.n == 21);
    /* 22 x 1500 B = 33000 > 32 kB, but the 22nd still starts now (goes into debt).
     * The 23rd must wait until next_free (about 2 ms later). */
    reset_sched();
    for (int i = 0; i < 23; i++) ingest(0, 1500, t_idle, 0);
    promote_ready(t_idle);
    expect("23rd 1500 B packet waits (burst already in debt)", wait_h.n == 1 && due_h.n == 22);
    immediate = 0;
    for (long i = 0; i < due_h.n; i++) if (due_h.a[i].t_ns >= t_idle) immediate++;
    (void)immediate;

    /* Contrast with the old bug: if charge used max(next_free, now), packet 2 of a burst
     * would be delayed by a full 3 ms. With the fix, packet 2 departs 12 us after packet 1. */
    reset_sched();
    ingest(0, 1500, t_idle, 0);
    ingest(0, 1500, t_idle, 0);
    uint64_t bur[4]; collect_due(t_idle, bur, 4);
    expect("burst packet 2 is not paced 3 ms later", due_h.n >= 2 && (bur[1] - bur[0]) == 12000);

    /* Policer: the same 22nd packet is dropped, not queued. */
    reset_sched();
    C.tuple_mode = MODE_POLICER;
    for (int i = 0; i < 22; i++) ingest(0, 1500, t_idle, 0);
    promote_ready(t_idle);
    expect("policer drops the over-bucket packet", D[0].drop_tuple == 1 && wait_h.n == 0 && due_h.n == 21);
    C.tuple_mode = MODE_SHAPER;

    if (failures) { fprintf(stderr, "%d wanem schedule test(s) failed\n", failures); return 1; }
    fprintf(stderr, "all wanem schedule tests passed\n");
    return 0;
}

#else

int main(int argc, char **argv) {
    if (argc < 4) { fprintf(stderr, "usage: wanem <tun> <config> <stats>\n"); return 2; }
    int fd = open("/dev/net/tun", O_RDWR | O_NONBLOCK); if (fd < 0) { perror("tun"); return 1; }
    struct ifreq ifr; memset(&ifr, 0, sizeof ifr); ifr.ifr_flags = IFF_TUN | IFF_NO_PI; strncpy(ifr.ifr_name, argv[1], IFNAMSIZ - 1);
    if (ioctl(fd, TUNSETIFF, &ifr) < 0) { perror("TUNSETIFF"); return 1; }
    signal(SIGHUP, on_hup);
    signal(SIGPIPE, SIG_IGN);
    signal(SIGTERM, on_term);
    signal(SIGINT, on_term);
    unsigned char tmp[65536]; uint64_t next_stats = now_ns() + 1000000000ULL;
    for (;;) {
        if (ending) { uint64_t t = now_ns(); promote_ready(t); emit_due(fd, t); write_stats(argv[3]); return 0; }
        if (reload) { reload = 0; load(argv[2]); }
        uint64_t t = now_ns();
        promote_ready(t);
        emit_due(fd, t);
        int to = 1000;
        if (wait_h.n) {
            uint64_t dt = wait_h.a[0].t_ns - t;
            int ms = (int)(dt / 1000000ULL);
            if (ms < to) to = ms;
        }
        if (due_h.n) {
            uint64_t dt = due_h.a[0].t_ns - t;
            int ms = (int)(dt / 1000000ULL);
            if (ms < to) to = ms;
        }
        if (to < 0) to = 0;
        if (t >= next_stats) { write_stats(argv[3]); next_stats = t + 1000000000ULL; }
        short ev = POLLIN;
        if (wqn) ev = (short)(ev | POLLOUT);
        struct pollfd pf = { fd, ev, 0 };
        int r = poll(&pf, 1, to > 1000 ? 1000 : to);
        if (r < 0 && errno == EINTR) continue;
        if (r <= 0) continue;
        if (pf.revents & POLLOUT) flush_pending(fd);
        if (!(pf.revents & POLLIN)) continue;
        for (int burst = 0; burst < 256; burst++) {
            ssize_t n = read(fd, tmp, sizeof tmp); if (n <= 0) break;
            if (n < 20 || (tmp[0] >> 4) != 4) continue;                       /* IPv4 only */
            int di = (tmp[16] == 10 && tmp[17] == 200 && tmp[18] == 1) ? 1 : 0; /* dst 10.200.1.x -> to near */
            dir_t *d = &D[di]; uint64_t tn = now_ns(); d->in++;
            int synack = 0;
            if (di == 1 && C.drop_synack > 0 && tmp[9] == 6) {
                int ihl = (tmp[0] & 15) * 4;
                if (n > ihl + 13 && (tmp[ihl + 13] & 0x12) == 0x12) synack = 1;
            }
            if (C.loss_point == LOSS_BEFORE && lost(d, tn)) continue;
            if (synack && C.loss_point == LOSS_BEFORE) { C.drop_synack--; d->drop_loss++; continue; }
            uint64_t ready = tn;
            if (tuple_accept(tmp, (int)n, tn, &ready) < 0) { d->drop_tuple++; continue; }
            if (queued_total() >= C.limit) { d->drop_limit++; continue; }
            unsigned char flags = 0;
            if (synack && C.loss_point == LOSS_AFTER) { C.drop_synack--; flags = 1; }
            pkt_t p;
            p.t_ns = ready;
            p.len = (uint32_t)n;
            p.seq = (uint32_t)((seqc++ & 0x7FFFFFFF) | ((uint32_t)di << 31));
            p.flags = flags;
            p.buf = malloc((size_t)n);
            if (!p.buf) { d->drop_limit++; continue; }
            memcpy(p.buf, tmp, (size_t)n);
            hpush(&wait_h, p);
        }
        /* Promote anything that became ready during this burst so later arrivals in the
         * same poll cycle do not have to wait a full timeout. */
        promote_ready(now_ns());
    }
}

#endif
