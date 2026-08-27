#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
trap 'rm -f test_activation_runtime.exe activation_runtime.o' EXIT

common_flags=(-std=c99 -Wall -Wextra -Werror -O2 -I../include)

gcc "${common_flags[@]}" -fsyntax-only test_activation_runtime.c
gcc "${common_flags[@]}" -c ../src/signal_candy_runtime.c \
    -o activation_runtime.o

if nm -u activation_runtime.o | grep -E '[[:space:]](malloc|calloc|realloc|free)$' >/dev/null; then
    echo "activation runtime must not reference a heap allocator" >&2
    exit 1
fi

if nm activation_runtime.o | awk 'NF >= 3 && $2 ~ /^[BbCcDd]$/ && $3 !~ /^\.(bss|data)$/ { print; found = 1 } END { exit !found }' >/dev/null; then
    echo "activation runtime must not contain mutable static storage" >&2
    exit 1
fi

gcc "${common_flags[@]}" activation_runtime.o test_activation_runtime.c \
    -o test_activation_runtime.exe
./test_activation_runtime.exe
