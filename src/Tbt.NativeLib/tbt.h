/* C ABI for libtbt.so (NativeAOT shared library, linux-bionic-arm64 or host).
 * Spec chapter 14: tbt_start / tbt_stop / tbt_stats, plus a protect(fd) callback
 * so the VpnService host can keep path sockets out of its own TUN.
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
 * outbound path socket, from the library's worker thread. */
typedef int32_t (*tbt_protect_fn)(int32_t fd, void *ctx);

enum {
    TBT_CONFIG_VERSION = 1,
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
    TBT_ERR_NOT_STARTED = -5
};

typedef struct tbt_config {
    int32_t version;          /* TBT_CONFIG_VERSION */
    int32_t flags;            /* TBT_FLAG_* */
    int32_t local_port;       /* userspace TCP listen port; 0 → 8443 */
    uint32_t local_ipv4;      /* host-order IPv4; 0 → 10.200.3.2 */
    int32_t window_clamp;     /* 0 → default 600000 */
    int32_t max_connections;  /* 0 → 64 */
    tbt_protect_fn protect;   /* nullable */
    void *protect_ctx;        /* nullable */
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
