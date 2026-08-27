#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fixture="$repo_root/examples/scimg_activation_demo"
output="$fixture/test_activation_demo.exe"
trap 'rm -f "$output"' EXIT

rm -rf "$fixture/build"
dotnet run --no-build --project "$repo_root/src/Signal.CANdy.CLI" -c Release -- \
    project build "$fixture/project_a.yaml"
dotnet run --no-build --project "$repo_root/src/Signal.CANdy.CLI" -c Release -- \
    project build "$fixture/project_b.yaml"

gcc -std=c99 -Wall -Wextra -Werror \
    -I"$repo_root/runtime/c99/include" -I"$fixture/build" \
    "$repo_root/runtime/c99/src/signal_candy_runtime.c" \
    "$fixture/test_activation_demo.c" -o "$output"
"$output"
