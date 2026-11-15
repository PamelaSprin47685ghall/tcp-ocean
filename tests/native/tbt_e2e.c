#define _DEFAULT_SOURCE
#include "tbt.h"
#include "tcp_pkt.h"

#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

static int protect_hits;

static int32_t on_protect(int32_t fd, void *ctx)
{
    (void)ctx;
    protect_hits++;
    fprintf(stderr, "protect(fd=%d) hit=%d\n", fd, protect_hits);
    return 1;
}

static int set_nonblock(int fd)
{
    int fl = fcntl(fd, F_GETFL, 0);
    if (fl < 0) return -1;
    return fcntl(fd, F_SETFL, fl | O_NONBLOCK);
}

static int read_pkt(int fd, uint8_t *buf, int cap, int timeout_ms)
{
    int waited = 0;
    while (waited < timeout_ms) {
        ssize_t n = read(fd, buf, (size_t)cap);
        if (n > 0)
            return (int)n;
        if (n < 0 && errno != EAGAIN && errno != EWOULDBLOCK)
            return -1;
        usleep(10 * 1000);
        waited += 10;
    }
    return 0;
}

static uint32_t ip4(const char *s)
{
    unsigned a, b, c, d;
    if (sscanf(s, "%u.%u.%u.%u", &a, &b, &c, &d) != 4)
        return 0;
    return (a << 24) | (b << 16) | (c << 8) | d;
}

int main(int argc, char **argv)
{
    if (argc < 4) {
        fprintf(stderr, "usage: tbt_e2e SERVER_IP SERVER_PORT PSK_HEX\n");
        return 1;
    }
    uint32_t server_ip = ip4(argv[1]);
    int server_port = atoi(argv[2]);
    const char *psk_hex = argv[3];
    if (strlen(psk_hex) != 64) {
        fprintf(stderr, "psk must be 64 hex digits\n");
        return 1;
    }
    uint8_t psk[32];
    for (int i = 0; i < 32; i++) {
        unsigned v;
        if (sscanf(psk_hex + 2 * i, "%2x", &v) != 1) {
            fprintf(stderr, "bad psk hex\n");
            return 1;
        }
        psk[i] = (uint8_t)v;
    }

    int sv[2];
    if (socketpair(AF_UNIX, SOCK_DGRAM, 0, sv) != 0) {
        perror("socketpair");
        return 1;
    }
    if (set_nonblock(sv[0]) < 0 || set_nonblock(sv[1]) < 0) {
        perror("fcntl");
        return 1;
    }

    tbt_config cfg;
    memset(&cfg, 0, sizeof cfg);
    cfg.version = TBT_CONFIG_VERSION;
    cfg.protect = on_protect;
    cfg.server_ipv4 = server_ip;
    cfg.server_port = server_port;
    cfg.psk = psk;
    cfg.psk_len = 32;
    cfg.path_count = 1;
    cfg.mss = 1448;

    int rc = tbt_start(sv[0], &cfg);
    if (rc != 0) {
        fprintf(stderr, "tbt_start failed: %d\n", rc);
        return 2;
    }

    /* Wait until the session is up before injecting a captured SYN. */
    char stats[4096];
    int up = 0;
    for (int i = 0; i < 100; i++) {
        int n = tbt_stats(stats, (int)sizeof stats);
        if (n > 0 && strstr(stats, "state=running") && strstr(stats, "paths=")) {
            up = 1;
            break;
        }
        usleep(20 * 1000);
    }
    if (!up) {
        fprintf(stderr, "session did not come up: %s\n", stats);
        tbt_stop();
        return 3;
    }
    fprintf(stderr, "session up: %s\n", stats);
    if (protect_hits < 1) {
        fprintf(stderr, "protect was not called on the path socket\n");
        tbt_stop();
        return 4;
    }

    const uint32_t src = ip4("10.200.3.2");
    const uint32_t dst = ip4("10.200.3.1");
    const uint16_t sport = 40000, dport = 80;
    const uint32_t iss = 1000;
    uint8_t mssopt[4] = { 2, 4, 0x05, 0xb4 };
    uint8_t pkt[2048], rbuf[2048];
    int plen = pkt_build(pkt, src, dst, sport, dport, iss, 0, TCP_SYN, 65535,
                         mssopt, 4, NULL, 0, 1);
    if (write(sv[1], pkt, (size_t)plen) != plen) {
        perror("write syn");
        tbt_stop();
        return 5;
    }

    uint32_t irs = 0;
    int got_synack = 0;
    for (int i = 0; i < 100; i++) {
        int n = read_pkt(sv[1], rbuf, (int)sizeof rbuf, 50);
        if (n <= 0)
            continue;
        struct pkt_seg s;
        if (pkt_parse(rbuf, n, &s) != 0)
            continue;
        if ((s.flags & TCP_SYN) && (s.flags & TCP_ACK) && s.ack == iss + 1
            && s.dst == src && s.dport == sport) {
            irs = s.seq;
            got_synack = 1;
            break;
        }
    }
    if (!got_synack) {
        tbt_stats(stats, (int)sizeof stats);
        fprintf(stderr, "no SYN-ACK; stats: %s\n", stats);
        tbt_stop();
        return 6;
    }

    plen = pkt_build(pkt, src, dst, sport, dport, iss + 1, irs + 1, TCP_ACK, 65535,
                     NULL, 0, NULL, 0, 2);
    if (write(sv[1], pkt, (size_t)plen) != plen) {
        perror("write ack");
        tbt_stop();
        return 7;
    }

    const char *msg = "hello\n";
    plen = pkt_build(pkt, src, dst, sport, dport, iss + 1, irs + 1,
                     TCP_ACK | TCP_PSH, 65535, NULL, 0,
                     (const uint8_t *)msg, (int)strlen(msg), 3);
    if (write(sv[1], pkt, (size_t)plen) != plen) {
        perror("write data");
        tbt_stop();
        return 8;
    }

    char collected[256];
    int got = 0;
    uint32_t nxt = iss + 1 + (uint32_t)strlen(msg);
    for (int i = 0; i < 200 && got < (int)strlen(msg); i++) {
        int n = read_pkt(sv[1], rbuf, (int)sizeof rbuf, 50);
        if (n <= 0)
            continue;
        struct pkt_seg s;
        if (pkt_parse(rbuf, n, &s) != 0)
            continue;
        if (s.paylen > 0 && s.dst == src && s.dport == sport) {
            int take = s.paylen;
            if (got + take > (int)sizeof collected)
                take = (int)sizeof collected - got;
            memcpy(collected + got, s.payload, (size_t)take);
            got += take;
            /* ACK what we received */
            uint32_t peer_nxt = s.seq + (uint32_t)s.paylen;
            plen = pkt_build(pkt, src, dst, sport, dport, nxt, peer_nxt, TCP_ACK, 65535,
                             NULL, 0, NULL, 0, 4);
            write(sv[1], pkt, (size_t)plen);
        }
    }

    tbt_stats(stats, (int)sizeof stats);
    fprintf(stderr, "stats: %s\n", stats);
    fprintf(stderr, "protect_hits=%d echoed=%d\n", protect_hits, got);
    if (got >= (int)strlen(msg) && memcmp(collected, msg, strlen(msg)) == 0) {
        tbt_stop();
        puts("tbt_e2e: ok");
        return 0;
    }
    fprintf(stderr, "echo mismatch (got %d bytes)\n", got);
    if (got > 0)
        fwrite(collected, 1, (size_t)got, stderr);
    tbt_stop();
    return 9;
}
