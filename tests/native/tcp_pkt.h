/* Minimal IPv4/TCP builder and parser for the host-side capture tests. */
#ifndef TCP_PKT_H
#define TCP_PKT_H
#ifndef _DEFAULT_SOURCE
#define _DEFAULT_SOURCE
#endif
#include <stdint.h>
#include <string.h>
#include <arpa/inet.h>

#define TCP_FIN 0x01
#define TCP_SYN 0x02
#define TCP_RST 0x04
#define TCP_PSH 0x08
#define TCP_ACK 0x10

static uint16_t pkt_fold(uint32_t s)
{
    while (s >> 16)
        s = (s & 0xFFFF) + (s >> 16);
    return (uint16_t)~s;
}

static uint32_t pkt_sum(const uint8_t *p, int n)
{
    uint32_t s = 0;
    int i = 0;
    for (; i + 1 < n; i += 2)
        s += ((uint32_t)p[i] << 8) | p[i + 1];
    if (i < n)
        s += (uint32_t)p[i] << 8;
    return s;
}

static int pkt_build(uint8_t *p, uint32_t src, uint32_t dst, uint16_t sport, uint16_t dport,
                     uint32_t seq, uint32_t ack, uint8_t flags, uint16_t window,
                     const uint8_t *opts, int optlen, const uint8_t *payload, int paylen,
                     uint16_t ipid)
{
    int tcp = 20 + optlen + paylen;
    int total = 20 + tcp;
    memset(p, 0, (size_t)total);
    p[0] = 0x45;
    p[2] = (uint8_t)(total >> 8);
    p[3] = (uint8_t)total;
    p[4] = (uint8_t)(ipid >> 8);
    p[5] = (uint8_t)ipid;
    p[6] = 0x40; /* DF */
    p[8] = 64;
    p[9] = 6;
    uint32_t src_n = htonl(src), dst_n = htonl(dst);
    memcpy(p + 12, &src_n, 4);
    memcpy(p + 16, &dst_n, 4);
    uint16_t ipc = pkt_fold(pkt_sum(p, 20));
    p[10] = (uint8_t)(ipc >> 8);
    p[11] = (uint8_t)ipc;
    uint8_t *t = p + 20;
    t[0] = (uint8_t)(sport >> 8);
    t[1] = (uint8_t)sport;
    t[2] = (uint8_t)(dport >> 8);
    t[3] = (uint8_t)dport;
    uint32_t seq_n = htonl(seq), ack_n = htonl(ack);
    memcpy(t + 4, &seq_n, 4);
    memcpy(t + 8, &ack_n, 4);
    t[12] = (uint8_t)(((20 + optlen) / 4) << 4);
    t[13] = flags;
    t[14] = (uint8_t)(window >> 8);
    t[15] = (uint8_t)window;
    if (optlen)
        memcpy(t + 20, opts, (size_t)optlen);
    if (paylen)
        memcpy(t + 20 + optlen, payload, (size_t)paylen);
    uint32_t pseudo = (src >> 16) + (src & 0xFFFF) + (dst >> 16) + (dst & 0xFFFF) + 6 + (uint32_t)tcp;
    uint16_t tc = pkt_fold(pseudo + pkt_sum(t, tcp));
    t[16] = (uint8_t)(tc >> 8);
    t[17] = (uint8_t)tc;
    return total;
}

struct pkt_seg {
    uint32_t src, dst, seq, ack;
    uint16_t sport, dport, window;
    uint8_t flags;
    const uint8_t *payload;
    int paylen;
};

static int pkt_parse(const uint8_t *p, int n, struct pkt_seg *s)
{
    if (n < 40 || (p[0] >> 4) != 4 || p[9] != 6)
        return -1;
    int ihl = (p[0] & 15) * 4;
    int total = (p[2] << 8) | p[3];
    if (ihl < 20 || total > n || total < ihl + 20)
        return -1;
    const uint8_t *t = p + ihl;
    int off = (t[12] >> 4) * 4;
    int tcp = total - ihl;
    if (off < 20 || off > tcp)
        return -1;
    uint32_t src, dst;
    memcpy(&src, p + 12, 4);
    memcpy(&dst, p + 16, 4);
    s->src = ntohl(src);
    s->dst = ntohl(dst);
    s->sport = (uint16_t)((t[0] << 8) | t[1]);
    s->dport = (uint16_t)((t[2] << 8) | t[3]);
    uint32_t seq, ack;
    memcpy(&seq, t + 4, 4);
    memcpy(&ack, t + 8, 4);
    s->seq = ntohl(seq);
    s->ack = ntohl(ack);
    s->flags = t[13];
    s->window = (uint16_t)((t[14] << 8) | t[15]);
    s->payload = t + off;
    s->paylen = tcp - off;
    return 0;
}
#endif
