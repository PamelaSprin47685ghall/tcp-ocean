/* wanem.c - user-space WAN emulator for the TBTun v2 testbed, used when the kernel has no sch_netem
 * (e.g. minimal container kernels).  One TUN device in the router namespace; policy routing sends every
 * forwarded packet from the two veths into the TUN; wanem delays / drops / rate-limits it and writes it
 * back; the kernel then forwards it to its destination (packets arriving from the TUN use the main table).
 *
 * Per direction (keyed by destination IPv4: 10.200.1.0/24 = "to near" (downlink), else "to far" (uplink)):
 *   release = max(now, tx_free) + len*8/rate  (serialisation, FIFO)  ->  + OWD + jitter
 *   loss:  iid p, or Gilbert-Elliott (p, r, 1-h, 1-k), plus optional periodic full outage.
 * Optional per-5-tuple token bucket (tuple_mbit, tuple_bucket_bytes): a leaky-bucket shaper
 * for IPv4 TCP/UDP, applied before the shared directional rate. 0 disables it.
 * Config: key=value lines in a file, re-read on SIGHUP.  Stats: written to <stats> every second.
 * drop_synack=N drops the first N TCP SYN-ACKs towards near (a handshake retransmit on demand).
 * Deterministic: loss RNG is SplitMix64 seeded from config "seed" (default 1).
 * Build: gcc -O2 -o wanem wanem.c        Run: wanem <tun-name> <config> <stats>
 */
#define _GNU_SOURCE
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

typedef struct {
    double owd_ms, jitter_ms, rate_mbit, loss, ge_p, ge_r, ge_1h, ge_1k, out_ms, out_every_s;
    long limit; uint64_t seed; long drop_synack;
    double tuple_mbit; long tuple_bucket_bytes;
} cfg_t;
typedef struct { double tx_free_ns; int ge_bad; uint64_t in, out, drop_loss, drop_out, drop_limit, drop_tuple; } dir_t;
typedef struct { uint64_t t_ns; uint32_t len; uint32_t seq; unsigned char *buf; } pkt_t;

#define TCAP 65536
typedef struct {
    uint32_t sip, dip;
    uint16_t sport, dport;
    uint8_t proto, used;
    uint64_t next_free_ns, last_seen_ns;
} tuple_t;

static cfg_t C; static dir_t D[2]; static volatile sig_atomic_t reload = 1;
static pkt_t *heap; static long hn, hcap; static uint64_t rng, seqc;
static tuple_t *Ttab; static uint64_t tuple_active, tuple_shaped;

static uint64_t sm64(void) { uint64_t z = (rng += 0x9E3779B97F4A7C15ULL); z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL; return z ^ (z >> 31); }
static double u01(void) { return (sm64() >> 11) * (1.0 / 9007199254740992.0); }
static double nrm(void) { double a = u01() + 1e-12, b = u01(); return __builtin_sqrt(-2 * __builtin_log(a)) * __builtin_cos(6.283185307179586 * b); }
static uint64_t now_ns(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (uint64_t)t.tv_sec * 1000000000ULL + t.tv_nsec; }
static void on_hup(int s) { (void)s; reload = 1; }
static int less(pkt_t *a, pkt_t *b) { return a->t_ns < b->t_ns || (a->t_ns == b->t_ns && a->seq < b->seq); }
static void hpush(pkt_t p) { if (hn == hcap) { hcap = hcap ? hcap * 2 : 4096; heap = realloc(heap, hcap * sizeof(pkt_t)); }
  long i = hn++; heap[i] = p; while (i > 0) { long q = (i - 1) / 2; if (!less(&heap[i], &heap[q])) break; pkt_t t = heap[i]; heap[i] = heap[q]; heap[q] = t; i = q; } }
static pkt_t hpop(void) { pkt_t r = heap[0]; heap[0] = heap[--hn]; long i = 0;
  for (;;) { long l = 2 * i + 1, m = i; if (l < hn && less(&heap[l], &heap[m])) m = l; if (l + 1 < hn && less(&heap[l + 1], &heap[m])) m = l + 1; if (m == i) break; pkt_t t = heap[i]; heap[i] = heap[m]; heap[m] = t; i = m; }
  return r; }

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
        /* Recycle a slot idle for 60 s so the table does not fill with dead flows. */
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

/* Leaky-bucket shaper: the tuple may burst up to tuple_bucket_bytes, then  proceeds at tuple_mbit.
   Returns the earliest time this packet may enter the shared directional FIFO. */
static uint64_t tuple_ready(const unsigned char *pkt, int n, uint64_t tn) {
    uint32_t sip, dip; uint16_t sp, dp; uint8_t proto;
    if (C.tuple_mbit <= 0) return tn;
    if (parse_tuple(pkt, n, &sip, &dip, &sp, &dp, &proto) < 0) return tn;
    tuple_t *e = tuple_get(sip, dip, sp, dp, proto, tn);
    if (!e) return tn;
    e->last_seen_ns = tn;
    double rate_bps = C.tuple_mbit * 1e6;
    uint64_t burst_ns = 0;
    if (C.tuple_bucket_bytes > 0 && rate_bps > 0)
        burst_ns = (uint64_t)(C.tuple_bucket_bytes * 8.0 / rate_bps * 1e9);
    uint64_t floor_ns = (tn > burst_ns) ? tn - burst_ns : 0;
    if (e->next_free_ns < floor_ns) e->next_free_ns = floor_ns;
    uint64_t start = e->next_free_ns < tn ? tn : e->next_free_ns;
    uint64_t ser = (uint64_t)(n * 8.0 / rate_bps * 1e9);
    e->next_free_ns = start + ser;
    if (start > tn) tuple_shaped++;
    return start;
}

static void load(const char *path) {
  cfg_t c = { 200, 0, 1000, 0, 0, 0, 0, 0, 0, 0, 100000, 1, 0, 0, 32000 };
  FILE *f = fopen(path, "r"); char line[256], k[64]; double v;
  if (f) {
    while (fgets(line, sizeof line, f)) {
      char *s = line;
      while (*s == ' ' || *s == '\t') s++;
      if (*s == '#' || *s == '\n' || *s == '\0') continue;
      if (sscanf(s, "%63[^=]=%lf", k, &v) != 2) continue;
      if (!strcmp(k, "owd_ms")) c.owd_ms = v; else if (!strcmp(k, "jitter_ms")) c.jitter_ms = v; else if (!strcmp(k, "rate_mbit")) c.rate_mbit = v;
      else if (!strcmp(k, "loss")) c.loss = v; else if (!strcmp(k, "ge_p")) c.ge_p = v; else if (!strcmp(k, "ge_r")) c.ge_r = v;
      else if (!strcmp(k, "ge_1h")) c.ge_1h = v; else if (!strcmp(k, "ge_1k")) c.ge_1k = v; else if (!strcmp(k, "outage_ms")) c.out_ms = v;
      else if (!strcmp(k, "outage_every_s")) c.out_every_s = v; else if (!strcmp(k, "limit")) c.limit = (long)v; else if (!strcmp(k, "seed")) c.seed = (uint64_t)v;
      else if (!strcmp(k, "drop_synack")) c.drop_synack = (long)v;
      else if (!strcmp(k, "tuple_mbit")) c.tuple_mbit = v;
      else if (!strcmp(k, "tuple_bucket_bytes")) c.tuple_bucket_bytes = (long)v;
    }
    fclose(f);
  }
  C = c; rng = c.seed * 0x2545F4914F6CDD1DULL;
  fprintf(stderr, "wanem: owd %.1f ms jitter %.1f rate %.0f Mbit loss %.4f ge(%.3f,%.3f,%.3f,%.3f) outage %.0f ms/%.1f s limit %ld seed %llu tuple %.3f Mbit bucket %ld B\n",
          C.owd_ms, C.jitter_ms, C.rate_mbit, C.loss, C.ge_p, C.ge_r, C.ge_1h, C.ge_1k, C.out_ms, C.out_every_s, C.limit, (unsigned long long)C.seed,
          C.tuple_mbit, C.tuple_bucket_bytes);
}
static int lost(dir_t *d, uint64_t t) {
  if (C.out_every_s > 0 && C.out_ms > 0) { double per = C.out_every_s * 1e9, ph = (double)(t % (uint64_t)per); if (ph >= per - C.out_ms * 1e6) { d->drop_out++; return 1; } }
  if (C.ge_p > 0 || C.ge_r > 0) {          /* Gilbert-Elliott: state transition, then loss in state */
    if (!d->ge_bad) { if (u01() < C.ge_p) d->ge_bad = 1; } else { if (u01() < C.ge_r) d->ge_bad = 0; }
    double pl = d->ge_bad ? C.ge_1h : C.ge_1k; if (u01() < pl) { d->drop_loss++; return 1; } return 0; }
  if (C.loss > 0 && u01() < C.loss) { d->drop_loss++; return 1; }
  return 0;
}
int main(int argc, char **argv) {
  if (argc < 4) { fprintf(stderr, "usage: wanem <tun> <config> <stats>\n"); return 2; }
  int fd = open("/dev/net/tun", O_RDWR | O_NONBLOCK); if (fd < 0) { perror("tun"); return 1; }
  struct ifreq ifr; memset(&ifr, 0, sizeof ifr); ifr.ifr_flags = IFF_TUN | IFF_NO_PI; strncpy(ifr.ifr_name, argv[1], IFNAMSIZ - 1);
  if (ioctl(fd, TUNSETIFF, &ifr) < 0) { perror("TUNSETIFF"); return 1; }
  signal(SIGHUP, on_hup); signal(SIGPIPE, SIG_IGN);
  unsigned char tmp[65536]; uint64_t next_stats = now_ns() + 1000000000ULL;
  for (;;) {
    if (reload) { reload = 0; load(argv[2]); }
    uint64_t t = now_ns(); int to = 1000;
    while (hn && heap[0].t_ns <= t) { pkt_t p = hpop(); if (write(fd, p.buf, p.len) < 0) {} free(p.buf); D[p.seq >> 31].out++; }
    if (hn) { uint64_t dt = heap[0].t_ns - t; to = (int)(dt / 1000000ULL); }
    if (t >= next_stats) { FILE *s = fopen(argv[3], "w"); if (s) { for (int i = 0; i < 2; i++) fprintf(s, "%s in %llu out %llu drop_loss %llu drop_outage %llu drop_limit %llu drop_tuple %llu\n", i ? "to_near" : "to_far",
          (unsigned long long)D[i].in, (unsigned long long)D[i].out, (unsigned long long)D[i].drop_loss, (unsigned long long)D[i].drop_out, (unsigned long long)D[i].drop_limit, (unsigned long long)D[i].drop_tuple);
          fprintf(s, "queued %ld tuples %llu shaped %llu\n", hn, (unsigned long long)tuple_active, (unsigned long long)tuple_shaped); fclose(s); }
      next_stats = t + 1000000000ULL; }
    struct pollfd pf = { fd, POLLIN, 0 }; int r = poll(&pf, 1, to > 1000 ? 1000 : to);
    if (r <= 0) continue;
    for (int burst = 0; burst < 256; burst++) {
      ssize_t n = read(fd, tmp, sizeof tmp); if (n <= 0) break;
      if (n < 20 || (tmp[0] >> 4) != 4) continue;                       /* IPv4 only */
      int di = (tmp[16] == 10 && tmp[17] == 200 && tmp[18] == 1) ? 1 : 0; /* dst 10.200.1.x -> to near */
      dir_t *d = &D[di]; uint64_t tn = now_ns(); d->in++;
      if (lost(d, tn)) continue;
      /* drop_synack=N: the first N TCP SYN-ACKs towards near are lost (handshake retransmit). */
      if (di == 1 && C.drop_synack > 0 && tmp[9] == 6) { int ihl = (tmp[0] & 15) * 4;
        if (n > ihl + 13 && (tmp[ihl + 13] & 0x12) == 0x12) { C.drop_synack--; d->drop_loss++; continue; } }
      if (hn >= C.limit) { d->drop_limit++; continue; }
      /* The per-tuple shaper and the shared link FIFO are independent: a packet
         waiting on its own bucket must not occupy the shared rate for others. */
      uint64_t ready = tuple_ready(tmp, (int)n, tn);
      double ser = n * 8.0 / (C.rate_mbit * 1e6) * 1e9;
      if (d->tx_free_ns < (double)tn) d->tx_free_ns = (double)tn;
      d->tx_free_ns += ser;
      double rel = (d->tx_free_ns > (double)ready ? d->tx_free_ns : (double)ready)
                   + C.owd_ms * 1e6 + (C.jitter_ms > 0 ? nrm() * C.jitter_ms * 1e6 : 0);
      if (rel < tn) rel = tn;
      pkt_t p; p.t_ns = (uint64_t)rel; p.len = (uint32_t)n; p.seq = (uint32_t)((seqc++ & 0x7FFFFFFF) | ((uint32_t)di << 31)); p.buf = malloc(n); memcpy(p.buf, tmp, n); hpush(p);
    }
  }
}
