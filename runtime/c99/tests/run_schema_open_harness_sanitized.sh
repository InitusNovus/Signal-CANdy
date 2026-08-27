#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
if [[ $# -lt 1 ]]; then
  echo "usage: run_schema_open_harness_sanitized.sh (--pack corpus.scorp | --image case-id image.scimg)" >&2
  exit 2
fi
llvm_runtime='/c/Program Files/LLVM/lib/clang/22/lib/windows'
if [[ -d "$llvm_runtime" ]]; then
  export PATH="$llvm_runtime:$PATH"
fi
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
clang -std=c99 -Wall -Wextra -Werror -O1 -g \
  -fno-omit-frame-pointer -fsanitize=address,undefined \
  -Iruntime/c99/include \
  runtime/c99/tests/schema_open_harness.c \
  -o "$work/schema_open_harness_san"
ASAN_OPTIONS=abort_on_error=1:halt_on_error=1 \
UBSAN_OPTIONS=halt_on_error=1:print_stacktrace=1 \
  "$work/schema_open_harness_san" "$@"
