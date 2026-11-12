/* wanem.c - user-space WAN emulator for the TBTun v2 testbed, used when the kernel has no sch_netem
 * (e.g. minimal container kernels).  One TUN device in the router namespace; policy routing sends every
 * forwarded packet from the two veths into the TUN; wanem delays / drops / rate-limits it and writes it
 * back; the kernel then forwards it to its destination (packets arriving from the TUN use the main table).
 *
 * Per direction (keyed by destination IPv4: 10.200.1.0/24 = "to near" (downlink), else "to far" (uplink)):
 *   release = max(now, tx_free) + len*8/rate  (serialisation, FIFO)  ->  + OWD + jitter
 *   loss:  iid p, or Gilbert-Elliott (p, r, 1-h, 1-k), plus optional periodic full outage.
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

typedef struct { double owd_ms, jitter_ms, rate_mbit, loss, ge_p, ge_r, ge_1h, ge_1k, out_ms, out_every_s; long limit; uint64_t seed; long drop_synack; } cfg_t;
typedef struct { uint64_t t_ns; uint32_t len; uint32_t seq; unsigned char *buf; } pkt_t;
typedef struct { double tx_free_ns; int ge_bad; uint64_t in, out, drop_loss, drop_out, drop_limit; } dir_t;

static cfg_t C; static dir_t D[2]; static volatile sig_atomic_t reload = 1;
static pkt_t *heap; static long hn, hcap; static uint64_t rng, seqc;
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
static void load(const char *path) {
  cfg_t c = { 200, 0, 1000, 0, 0, 0, 0, 0, 0, 0, 100000, 1, 0 };
  FILE *f = fopen(path, "r"); char k[64]; double v;
  if (f) { while (fscanf(f, " %63[^=]=%lf", k, &v) == 2) {
      if (!strcmp(k, "owd_ms")) c.owd_ms = v; else if (!strcmp(k, "jitter_ms")) c.jitter_ms = v; else if (!strcmp(k, "rate_mbit")) c.rate_mbit = v;
      else if (!strcmp(k, "loss")) c.loss = v; else if (!strcmp(k, "ge_p")) c.ge_p = v; else if (!strcmp(k, "ge_r")) c.ge_r = v;
      else if (!strcmp(k, "ge_1h")) c.ge_1h = v; else if (!strcmp(k, "ge_1k")) c.ge_1k = v; else if (!strcmp(k, "outage_ms")) c.out_ms = v;
      else if (!strcmp(k, "outage_every_s")) c.out_every_s = v; else if (!strcmp(k, "limit")) c.limit = (long)v; else if (!strcmp(k, "seed")) c.seed = (uint64_t)v;
      else if (!strcmp(k, "drop_synack")) c.drop_synack = (long)v; }
    fclose(f); }
  C = c; rng = c.seed * 0x2545F4914F6CDD1DULL;
  fprintf(stderr, "wanem: owd %.1f ms jitter %.1f rate %.0f Mbit loss %.4f ge(%.3f,%.3f,%.3f,%.3f) outage %.0f ms/%.1f s limit %ld seed %llu\n",
          C.owd_ms, C.jitter_ms, C.rate_mbit, C.loss, C.ge_p, C.ge_r, C.ge_1h, C.ge_1k, C.out_ms, C.out_every_s, C.limit, (unsigned long long)C.seed);
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
    if (t >= next_stats) { FILE *s = fopen(argv[3], "w"); if (s) { for (int i = 0; i < 2; i++) fprintf(s, "%s in %llu out %llu drop_loss %llu drop_outage %llu drop_limit %llu\n", i ? "to_near" : "to_far",
          (unsigned long long)D[i].in, (unsigned long long)D[i].out, (unsigned long long)D[i].drop_loss, (unsigned long long)D[i].drop_out, (unsigned long long)D[i].drop_limit); fprintf(s, "queued %ld\n", hn); fclose(s); }
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
      double ser = n * 8.0 / (C.rate_mbit * 1e6) * 1e9; if (d->tx_free_ns < tn) d->tx_free_ns = tn; d->tx_free_ns += ser;
      double rel = d->tx_free_ns + C.owd_ms * 1e6 + (C.jitter_ms > 0 ? nrm() * C.jitter_ms * 1e6 : 0);
      if (rel < tn) rel = tn;
      pkt_t p; p.t_ns = (uint64_t)rel; p.len = (uint32_t)n; p.seq = (uint32_t)((seqc++ & 0x7FFFFFFF) | ((uint32_t)di << 31)); p.buf = malloc(n); memcpy(p.buf, tmp, n); hpush(p);
    }
  }
}
