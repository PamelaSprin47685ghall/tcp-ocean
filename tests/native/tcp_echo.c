#define _DEFAULT_SOURCE
#include <errno.h>
#include <netinet/in.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    int port = argc > 1 ? atoi(argv[1]) : 0;
    int l = socket(AF_INET, SOCK_STREAM, 0);
    int yes = 1;
    if (l < 0) { perror("socket"); return 1; }
    setsockopt(l, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof yes);
    struct sockaddr_in a;
    memset(&a, 0, sizeof a);
    a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    a.sin_port = htons((uint16_t)port);
    if (bind(l, (struct sockaddr *)&a, sizeof a) != 0) { perror("bind"); return 1; }
    socklen_t alen = sizeof a;
    if (getsockname(l, (struct sockaddr *)&a, &alen) != 0) { perror("getsockname"); return 1; }
    if (listen(l, 16) != 0) { perror("listen"); return 1; }
    printf("%u\n", (unsigned)ntohs(a.sin_port));
    fflush(stdout);
    for (;;) {
        int c = accept(l, NULL, NULL);
        if (c < 0) {
            if (errno == EINTR) continue;
            perror("accept");
            return 1;
        }
        char buf[4096];
        ssize_t n;
        while ((n = read(c, buf, sizeof buf)) > 0) {
            ssize_t off = 0;
            while (off < n) {
                ssize_t w = write(c, buf + off, (size_t)(n - off));
                if (w <= 0) break;
                off += w;
            }
        }
        close(c);
    }
}
