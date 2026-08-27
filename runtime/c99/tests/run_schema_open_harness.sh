#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
if [[ $# -lt 1 ]]; then
  echo "usage: run_schema_open_harness.sh (--pack corpus.scorp | --image case-id image.scimg)" >&2
  exit 2
fi
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cc -std=c99 -Wall -Wextra -Werror -O2 \
  -Iruntime/c99/include \
  runtime/c99/tests/schema_open_harness.c \
  -o "$work/schema_open_harness"
"$work/schema_open_harness" "$@"
