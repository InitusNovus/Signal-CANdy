#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "usage: run_p0_aot_scimg_diff.sh <generated-dir> <image.scimg> <output-dir>" >&2
    exit 2
fi

generated_dir=$(cd "$1" && pwd)
image_path=$(cd "$(dirname "$2")" && pwd)/$(basename "$2")
mkdir -p "$3"
output_dir=$(cd "$3" && pwd)
script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
runtime_dir=$(cd "$script_dir/.." && pwd)
compiler=${CC:-gcc}
executable="$output_dir/p0_aot_scimg_diff"

"$compiler" -std=c99 -Wall -Wextra -Werror -O2 \
    -I"$runtime_dir/include" -I"$generated_dir/include" \
    "$runtime_dir/src/signal_candy_runtime.c" \
    "$generated_dir"/src/*.c "$script_dir/p0_aot_scimg_diff_harness.c" \
    -lm -o "$executable"

"$executable" "$image_path"
