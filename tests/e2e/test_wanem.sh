#!/usr/bin/env bash
# Offline unit tests for wanem v2 scheduling (no TUN, no netns, no lock).
# Reproduces the review 2-flow example: A2/B2 must not release simultaneously.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=$(mktemp)
trap 'rm -f "$out"' EXIT
gcc -O2 -Wall -Wextra -Wno-unused-function -DWANEM_SCHED_TEST -o "$out" "$here/wanem.c" -lm
"$out"
