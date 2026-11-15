/* C ABI for libtbt.so (NativeAOT shared library, linux-bionic-arm64 or host).
 * Spec chapter 14: tbt_start / tbt_stop / tbt_stats, plus a protect(fd) callback
 * so the VpnService host can keep path sockets out of its own TUN.
 *
 * tbt_start with server_port and a 32-byte PSK opens a real client session:
 * TCP flows captured on the TUN fd are carried over TBTun paths to that server.
 * protect(fd) is invoked on every path socket before connect(2). This entry is
 * independent of the default `tbtun client` (which still only listens on a
 * local kernel TCP port).
 *
 * The host owns the TUN file descriptor for its lifetime. tbt_stop does not close it.
 * All functions are thread-safe with respect to each other. tbt_start may be called
 * again only after tbt_stop has returned.
 */
#ifndef TBT_H
#define TBT_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Return 1 if the socket may be connected, 0 to abort. ctx is the pointer
 * given to tbt_start / tbt_set_protect. Called before connect(2) on every
 * outbound path socket. */
typedef int32_t (*tbt_protect_fn)(int32_t fd, void *ctx);

enum {
    TBT_CONFIG_VERSION = 2,
    /* Do not open a session; attach the TUN fd, run the receive loop, and
     * exercise the protect callback once on a throwaway socket. */
    TBT_FLAG_SMOKE = 1
};

/* Error codes from tbt_start / tbt_set_protect. */
enum {
    TBT_OK = 0,
    TBT_ERR_ARGS = -1,
    TBT_ERR_BUSY = -2,
    TBT_ERR_ATTACH = -3,
    TBT_ERR_THREAD = -4,
    TBT_ERR_NOT_STARTED = -5,
    TBT_ERR_HANDSHAKE = -6,
    TBT_ERR_PROTECT = -7
};

typedef struct tbt_config {
    int32_t version;          /* TBT_CONFIG_VERSION (1 = attach/smoke only) */
    int32_t flags;            /* TBT_FLAG_* */
    int32_t local_port;       /* unused by the capture client; kept for v1 */
    uint32_t local_ipv4;      /* host-order IPv4; 0 → 10.200.3.2 (stats / v1) */
    int32_t window_clamp;     /* 0 → default 600000 */
    int32_t max_connections;  /* 0 → 64 captured flows */
    tbt_protect_fn protect;   /* nullable; required on Android before connect */
    void *protect_ctx;        /* nullable */
    /* v2: real client session. Ignored when version==1 or TBT_FLAG_SMOKE. */
    uint32_t server_ipv4;     /* host-order IPv4 of the TBTun server */
    int32_t server_port;      /* >0 enables the session */
    const uint8_t *psk;       /* 32-byte pre-shared key */
    int32_t psk_len;          /* must be 32 when psk != NULL */
    int32_t path_count;       /* 0 → 1 */
    int32_t mss;              /* 0 → 1448 */
    const char *host;         /* HTTP Host header; NULL → server dotted address */
} tbt_config;

int32_t tbt_start(int32_t tun_fd, const tbt_config *cfg);
void    tbt_stop(void);
/* Write a UTF-8, NUL-terminated stats line into buf. Returns the number of
 * bytes written excluding the NUL, or a negative TBT_ERR_* code. */
int32_t tbt_stats(char *buf, int32_t len);
/* Replace the protect callback (also accepted in tbt_config). */
int32_t tbt_set_protect(tbt_protect_fn fn, void *ctx);

#ifdef __cplusplus
}
#endif

#endif
