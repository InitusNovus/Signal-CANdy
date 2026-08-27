#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
trap 'rm -f test_quality_runtime.exe' EXIT
gcc -std=c99 -Wall -Wextra -Werror -O2 -I../include \
    ../src/signal_candy_runtime.c test_quality_runtime.c \
    -o test_quality_runtime.exe
./test_quality_runtime.exe
