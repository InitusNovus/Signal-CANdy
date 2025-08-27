# Signal CANdy — DBC to C Code Generator (F#)

[![CI](https://github.com/InitusNovus/Signal-CANdy/actions/workflows/ci.yml/badge.svg)](https://github.com/InitusNovus/Signal-CANdy/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/InitusNovus/Signal-CANdy.svg)](https://github.com/InitusNovus/Signal-CANdy/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![F#](https://img.shields.io/badge/F%23-language-blue.svg)](https://fsharp.org/)
[![Version](https://img.shields.io/github/v/release/InitusNovus/Signal-CANdy?include_prereleases)](https://github.com/InitusNovus/Signal-CANdy/releases)

[![C99](https://img.shields.io/badge/C-99-blue.svg)](https://en.wikipedia.org/wiki/C99)
[![CAN DBC](https://img.shields.io/badge/protocol-CAN%20DBC-green.svg)](https://en.wikipedia.org/wiki/CAN_bus)

Languages: This README is in English. For Korean, see README.ko.md.

This project generates portable C99 parser modules (headers/sources) from a `.dbc` file using an F# code generator.

## 📦 NuGet Packages

- SignalCandy.Core — Core F# library (parsing, config, codegen)
- SignalCandy — C#-friendly facade over the Core

Install:

```pwsh
dotnet add package SignalCandy.Core --version 0.2.1
dotnet add package SignalCandy --version 0.2.1
```

## ⚡ Quick Start (5 minutes)

1) Check prerequisites

```bash
dotnet --version   # 8.0+
gcc --version      # C compiler (optional: for local validation)
make --version     # GNU Make (optional: for local validation)
```

2) Generate C code from a sample DBC

```bash
dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen
```

3) Build and run a basic test

```bash
make -C gen build
./gen/build/test_runner test_roundtrip
```

Expected: roundtrip test passes and generated headers appear under `gen/include`.

Note: Step 3 is optional but recommended for validation. The generated C files under `gen/include` and `gen/src` are ready for integration into your firmware project with any compatible C99 toolchain.

## 📋 Table of Contents

- Getting Started
- Usage
- Supported features
- Configuration (overview)
- How code is generated
- Large-scale testing and stress suite
- Multiplexed messages
- Value tables (VAL_)
- Output layout and naming
- Including generated files in firmware
- Build system examples
- Platforms, compilers, and test environments
- Runtime usage examples
- PhysType details and numeric precision
- Dispatch modes and registry
- Endianness and bit utilities
- Project Structure
- Performance benchmarks
- Troubleshooting
- Limitations
- License, third-party, and AI provenance

## Getting Started

### Prerequisites

- .NET SDK 8.0 or later
- gcc or clang (optional: for validating generated C code; unrelated to your target firmware toolchain)
- make (optional: for validating generated C code; unrelated to your target firmware toolchain)

### Usage

1.  **Generate C code from a DBC file:**

    ```bash
    dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen
    ```

    This command will parse `examples/sample.dbc` and generate C header and source files into the `gen/` directory.

2.  **Build the generated C code:**

    ```bash
    make -C gen build
    ```

    This command compiles the C code generated in the `gen/` directory.

3.  **Run tests for the generated C code:**

    ```bash
    make -C gen test
    ```

    This command executes the tests for the generated C code.

## Supported features

- Endianness: Little-Endian (Intel-style) and Big-Endian (Motorola-style), supporting both Motorola MSB and LSB start-bit conventions
- Multiplexing: one switch (M) and branch signals (m<k>) with valid bitmask and mux_active enum
- Value tables: parse VAL_ and generate per-signal enums and to_string helpers
- Configurable scaling math: phys_type float or fixed with phys_mode selection
- Range checking: optional min/max checks in encode/decode
- Dispatch modes: binary_search or direct_map registry

## Configuration (config.yaml)

Optional file to control code generation behavior:

- phys_type: "float" | "fixed"
  - float: physical-value math using floating point intermediates
  - fixed: enable integer fast path when factor is a power of 10 (10^-n) and offset is integral
- phys_mode: "double" | "float" | "fixed_double" | "fixed_float"
  - double: when phys_type=float, use double intermediates for math (default)
  - float: when phys_type=float, use float intermediates for math
  - fixed_double: when phys_type=fixed, use double as fallback when fast path not applicable (default)
  - fixed_float: when phys_type=fixed, use float as fallback when fast path not applicable
  - Defaults (backward compatible):
    - If phys_type is omitted or "float" → phys_mode defaults to "double"
    - If phys_type is "fixed" → phys_mode defaults to "fixed_double"
- range_check: true | false
  - Enforce min/max bounds during encode/decode (rejects out-of-range)
- dispatch: "binary_search" | "direct_map"
  - Registry dispatch strategy for id → decode function
- motorola_start_bit: "msb" | "lsb"
  - Motorola big-endian start-bit convention used for codegen normalization
  - msb: treat DBC start bit as MSB-based sawtooth (default, common in many tools)
  - lsb: treat DBC start bit as LSB-based, generator converts to MSB sawtooth internally
- crc_counter_check: true | false
  - Reserved for future; hooks to validate CRC/counter signals (deferred)

Examples:

```yaml
# examples/config_range_check.yaml
phys_type: float
phys_mode: double     # default when omitted
range_check: true
dispatch: direct_map
crc_counter_check: false
```

```yaml
# examples/config_fixed.yaml (snake_case preferred)
phys_type: fixed
phys_mode: fixed_double  # default fallback; uses integer fast path when applicable
range_check: false
dispatch: direct_map
crc_counter_check: false
```

Note: Config keys use snake_case by default. For compatibility, PascalCase aliases are accepted for the same keys (e.g., PhysType ↔ phys_type). Matching is alias-based, not fully case-insensitive.

```yaml
# Single-precision FPU MCU (minimize double ops in fallback)
phys_type: fixed
phys_mode: fixed_float
range_check: true
dispatch: binary_search
crc_counter_check: false
```

```yaml
# Prefix common generated files to avoid name collisions
# yields: gen/include/sc_registry.h, gen/src/sc_registry.c, etc.
file_prefix: sc_
```

## CLI flags (overrides)

You can override some config fields from the command line:

- `--prefix <str>`: overrides `file_prefix` for generated common files.
- `--emit-main <true|false>`: controls copying `examples/main.c` to `gen/src/main.c`.

Examples

```bash
# Use prefix foo_ and skip copying main.c
dotnet run --project src/Generator -- \
  --dbc examples/sample.dbc \
  --out gen \
  --config examples/config.yaml \
  --prefix foo_ \
  --emit-main false
```

### Using a config

```bash
# with config
dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen --config examples/config_range_check.yaml
make -C gen build
./gen/build/test_runner test_range_check
```

Motorola LSB start-bit example

```bash
# generate with LSB convention (Motorola BE)
dotnet run --project src/Generator -- \
  --dbc examples/motorola_lsb_suite.dbc \
  --out gen \
  --config examples/config_motorola_lsb.yaml

make -C gen build
./gen/build/test_runner test_moto_lsb_basic
./gen/build/test_runner test_moto_lsb_roundtrip
```

## How code is generated from a DBC

1) Basic generation

```bash
# Binary search dispatch (default)
dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen
```

2) With a config file

```bash
# Choose phys type, dispatch strategy, range checks, etc.
dotnet run --project src/Generator -- --dbc <your>.dbc --out gen --config examples/config.yaml
```

3) Validate generated C builds

```bash
make -C gen build
```

Notes
- Codegen writes files under `gen/include` and `gen/src`.
- A sample test runner `gen/src/main.c` is emitted by copying `examples/main.c`. Do not ship or compile this in firmware; it exists only for local testing.

## Large-scale testing and stress suite

To validate at scale, this repo includes a stress test harness that repeatedly decodes/encodes frames and optionally benchmarks registry dispatch:

- One-off run after codegen/build:

  ```bash
  make -C gen build
  ./gen/build/test_runner test_stress_suite
  ```

- Bulk run across many DBC files (PowerShell):

  ```pwsh
  pwsh ./scripts/bulk_stress.ps1
  # Options
  # pwsh ./scripts/bulk_stress.ps1 -Config examples/config_directmap_fixed.yaml -OutDir gen
  ```

Guidance for expanding the corpus:
- Place permissively licensed public DBC files under `external_test/`.
- See `scripts/fetch_dbcs.md` for tips and sources. Respect licenses; do not commit proprietary files.
- The stress runner tolerates decode failures for random payloads and focuses on stability/throughput and decoder robustness.

Related docs
- Test overview and outcomes: see `TEST_SUMMARY.md`
- How to find public DBCs: `scripts/fetch_dbcs.md`

### Multiplexed messages

The generator supports DBC multiplexing (one switch `M`, and branch signals tagged `m<k>`):
- Decode: the switch is decoded first; only signals in the active branch are decoded. The struct exposes:
  - `uint32_t valid` bitmask macros per field (e.g., `MSG_VALID_SIGNAL`) to know which signals were decoded
  - `<Msg>_mux_e mux_active` enum with known branch values (e.g., `MSG_MUX_0`, `MSG_MUX_1`, ...)
- Encode: only signals belonging to the branch selected by the switch value are encoded.

Example
```bash
dotnet run --project src/Generator -- --dbc examples/multiplex_suite.dbc --out gen --config examples/config.yaml
make -C gen build
./gen/build/test_runner test_multiplex_roundtrip
```

Notes
- Branch selection uses the raw integer value of the switch signal (typical DBC semantics).
- Base (non-multiplexed) signals are always decoded/encoded.
 - Valid bitmask width: current implementations use a 32-bit `valid` field. Extremely large messages with >32 branch/base signals may require widening (e.g., to 64-bit) or an array. This is called out in Limitations; auto-widening is on the roadmap.

Using valid and mux_active
```c
#include "mux_msg.h"

void handle_mux(const uint8_t data[8]) {
  MUX_MSG_t m = {0};
  if (MUX_MSG_decode(&m, data, 8)) {
    if (m.mux_active == MUX_MSG_MUX_1) {
      if (m.valid & MUX_MSG_VALID_SIG_M1) {
        // use m.Sig_m1
      }
    } else if (m.mux_active == MUX_MSG_MUX_2) {
      if (m.valid & MUX_MSG_VALID_SIG_M2) {
        // use m.Sig_m2
      }
    }
    if (m.valid & MUX_MSG_VALID_BASE_8) {
      // base signal always available when decoded
    }
  }
}
```

### Value tables (VAL_)

Signals with `VAL_` mappings get C enums and to_string helpers:
- Header emits: `typedef enum { <MSG>_<SIG>_<NAME> = <value>, ... } <Msg>_<Sig>_e;`
- Source emits: `const char* <Msg>_<Sig>_to_string(int v);` returning the label or `"UNKNOWN"` if not mapped.

Example
```bash
dotnet run --project src/Generator -- --dbc examples/value_table.dbc --out gen --config examples/config_directmap_fixed.yaml
make -C gen build
./gen/build/test_runner test_value_table
```

Using enums and to_string
```c
#include "vt_msg.h"

void log_mode(int v) {
  const char* label = VT_MSG_Mode_to_string(v);
  // prints OFF/ON/AUTO or UNKNOWN
}

void compare_state(int v) {
  if (v == VT_MSG_STATE_RUN) {
    // matched via generated enum value
  }
}
```

### Output layout and naming
- gen/include/
  - sc_utils.h, sc_registry.h (prefix configurable via config: file_prefix)
  - <message>.h per message (snake_case filename)
- gen/src/
  - sc_utils.c, sc_registry.c (prefix configurable)
  - <message>.c per message (snake_case filename)
  - main.c (test runner; exclude in firmware builds)

Message API naming convention
- Type: `<MessageName>_t` (e.g., `MESSAGE_1_t`, `C2_MSG0280A1_BMS2VCU_Sts1_t`)
- Functions:
  - `bool <MessageName>_decode(<MessageName>_t* out, const uint8_t data[8], uint8_t dlc);`
  - `bool <MessageName>_encode(uint8_t data[8], uint8_t* out_dlc, const <MessageName>_t* in);`
- Registry (dispatch):
  - `bool decode_message(uint32_t can_id, const uint8_t data[8], uint8_t dlc, void* out_msg_struct);`
    - When `can_id` matches a known message, this fills the struct of the corresponding type pointed to by `out_msg_struct` and returns true.

Type-safety note (important)
- The registry API takes a `void*`. Passing the wrong struct type is undefined behavior. Prefer per-message calls when you know the type, or guard with a switch on ID:

```c
switch (can_id) {
  case MESSAGE_1_ID: {
    MESSAGE_1_t m = {0};
    if (MESSAGE_1_decode(&m, data, dlc)) { /* use m */ }
    break;
  }
  /* other IDs */
}
```

Behavior reference

- DLC semantics
  - encode: sets `*out_dlc` to the minimal required bytes for that message.
  - decode: returns false if `dlc` < required; extra bytes are ignored.
  - Planned: a config knob to select encode DLC policy (e.g., `encode_dlc_mode: minimal | fixed_8`).
- Range checks and atomicity
  - When enabled, out-of-range causes `false`. Operations are non-atomic by design: on failure, the output buffer/struct may be partially updated; callers must discard outputs when `false`.
  - Planned: optional atomic mode (all-or-nothing) if demand is strong.
- Overflow/truncation policy
  - Encode raw intermediate uses `int64_t`; decode uses `uint64_t` plus sign-fix for signed.
  - Without `range_check`, values exceeding bit width are masked/truncated to fit.
  - With `range_check`, out-of-range returns `false` before committing that signal.
  - Planned: a saturate-on-overflow option.

## Including generated files in firmware

You have two integration options: direct per-message calls or registry-based dispatch.

- Direct per-message
  - `#include "<message>.h"`
  - Call `<Message>_decode(...)` / `<Message>_encode(...)` when you already know the message type.

- Registry-based dispatch
  - `#include "sc_registry.h"` (or your `<prefix>registry.h`)
  - Call `decode_message(can_id, data, dlc, &your_msg_struct)` to route by CAN ID at runtime.

### Build system examples

Minimal GCC/Make example

```make
# Add include path
o_cflags += -I$(PROJECT_ROOT)/gen/include

# Compile all generated sources except the test runner
GEN_SRC := $(wildcard $(PROJECT_ROOT)/gen/src/*.c)
GEN_SRC := $(filter-out $(PROJECT_ROOT)/gen/src/main.c,$(GEN_SRC))

objgen := $(GEN_SRC:.c=.o)

$(objgen): CFLAGS += -I$(PROJECT_ROOT)/gen/include -std=c99 -Wall -Wextra

# Link your firmware with $(objgen)
firmware.elf: $(objgen) $(your_other_objects)
	$(CC) $(LDFLAGS) -o $@ $^
```

CMake example

```cmake
file(GLOB GEN_SRC "${CMAKE_SOURCE_DIR}/gen/src/*.c")
list(REMOVE_ITEM GEN_SRC "${CMAKE_SOURCE_DIR}/gen/src/main.c")

add_library(dbccodegen STATIC ${GEN_SRC})
target_include_directories(dbccodegen PUBLIC ${CMAKE_SOURCE_DIR}/gen/include)

add_executable(app main.c)
target_link_libraries(app PRIVATE dbccodegen)
```

Vendor IDEs
- Add `gen/include` to include paths.
- Add all `gen/src/*.c` except `gen/src/main.c` to your project.

Configurable prefix
- Common files use a prefix (default `sc_`), yielding `sc_utils.{h,c}` and `sc_registry.{h,c}`.
- Change it via `file_prefix` in your YAML config if needed.

### C++ compatibility

All generated headers include `extern "C"` guards for seamless C++ integration:

```cpp
// Your C++ firmware can include generated headers directly
#include "gen/include/sc_utils.h"      // prefixed header
#include "gen/include/utils.h"         // compatibility shim
#include "gen/include/MESSAGE_1.h"     // message-specific header

extern "C" {
    // C++ code can call generated C functions directly
    MESSAGE_1_t msg = {0};
    bool success = MESSAGE_1_decode(&msg, can_data, dlc);
}
```

The generated code remains pure C99, ensuring compatibility with both C and C++ projects without requiring changes to build systems or toolchains.

## Platforms, compilers, and test environments

Tested combos
- Local: macOS (Apple Silicon) + clang + GNU Make
- CI: Linux (GitHub Actions Ubuntu) + gcc + GNU Make
- Windows: not yet tested

Windows notes (early guidance)
- Prefer CMake to integrate generated C on Windows.
- Toolchains: MSVC, LLVM clang-cl, or MinGW-w64 gcc should work in principle.
- Set C standard and include path; exclude the test runner:
  - CMake: add
    - `target_compile_features(dbccodegen PUBLIC c_std_99)`
    - `target_include_directories(dbccodegen PUBLIC ${CMAKE_SOURCE_DIR}/gen/include)`
    - Remove `gen/src/main.c` from sources.
- Linking: MSVC doesn’t need `-lm`; math functions (llround/llroundf) are in the CRT when including `<math.h>`.
- If you hit MSVC-specific C99 quirks, consider LLVM clang-cl or MinGW as a fallback. Please open an issue with compiler/version details so we can add CI coverage.

### Quick checklist (firmware integration)

- [ ] Do NOT compile `gen/src/main.c` (test runner only)
- [ ] Add `gen/include` to include paths
- [ ] Compile all `gen/src/*.c` except `main.c`
- [ ] Include your prefix headers (e.g., `#include "sc_registry.h"`)
- [ ] Choose dispatch mode per product: `binary_search` (sparse IDs) vs `direct_map` (dense range)
- [ ] If you need different common-file names, set `file_prefix` (e.g., `file_prefix: fw_`)

### Runtime usage examples

Decode known message

```c
#include "message_1.h"

void on_frame(const uint8_t* data, size_t dlc) {
    MESSAGE_1_t m = {0};
    if (MESSAGE_1_decode(&m, data, dlc)) {
        // use m.Signal_*
    }
}
```

Decode by ID (registry)

```c
#include "sc_registry.h"
#include "message_1.h"

void on_can(uint32_t id, const uint8_t* data, size_t dlc) {
    MESSAGE_1_t m = {0};
    if (decode_message(id, data, dlc, &m)) {
        // routed to MESSAGE_1 decoder if id matches
    }
}
```

Encode

```c
#include "message_1.h"

void build_frame(uint8_t out[8], size_t* out_dlc) {
    MESSAGE_1_t m = { .Signal_1 = 123.0, .Signal_2 = 45.6 };
    if (MESSAGE_1_encode(out, out_dlc, &m)) {
        // transmit out, *out_dlc
    }
}
```

## PhysType details and numeric precision

phys_type determines how physical values are computed. Generated C struct fields remain 32-bit float for ABI stability; phys_mode controls the math precision/perf in encode/decode.

Summary
- float + phys_mode=double: use double intermediates (default)
- float + phys_mode=float: use float intermediates
- fixed + phys_mode=fixed_double: enable 10^-n fast path; fallback uses double (default)
- fixed + phys_mode=fixed_float: enable 10^-n fast path; fallback uses float

Details
- phys_type: float
  - Fields: float (32-bit)
  - Decode
    - double: `msg->sig = (float)((double)raw * factor + offset);`
    - float:  `msg->sig = (float)(((float)raw * (float)factor) + (float)offset);`
  - Encode
    - double: `double tmp = ((double)phys - offset) / factor; raw = round(tmp);`
    - float:  `float  tmp = ((float)phys - (float)offset) / (float)factor; raw = llroundf(tmp);`
  - Note: no 10^-n integer fast path in float mode.

- phys_type: fixed
  - Fields: still float (API consistency)
  - Fast path (always when applicable)
    - Conditions: `factor = 10^-n` and integral `offset`
    - Decode: `scale = 10^n`; `phys = (raw + offset*scale) / scale`
    - Encode: `raw = llround((phys - offset) * scale)`
  - Fallback (when fast path not applicable)
    - fixed_double: same as float/double path
    - fixed_float:  same as float/float path (encode uses llroundf)

Selection guide (MCU/FPU)
- MCUs with single-precision FPU where double is costly:
  - Mostly 10^-n scales → phys_type=fixed + phys_mode=fixed_float
  - Mixed arbitrary scales → phys_type=float + phys_mode=float
- Precision-first (host/large MCU) → phys_type=float + phys_mode=double or phys_type=fixed + fixed_double

Compiler flags tips
- `-Wdouble-promotion` (warn on implicit double promotion)
- `-fsingle-precision-constant` (treat FP literals as float; beware global impact)
- You can customize templates to add `f` suffix on FP literals if desired

Range checking
- With `RangeCheck/range_check = true`, encode/decode enforce min/max; out-of-range fails.

## 📊 Performance benchmarks

Measured on Apple Silicon (arm64), gcc -O2, representative scenarios:
- Simple message roundtrip: ~5–8M ops/sec
- Complex/multiplexed message roundtrip: ~1–4M ops/sec
- Registry dispatch throughput: ~7–72M ops/sec (depends on ID spread and strategy)
- Large external DBC (27 messages): stable end-to-end generation and build

Details can be reproduced via the stress suite and bulk runner in `scripts/bulk_stress.ps1`. Aggregated CSV lives under `tmp/stress_reports/summary.csv` when generated. A human-readable overview of tests and results is in `TEST_SUMMARY.md`.

### Methodology

- Harness: the generated C test runner executes tight encode/decode loops per case and measures wall-clock time with a monotonic timer; ops/sec = iterations / elapsed.
- Environment: Apple Silicon (arm64) with clang/gcc at `-O2` unless noted. CPU scaling/thermals can affect results.
- Repro:
  - Build: `make -C gen build`
  - Quick smoke: `./gen/build/test_runner test_stress_suite`
  - Corpus run: `pwsh ./scripts/bulk_stress.ps1` → see `tmp/stress_reports/summary.csv`
- Reporting: multiple trials per case; warm-up is discarded; we typically report the median.
- Portability: use your target compiler/flags/hardware to get realistic numbers for production.

### ⚙️ Performance tuning cheatsheet

- Compiler: `-O2` or `-O3`, enable LTO if your toolchain supports it
- Floating point: On single-precision FPU MCUs, prefer `phys_type: fixed` + `phys_mode: fixed_float` when 10^-n scales dominate
- Dispatch: `direct_map` is O(1) but can cost memory if IDs are sparse; `binary_search` is O(log N) and compact
- Link-time: Build generated sources as a static library to improve incremental builds
- Headers: Keep `gen/include` first in include paths to avoid name collisions

## 🔧 Troubleshooting (common issues)

- fatal error: 'message_1.h' file not found
  - Ensure include path: add `-I./gen/include` (see Make/CMake examples)

- undefined reference to MESSAGE_1_decode
  - Link generated objects from `gen/src` (exclude `gen/src/main.c`)

- Overlapping signals detected / DLC exceeds size
  - The validator rejects conflicting signals and DLC overflows. Fix the DBC or split signals.

- Unexpected float rounding differences
  - Consider `phys_type: fixed` to leverage the 10^-n fast path where applicable.

## ⚠️ Limitations

- Automatic CRC/Counter validation is not yet implemented (config flag is reserved)
- Primarily targets 8-byte classic CAN frames; extended payloads require template adjustments
- Extremely large messages with >32 signals may require widening the `valid` bitmask

## Dispatch modes, registry, and relation to nanopb

Per-message functions are always generated:
- <Message>_encode(...) and <Message>_decode(...) for each message.

The registry provides a convenience router: decode_message(can_id, data, dlc, out_struct).
- binary_search
  - A sorted table of {id, (optional) extended-flag, function pointer}; looked up with binary search. O(log N). Small memory, good for sparse IDs.
- direct_map
  - A direct mapping keyed by ID (implemented as a compact switch or table depending on range). O(1) lookup, but can cost memory/code size if IDs are sparse. Rule of thumb: prefer when IDs are dense in a small contiguous range (≈30–50%+ density); otherwise choose binary_search.
Value tables
- For signals with VAL_ tables, the generator emits enums and `<Msg>_<Sig>_to_string(int)` helpers by default. On memory-constrained targets, consider forking templates to omit string tables; a toggle may be added in a future release.

Comparison to nanopb
- Conceptually similar in spirit to nanopb-style runtime dispatch: you call a single entry point and it routes to the specific decoder based on an identifier.
- Differences: This registry is a hand-rolled CAN-ID router for C structs, not tied to protobuf descriptors or nanopb’s field/tag system.

CRC/Counter note
- The configuration flag exists, but automatic CRC/Counter validation is deferred. Handle at a higher layer or await a future generator option that maps CRC/counter signals from YAML.

## Endianness and bit utilities

- Little-Endian (Intel) and Big-Endian (Motorola) are supported. For Motorola, both MSB-sawtooth and LSB start-bit conventions are handled (configurable via `motorola_start_bit: msb|lsb`).
- Motorola BE numbering quick view (MSB sawtooth across 8 bytes):
  - Byte0: [7][6][5][4][3][2][1][0]
  - Byte1: [15][14][13][12][11][10][9][8]
  - Byte2: [23][22][21][20][19][18][17][16]
  - Byte3: [31][30][29][28][27][26][25][24]
  - Byte4: [39][38][37][36][35][34][33][32]
  - Byte5: [47][46][45][44][43][42][41][40]
  - Byte6: [55][54][53][52][51][50][49][48]
  - Byte7: [63][62][61][60][59][58][57][56]
  - LSB start-bit inputs are internally converted to MSB sawtooth for codegen normalization.
- Generated `<prefix>utils.{h,c}` (default `sc_utils.{h,c}`) provide `get_bits_le/be` and `set_bits_le/be` used by message codecs.

## Project Structure

- `src/Generator`: F# source code for the code generator.
- `templates`: Scriban templates for C code generation.
- `examples`: Sample DBC file, configuration, and a main C file for testing.
- `tests/Generator.Tests`: F# unit tests for the generator.
- `infra`: CI/CD configurations (e.g., GitHub Actions).
- `gen`: Output directory for generated C code (ignored by Git).

## License, third-party, and AI provenance

- License: This repository is licensed under the MIT License (see LICENSE).
- Third-party: Public DBCs used for testing are not bundled; see THIRD_PARTY_NOTICES.md and the policy in `external_test/README.md`.
- Confidentiality: Do not commit proprietary or internal DBCs. The `external_test/` folder is git-ignored by default except for placeholders.
- AI assistance: Portions of this repository’s documentation and code were authored with the assistance of GitHub Copilot and other LLMs; human maintainers reviewed and accepted changes.

Pre-release checklist
- [ ] Audit repository for accidental secrets or confidential data (including DBCs under external_test/)
- [ ] Verify LICENSE and THIRD_PARTY_NOTICES.md are accurate
- [ ] Ensure README(EN/KR) are consistent and up to date
- [ ] Run validation: dotnet build/test, codegen + make -C gen build, and core tests


