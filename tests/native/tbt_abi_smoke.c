#define _DEFAULT_SOURCE
/* Host-side smoke of the C ABI in tbt.h, against a linux-x64 NativeAOT
 * libtbt.so. A SOCK_DGRAM socketpair stands in for the TUN fd. */
#include "tbt.h"

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

int main(void)
{
    int sv[2];
    tbt_config cfg;
    char buf[4096];
    unsigned char pkt[64];
    int rc, n, i, saw_pkt = 0, saw_protect = 0;
    ssize_t w;

    if (socketpair(AF_UNIX, SOCK_DGRAM, 0, sv) != 0) {
        perror("socketpair");
        return 1;
    }
    if (set_nonblock(sv[0]) < 0 || set_nonblock(sv[1]) < 0) {
        perror("fcntl");
        return 1;
    }

    memset(&cfg, 0, sizeof cfg);
    cfg.version = TBT_CONFIG_VERSION;
    cfg.flags = TBT_FLAG_SMOKE;
    cfg.protect = on_protect;

    rc = tbt_start(sv[0], &cfg);
    if (rc != 0) {
        fprintf(stderr, "tbt_start failed: %d\n", rc);
        return 2;
    }

    /* Any 64 bytes are a "packet" to TunStack.Receive (parse may fail). */
    memset(pkt, 0, sizeof pkt);
    pkt[0] = 0x45; /* a lookalike IPv4 header so dumps are less surprising */
    w = write(sv[1], pkt, sizeof pkt);
    if (w < 0) {
        perror("write");
        tbt_stop();
        return 3;
    }

    /* Wait for the worker to receive and refresh stats. */
    for (i = 0; i < 50; i++) {
        usleep(20 * 1000);
        n = tbt_stats(buf, (int32_t)sizeof buf);
        if (n < 0) {
            fprintf(stderr, "tbt_stats failed: %d\n", n);
            tbt_stop();
            return 4;
        }
        if (strstr(buf, "pkts_in=") && strstr(buf, "pkts_in=0") == NULL)
            saw_pkt = 1;
        if (strstr(buf, "protect_calls=") && strstr(buf, "protect_calls=0") == NULL)
            saw_protect = 1;
        if (saw_pkt && saw_protect)
            break;
    }

    printf("stats: %s\n", buf);
    printf("protect_hits=%d saw_pkt=%d saw_protect=%d\n",
           protect_hits, saw_pkt, saw_protect);

    tbt_stop();
    close(sv[0]);
    close(sv[1]);

    if (!saw_protect || protect_hits < 1) {
        fprintf(stderr, "protect callback was not invoked\n");
        return 5;
    }
    if (!saw_pkt) {
        fprintf(stderr, "TunStack did not count the fake TUN packet\n");
        return 6;
    }
    if (strstr(buf, "tun_fd=") == NULL || strstr(buf, "state=running") == NULL) {
        fprintf(stderr, "stats missing tun_fd / state=running\n");
        return 7;
    }
    puts("tbt_abi_smoke: ok");
    return 0;
}
