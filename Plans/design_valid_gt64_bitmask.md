# Design: Valid Bitmask `>64` Signal Fallback Policy

> **Status**: Design Complete — Ready for Implementation  
> **Author**: Prometheus (planning agent)  
> **Date**: 2026-03-15  
> **Scope**: Codegen policy for mux messages with >64 signals  
> **Out of Scope**: Oracle reference decoder incompatibility (ROADMAP item #2), extended mux (MUX_M), non-mux messages  
> **Baseline**: 152/152 tests pass, 0 warnings/errors, commit `d23802e` on `main`  
> **Deployment**: After review, copy this file to `Plans/design_valid_gt64_bitmask.md`

---

## 1. Current State Analysis

The valid bitmask system in `Codegen.fs` tracks which mux-branch signals were actually decoded. It has a 3-tier structure:

| Signal Count | C Type | Shift Literal | Init Literal | Status |
|---|---|---|---|---|
| ≤32 (mux or non-mux with `isMux`) | `uint32_t valid` | `1u` | `0u` | ✅ Working |
| 33–64 (mux only, `isMux && Signals.Length > 32`) | `uint64_t valid` | `1ULL` | `0ULL` | ✅ Working |
| >64 (mux only) | ❌ Hard error | — | — | `CodeGenError.UnsupportedFeature` |

### Codegen.fs Touch Points (9 locations)

All valid-related code lives in `src/Signal.CANdy.Core/Codegen.fs`. Templates are **passive containers** — they receive pre-built C code strings via model variables (`pre_struct_declarations`, `struct_extra_fields`, `signal_decode_c`).

| # | Location | Function/Block | What It Does |
|---|---|---|---|
| 1 | L447–463 | `partitionMultiplex(message)` | Separates switch (M), base, and branch (m<k>) signals |
| 2 | L487–491 | `isMux` detection | True when both switchOpt and branches are non-empty |
| 3 | L492–496 | Width selection | `if isMux && Signals.Length > 32 then uint64_t else uint32_t` |
| 4 | L498–500 | `validMacro` builder | Generates `#define MSG_VALID_SIG (shift << idx)` per signal |
| 5 | L504–510 | `signalDecodeWithValid` | Emits `msg->valid \|= MACRO_NAME;` after each signal decode |
| 6 | L535–537 | Decode init | Emits `msg->valid = 0u;` or `msg->valid = 0ULL;` |
| 7 | L720–727 | Macro emission | `List.iteri` over `message.Signals` producing `#define` lines |
| 8 | L731–736 | Struct field | Emits `uint32_t valid;` or `uint64_t valid;` |
| 9 | L1043–1064 | Overflow guard | `hasMuxSwitch && hasMuxBranches && Signals.Length > 64` → `Error(UnsupportedFeature(...))` |

### Template Model Variables

- `pre_struct_declarations` → valid macros + mux enum
- `struct_extra_fields` → `uint32_t valid;` / `uint64_t valid;` + `mux_active`
- `signal_decode_c` → decode body including init and `msg->valid |=` statements

### Bit Index Assignment

Bit positions are assigned **contiguously 0..N-1** via `List.iteri` over `message.Signals`. All signals (base + switch + branch) count toward N. Ordering depends on `DbcParserLib` parse order — this is the existing contract and is NOT changed by this design.

### Utils Template Model

The `utilsHContent` and `utilsCContent` functions build their Scriban model as `(string * obj) list` at L104–118 and L141–145. The private record types `UtilsHeaderTemplateModel`/`UtilsSourceTemplateModel` at L62–74 are **dead code** (never used). New model variables follow the existing list-append pattern (like `has_crc_j1850` at L86–93).

### Golden Test Files

- `tests/Signal.CANdy.Core.Tests/golden/mux_msg.h` — shows `uint32_t valid;` and `#define MUX_MSG_VALID_SIG (1u << N)` macros
- `tests/Signal.CANdy.Core.Tests/golden/mux_msg.c` — shows `msg->valid = 0u;` and `msg->valid |= MUX_MSG_VALID_SIG;` patterns

### Frequency in Real-World DBCs

- **Vendor DBC corpus** (15 files from opendbc in `tests/oracle/vendor_dbc/`): No confirmed >64-signal mux messages
- **Synthetic test** (CodegenTests.fs ~L1617): 65-signal test is artificial (64 branch + 1 switch = 65 total); tests the `UnsupportedFeature` guard
- **Other DBC codegens** (cantools, dbcc, dbcppp, coderdbc, dbc-codegen): None implement >64-signal valid masks
- **Conclusion**: >64 mux signals are an edge case. All testing will be synthetic

---

## 2. Problem Definition

The overflow guard at `Codegen.fs` L1043–1064 rejects any mux message with >64 total signals as `CodeGenError.UnsupportedFeature`. This means:

1. **Users cannot generate code** for DBC files containing large mux messages (>64 signals)
2. **The error is fail-fast** — emitted before any file writing, so no partial output
3. **No workaround exists** — users must split their DBC or remove signals

### Why This Matters

While >64 mux signals are rare in practice, the limitation is:
- **Documented publicly** in README.md ("Messages with >64 multiplexed signals are not supported")
- **Referenced** in `tests/oracle/CATEGORY_C_EXCEPTIONS.md` Exception 3
- **A known backlog item** in `Plans/ROADMAP.md` item #1

### Design Question

Should we lift this limit with a portable C99 fallback representation, or keep and strengthen the documentation?

**Decision**: Lift the limit with a hybrid approach (see Section 4)

---

## 3. Policy Alternatives

Four approaches were evaluated against C99 portability, embedded suitability, API ergonomics, template complexity, and backward compatibility.

### A. `uint8_t valid[(N+7)/8]` — Compact Byte Array

```c
// Struct field
uint8_t valid[9];  // for 65 signals: ceil(65/8) = 9 bytes

// Helpers (static inline in sc_utils.h)
static inline void sc_valid_set(uint8_t* v, unsigned idx)   { v[idx >> 3] |=  (uint8_t)(1u << (idx & 7)); }
static inline void sc_valid_clear(uint8_t* v, unsigned idx) { v[idx >> 3] &= (uint8_t)~(1u << (idx & 7)); }
static inline bool sc_valid_test(const uint8_t* v, unsigned idx) { return (v[idx >> 3] & (uint8_t)(1u << (idx & 7))) != 0; }

// Per-signal macros (bit index constants)
#define MUX65_MSG_VALID_BRANCH_0   0
#define MUX65_MSG_VALID_BRANCH_63 64

// Usage
if (sc_valid_test(m.valid, MUX65_MSG_VALID_BRANCH_0)) { /* use signal */ }
```

- **Pros**: Minimal memory (ceil(N/8)), well-established pattern (Apache Arrow/Parquet, Linux kernel), simple byte-granular ops native on ARM Cortex-M
- **Cons**: Needs helper functions, API syntax differs from ≤64 scalar path

### B. `uint64_t valid[⌈N/64⌉]` — Multi-Word Array

```c
uint64_t valid[2];  // for 65 signals: ceil(65/64) = 2 words = 16 bytes
#define MUX65_MSG_VALID_BRANCH_0 0
// Check: valid[idx/64] & (1ULL << (idx % 64))
```

- **Pros**: Good alignment on 64-bit targets, leverages existing uint64_t shift patterns
- **Cons**: 64-bit ops may be costly on 32-bit MCUs (double-word loads), 16 bytes for 65 signals vs 9 bytes for byte array, more complex two-level indexing macros

### C. `uint8_t valid_flags[N]` — Per-Signal Byte Flags

```c
uint8_t valid_flags[65];  // 1 byte per signal = 65 bytes
// Check: valid_flags[idx] != 0
```

- **Pros**: Simplest API (`flags[i]`), no bitwise ops needed
- **Cons**: 8× memory waste (65 bytes vs 9 bytes), no industry precedent in embedded CAN, wasteful for RAM-constrained MCUs

### D. Reject & Document — Keep Hard Error

- **Pros**: Zero implementation cost, zero risk, zero complexity
- **Cons**: Permanent user-facing limitation, no growth path

### Comparison Matrix

| Criterion | A. Byte Array | B. Word Array | C. Flag Array | D. Reject |
|---|---|---|---|---|
| **C99 Portability** | ✅ Full | ✅ Full | ✅ Full | N/A |
| **Memory (N=65)** | 9 bytes | 16 bytes | 65 bytes | 0 |
| **Memory (N=128)** | 16 bytes | 16 bytes | 128 bytes | 0 |
| **Memory (N=256)** | 32 bytes | 32 bytes | 256 bytes | 0 |
| **ARM Cortex-M Suitability** | ✅ Native byte ops | ⚠️ 64-bit load needed | ⚠️ Large footprint | ✅ |
| **API Ergonomics** | Medium | Medium | ✅ Simple | N/A |
| **Backward Compat (≤64)** | ✅ None (hybrid) | ✅ None (hybrid) | ✅ None (hybrid) | ✅ |
| **Codegen Complexity** | Medium | Medium-High | Low | Zero |
| **Real-World Precedent** | ✅ Arrow/Parquet, kernel | ✅ Kernel bitmaps | Rare | All other DBC codegens |

### Verdict

**Approach A** wins on memory efficiency, embedded suitability, and real-world precedent. Combined with the hybrid strategy (≤64 unchanged), it provides the best balance

---

## 4. Recommended Approach

**Hybrid `uint8_t` Byte Array** — keep `uint32_t`/`uint64_t` for ≤64 signals (zero backward impact), introduce `uint8_t valid[(N+7)/8]` byte array for >64 with `sc_valid_set/test/clear` inline helpers.

### Width Selection (3-tier, replacing 2-tier)

```
Signal Count    C Type                  Shift/Literal       Init
─────────────────────────────────────────────────────────────────
≤32             uint32_t valid          1u << idx           0u
33–64           uint64_t valid          1ULL << idx         0ULL
65–1024         uint8_t valid[(N+7)/8]  sc_valid_set()      memset(..., 0, ...)
>1024           UnsupportedFeature      —                   —
```

### Key Design Properties

1. **Backward compatible**: ≤64 path is byte-identical to current output. Zero regression.
2. **Automatic**: Threshold is computed from signal count. No user configuration needed.
3. **Self-contained headers**: >64 message headers conditionally include `sc_utils.h` so users don't need manual includes.
4. **Conditional helpers**: `sc_valid_set/test/clear` are only emitted in `sc_utils.h` when at least one message in the IR exceeds 64 signals. Zero bloat for the common case.
5. **Always emit size define**: `#define MSG_VALID_BYTES N` emitted alongside the array for programmatic access.

### Generated C Output Example (65 signals)

**Header (`mux65_msg.h`)**:
```c
#ifndef MUX65_MSG_H
#define MUX65_MSG_H

#include <stdint.h>
#include <stdbool.h>
#include "sc_utils.h"   /* auto-included for >64 valid array */

/* Valid bit indices */
#define MUX65_MSG_VALID_SWITCH    0
#define MUX65_MSG_VALID_BRANCH_0  1
#define MUX65_MSG_VALID_BRANCH_1  2
/* ... */
#define MUX65_MSG_VALID_BRANCH_63 64
#define MUX65_MSG_VALID_BYTES     9    /* ceil(65/8) */

typedef struct {
    /* signal fields ... */
    uint8_t valid[9];
    MUX65_MSG_mux_e mux_active;
} MUX65_MSG_t;

bool MUX65_MSG_decode(MUX65_MSG_t* msg, const uint8_t* data, uint8_t dlc);
bool MUX65_MSG_encode(uint8_t* data, uint8_t* dlc, const MUX65_MSG_t* msg);

#endif
```

**Source (`mux65_msg.c`)**:
```c
#include "mux65_msg.h"
#include <string.h>

bool MUX65_MSG_decode(MUX65_MSG_t* msg, const uint8_t* data, uint8_t dlc) {
    memset(msg->valid, 0, sizeof(msg->valid));
    /* decode switch */
    sc_valid_set(msg->valid, MUX65_MSG_VALID_SWITCH);
    /* branch decoding */
    switch (msg->mux_active) {
        case MUX65_MSG_MUX_0:
            /* decode branch 0 signals */
            sc_valid_set(msg->valid, MUX65_MSG_VALID_BRANCH_0);
            break;
        /* ... */
    }
    return true;
}
```

**User usage**:
```c
MUX65_MSG_t m = {0};
if (MUX65_MSG_decode(&m, data, dlc)) {
    if (sc_valid_test(m.valid, MUX65_MSG_VALID_BRANCH_0)) {
        /* use m.Branch_0 */
    }
}
```

### API Discontinuity Acknowledgment

| Signal Count | Check Pattern | Type |
|---|---|---|
| ≤64 | `m.valid & MSG_VALID_SIG` | Scalar bitwise AND |
| >64 | `sc_valid_test(m.valid, MSG_VALID_SIG)` | Function call on byte array |

This discontinuity is acceptable because:
- >64 doesn't work today — no existing user code to break
- The patterns are clearly differentiated by type (`uint32_t` vs `uint8_t[]`)
- README will document both patterns with examples

---

## 5. MVP Implementation Scope

### Changes Required

All changes are in **3 files** (Codegen.fs + 2 Scriban templates). Templates remain passive.

#### 5.1 `src/Signal.CANdy.Core/Codegen.fs` — 8 modifications

**M1. Width selection (L492–496)** — Add `>64` branch

Current:
```fsharp
let validType, shiftSuffix, initLiteral =
    if isMux && message.Signals.Length > 32 then
        "uint64_t", "1ULL", "0ULL"
    else
        "uint32_t", "1u", "0u"
```

Target:
```fsharp
let useValidArray = isMux && message.Signals.Length > 64
let validType, shiftSuffix, initLiteral =
    if useValidArray then
        // byte array — shift/init not used directly
        sprintf "uint8_t", "", ""
    elif isMux && message.Signals.Length > 32 then
        "uint64_t", "1ULL", "0ULL"
    else
        "uint32_t", "1u", "0u"
let validArraySize = if useValidArray then (message.Signals.Length + 7) / 8 else 0
```

**M2. Macro emission (L720–727)** — Emit index constants for >64

Current: `#define MSG_VALID_SIG (1u << idx)` or `(1ULL << idx)`  
Target for >64: `#define MSG_VALID_SIG idx` (plain integer)  
Also emit: `#define MSG_VALID_BYTES validArraySize`

**M3. Struct field (L731–736)** — Emit byte array for >64

Current: `uint32_t valid;` or `uint64_t valid;`  
Target for >64: `uint8_t valid[{validArraySize}];`

**M4. Decode init (L535–537)** — Use memset for >64

Current: `msg->valid = 0u;` or `msg->valid = 0ULL;`  
Target for >64: `memset(msg->valid, 0, sizeof(msg->valid));`  
Note: `<string.h>` is already included in `message.c.scriban` L4.

**M5. Per-signal valid set (L504–510)** — Use `sc_valid_set()` for >64

Current: `msg->valid |= MACRO_NAME;`  
Target for >64: `sc_valid_set(msg->valid, MACRO_NAME);`  
This is inside `signalDecodeWithValid` which must receive `useValidArray` as context.

**M6. Overflow guard (L1043–1064)** — Change threshold from 64 to 1024

Current: `msg.Signals.Length > 64` → `Error(UnsupportedFeature(...))`  
Target: `msg.Signals.Length > 1024` → `Error(UnsupportedFeature(...))`

**M7. Utils model — `has_valid_array` boolean**

At L86–118 where the utils Scriban model is built as `(string * obj) list`:
- Compute `has_valid_array` by scanning all messages in the IR: `messages |> List.exists (fun m -> isMux(m) && m.Signals.Length > 64)`
- Add `("has_valid_array", box hasValidArray)` to the model list
- Follow exact pattern of `has_crc_j1850` at L86–93

**M8. Message header model — conditional `#include`**

When emitting the message header model, add a boolean `needs_utils_include` when the message uses byte-array valid. This triggers `message.h.scriban` to conditionally emit `#include "{{ utils_header_name }}"`.

#### 5.2 `templates/utils.h.scriban` — 1 addition

Add conditional block (follow pattern at L28–30 for CRC):

```
{{ if has_valid_array }}
/* ── Valid bitmask helpers (byte-array, >64 signals) ── */
static inline void sc_valid_set(uint8_t* arr, unsigned bit) {
    arr[bit >> 3] |= (uint8_t)(1u << (bit & 7u));
}
static inline void sc_valid_clear(uint8_t* arr, unsigned bit) {
    arr[bit >> 3] &= (uint8_t)~(1u << (bit & 7u));
}
static inline bool sc_valid_test(const uint8_t* arr, unsigned bit) {
    return (arr[bit >> 3] & (uint8_t)(1u << (bit & 7u))) != 0;
}
{{ end }}
```

No changes to `utils.c.scriban` — helpers are `static inline` in the header only.

#### 5.3 `templates/message.h.scriban` — 1 addition

Add conditional include after existing `<stdbool.h>` include:

```
{{ if needs_utils_include }}
#include "{{ utils_header_name }}"
{{ end }}
```

#### 5.4 Files NOT Changed

- `templates/message.c.scriban` — receives pre-built strings, no template logic change
- `templates/utils.c.scriban` — helpers are header-only (`static inline`)
- `src/Signal.CANdy.Core/Ir.fs` — IR types unchanged
- `src/Signal.CANdy.Core/Errors.fs` — `UnsupportedFeature` variant unchanged (just threshold change)
- Encode path (`Codegen.fs` L546–650) — encode does not use `valid`

### Implementation Guardrails

| ID | Guardrail | Enforcement |
|---|---|---|
| G1 | ≤64 path produces **byte-identical** output | Golden test regression: `mux_msg.h`, `mux_msg.c` |
| G2 | Non-mux messages unaffected | Existing test at ~L1632 |
| G3 | Encode path untouched | Zero diff in L546–650 |
| G4 | Use `(string * obj) list` pattern for model | Follow L104–118 |
| G5 | Helper names use `sc_` prefix (not `file_prefix`) | Follow CRC helper precedent |
| G6 | No extended mux (MUX_M) support | Explicit scope exclusion |
| G7 | Helpers NOT emitted when no message >64 | `has_valid_array` conditional |
| G8 | No hallucinated vendor DBC evidence | All >64 testing is synthetic |
| G9 | Signal index ordering unchanged | `List.iteri` contract preserved |

---

## 6. Test Strategy

### TDD Approach

Follow RED → GREEN → REFACTOR. Tests first, implementation second.

#### Phase RED: Write Failing Tests

**T1. Refactor 65-signal test** (CodegenTests.fs ~L1617)

Current: expects `Error(UnsupportedFeature(...))` for 65 signals.  
Refactor to expect `Ok` with specific assertions:
- Header contains `uint8_t valid[9];`
- Header contains `#define MUX65_MSG_VALID_BYTES 9`
- Header contains `#define MUX65_MSG_VALID_BRANCH_0 1` (index constant, NOT `(1u << 1)`)
- Header contains `#define MUX65_MSG_VALID_BRANCH_63 64`
- Header contains `#include "sc_utils.h"`
- Source contains `memset(msg->valid, 0, sizeof(msg->valid))`
- Source contains `sc_valid_set(msg->valid,`
- Source does NOT contain `msg->valid |=`
- Source does NOT contain `msg->valid = 0`

**T2. Add 128-signal test**

New test: `mkMuxMessage "MUX128_MSG"` with 127 branch + 1 switch = 128 total.
- Header contains `uint8_t valid[16];` (= ⌈128/8⌉)
- Header contains `#define MUX128_MSG_VALID_BYTES 16`
- Macro indices span 0 to 127

**T3. Add boundary test: exactly 64 signals remains uint64_t**

Verify existing test at ~L1596 still passes. Add assertion:
- Header contains `uint64_t valid;` (NOT `uint8_t valid[`)
- Header contains `(1ULL << ` (shift macro, NOT index constant)

**T4. Add conditional utils emission test**

Generate from a DBC with only ≤64-signal mux messages:
- `sc_utils.h` output does NOT contain `sc_valid_set`
- `sc_utils.h` output does NOT contain `sc_valid_test`
- `sc_utils.h` output does NOT contain `sc_valid_clear`

Generate from a DBC with at least one >64-signal mux message:
- `sc_utils.h` output DOES contain all three helpers

**T5. Add golden files**

- Create `tests/Signal.CANdy.Core.Tests/golden/mux65_msg.h` — representative >64 header
- Create `tests/Signal.CANdy.Core.Tests/golden/mux65_msg.c` — representative >64 source
- Verify via `assertGeneratedFileMatchesGolden`

#### Phase GREEN: Implement Until Tests Pass

Follow M1–M8 modifications from Section 5 in dependency order.

#### Phase REFACTOR: Clean Up

- Remove any dead code introduced during implementation
- Ensure `dotnet fantomas --check` passes

### Acceptance Criteria

| ID | Criterion | Verification Command |
|---|---|---|
| AC1 | All existing tests pass (backward compat) | `dotnet test -c Release -v minimal --nologo` → 152+ pass |
| AC2 | Golden files for ≤64 byte-identical | `diff` against existing `mux_msg.h`, `mux_msg.c` |
| AC3 | 65-signal test: byte-array assertions | `dotnet test -c Release --filter "DisplayName~65"` |
| AC4 | 128-signal test: byte-array assertions | `dotnet test -c Release --filter "DisplayName~128"` |
| AC5 | 64-signal test: still uint64_t | `dotnet test -c Release --filter "DisplayName~64 signal"` |
| AC6 | Conditional utils emission | `dotnet test -c Release --filter "DisplayName~valid_array"` |
| AC7 | New golden files verified | `assertGeneratedFileMatchesGolden` in test |
| AC8 | C99 compilation | `gcc -std=c99 -Wall -Wextra -Werror -c` zero diagnostics |
| AC9 | C++ compatibility | `g++ -std=c++11 -Wall -Wextra -Werror -c` zero errors |
| AC10 | Build 0 warnings | `dotnet build -c Release --nologo` |
| AC11 | Format check | `dotnet fantomas --check src/ tests/` |

### Edge Cases to Cover

| ID | Case | Expected Behavior |
|---|---|---|
| EC1 | Exactly 64 signals | `uint64_t valid` — existing path, no change |
| EC2 | Exactly 65 signals | `uint8_t valid[9]` — first byte-array case |
| EC3 | Exactly 72 signals (byte-aligned) | `uint8_t valid[9]` — verify no over-allocation |
| EC4 | 1024 signals | `uint8_t valid[128]` — max supported |
| EC5 | 1025 signals | `UnsupportedFeature` error |
| EC6 | Non-mux message with 100 signals | No valid field emitted (guard: existing test ~L1632) |

---

## 7. Risk Assessment

### Risk Matrix

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | **Backward regression**: ≤64 path output changes | Low | **Critical** | Golden file tests (`mux_msg.h`, `mux_msg.c`) catch byte-level drift. AC2 enforces. |
| R2 | **Template injection break**: new model variables misalign with Scriban syntax | Medium | High | Templates are passive containers — changes are additive (new conditionals). Existing variables untouched. |
| R3 | **`memset` unavailability on exotic targets** | Very Low | Medium | `<string.h>` already included in `message.c.scriban` L4. C99 mandates `memset`. |
| R4 | **Index overflow in helper**: `bit >> 3` exceeds array bounds | Low | High | Indices are assigned 0..N-1 via `List.iteri` where N ≤ 1024. Array size is `⌈N/8⌉`. Max index `1023 >> 3 = 127` < 128 = `⌈1024/8⌉`. Add assertion-style test (EC4, EC5). |
| R5 | **Signal ordering changes across parser versions** | Low | Medium | `DbcParserLib` parse order is the existing contract for ≤64. >64 path uses identical `List.iteri`. No new ordering dependency introduced. |
| R6 | **API discontinuity confuses users** | Medium | Low | README documents both patterns with code examples. The >64 case doesn't work today — no existing user code to break. Discontinuity is clearly signaled by type difference (`uint32_t` vs `uint8_t[]`). |
| R7 | **Scope creep into oracle decoder** | Medium | High | Explicit guardrail: oracle reference decoder incompatibility is ROADMAP item #2, out of scope. `CATEGORY_C_EXCEPTIONS.md` Exception 3 remains valid — oracle tests don't cover >64. |
| R8 | **`sc_utils.h` conditional emission false-positive** | Low | Medium | `has_valid_array` is computed by scanning IR at generation time, not per-message. If any message triggers it, helpers are emitted. Verified by T4 test group. |
| R9 | **C++ compilation failure for helpers** | Low | Medium | Helpers use `uint8_t*`, `unsigned`, `bool` — all C++11 compatible. `stdbool.h` already included. AC9 enforces C++ check. |
| R10 | **Fantomas formatting changes break TDD cycle** | Low | Low | Run `dotnet fantomas --check` at REFACTOR phase, not during RED/GREEN. AC11 is a final gate. |

### Dependency Risks

| Dependency | Version | Risk | Notes |
|---|---|---|---|
| `DbcParserLib` | 1.7.0 | Stable | Signal ordering contract unchanged; no API changes needed |
| `Scriban` | 6.2.1 | Stable | Only additive template changes; no new syntax features used |
| `YamlDotNet` | 16.3.0 | None | Config parsing unaffected; no new config keys |
| `FsUnit.xUnit` | 7.1.1 | None | Test assertions only; no changes to framework |

### Rollback Strategy

If critical issues emerge during implementation:

1. **Git revert**: All implementation is atomic per commit phase (7 phases in commit strategy). Any phase can be reverted independently.
2. **Guard restoration**: Reverting M6 (threshold change from 64→1024) restores the original hard error for >64. This is a one-line change.
3. **Feature flag** (NOT recommended for MVP): A compile-time `#define` could gate the >64 path, but adds complexity with no real-world need given the synthetic-only test base.

### What Is NOT a Risk

- **Memory pressure from `uint8_t[128]`**: 128 bytes at maximum (1024 signals) is negligible on any CAN-capable platform. CAN ECUs typically have 16KB+ RAM minimum.
- **Performance of byte-array helpers**: `sc_valid_set/test/clear` compile to 3–4 ARM instructions (shift, mask, load-store). No measurable overhead vs scalar `|=`.
- **CI breakage**: All changes are gated behind existing CI pipeline (`dotnet build` → `dotnet test` → codegen → `gcc` → `g++` → NuGet pack). No CI configuration changes needed.

---

## 8. Sisyphus Implementation Prompt

This section is the **complete, self-contained prompt** for Sisyphus to execute this design. It contains ALL decisions, ALL file references, ALL acceptance criteria, and ALL edge cases. The executor needs NO external context beyond this section and the repository files it references.

### Context

Signal-CANdy is an F# (.NET 8) code generator that produces portable C99 parser modules from CAN DBC files. The generated C code includes a `valid` bitmask in multiplexed message structs, tracking which signals were decoded. Currently, this bitmask is limited to 64 signals (`uint32_t` for ≤32, `uint64_t` for 33–64). Messages with >64 signals fail with `CodeGenError.UnsupportedFeature`.

**Goal**: Replace the hard error with a `uint8_t valid[(N+7)/8]` byte-array fallback for >64 signals, with `sc_valid_set/test/clear` inline helpers in `sc_utils.h`, while keeping the ≤64 path byte-identical to current output.

### Baseline

- **Commit**: `d23802e` on `main`
- **Tests**: 152/152 pass
- **Build**: 0 warnings/errors

### Files to Modify (3 files only)

1. **`src/Signal.CANdy.Core/Codegen.fs`** — 8 code modifications (M1–M8)
2. **`templates/utils.h.scriban`** — 1 conditional block addition
3. **`templates/message.h.scriban`** — 1 conditional include addition

### Files to Create (2 golden test files + new tests)

4. **`tests/Signal.CANdy.Core.Tests/golden/mux65_msg.h`** — reference header for 65-signal mux
5. **`tests/Signal.CANdy.Core.Tests/golden/mux65_msg.c`** — reference source for 65-signal mux

### Files NOT to Modify

- `templates/message.c.scriban` — receives pre-built strings, no change
- `templates/utils.c.scriban` — helpers are `static inline` in header
- `src/Signal.CANdy.Core/Ir.fs` — IR types unchanged
- `src/Signal.CANdy.Core/Errors.fs` — `UnsupportedFeature` variant unchanged
- Encode path (`Codegen.fs` L546–650) — encode does not use `valid`

---

### Modification Specifications

**M1. Width selection** (`Codegen.fs` ~L492–496)

Replace 2-tier selection with 3-tier:

```fsharp
// CURRENT (2-tier):
let validType, shiftSuffix, initLiteral =
    if isMux && message.Signals.Length > 32 then
        "uint64_t", "1ULL", "0ULL"
    else
        "uint32_t", "1u", "0u"

// TARGET (3-tier):
let useValidArray = isMux && message.Signals.Length > 64
let validType, shiftSuffix, initLiteral =
    if useValidArray then
        "uint8_t", "", ""
    elif isMux && message.Signals.Length > 32 then
        "uint64_t", "1ULL", "0ULL"
    else
        "uint32_t", "1u", "0u"
let validArraySize = if useValidArray then (message.Signals.Length + 7) / 8 else 0
```

**M2. Macro emission** (`Codegen.fs` ~L720–727)

For >64: emit `#define MSG_VALID_SIG idx` (plain integer index, NOT `(1u << idx)`)
Also emit: `#define MSG_VALID_BYTES validArraySize`

For ≤64: emit UNCHANGED `#define MSG_VALID_SIG (1u << idx)` or `(1ULL << idx)`

**M3. Struct field** (`Codegen.fs` ~L731–736)

For >64: emit `uint8_t valid[{validArraySize}];`
For ≤64: emit UNCHANGED `uint32_t valid;` or `uint64_t valid;`

**M4. Decode init** (`Codegen.fs` ~L535–537)

For >64: emit `memset(msg->valid, 0, sizeof(msg->valid));`
For ≤64: emit UNCHANGED `msg->valid = 0u;` or `msg->valid = 0ULL;`
Note: `<string.h>` is already included in `message.c.scriban` L4.

**M5. Per-signal valid set** (`Codegen.fs` ~L504–510)

For >64: emit `sc_valid_set(msg->valid, MACRO_NAME);`
For ≤64: emit UNCHANGED `msg->valid |= MACRO_NAME;`

The function `signalDecodeWithValid` must receive `useValidArray` as context (pass as parameter or close over it).

**M6. Overflow guard** (`Codegen.fs` ~L1043–1064)

Change threshold: `msg.Signals.Length > 64` → `msg.Signals.Length > 1024`
Error message should say "1024" instead of "64".

**M7. Utils model — `has_valid_array`** (`Codegen.fs` ~L86–118)

Add boolean to the `(string * obj) list` model:
- Compute: `messages |> List.exists (fun m -> isMuxMessage(m) && m.Signals.Length > 64)`
- Add: `("has_valid_array", box hasValidArray)` to the model list
- Pattern: follow exactly how `has_crc_j1850` is added at ~L86–93

**M8. Message header model — conditional `#include`** (`Codegen.fs` message header model building)

When a message uses byte-array valid (`useValidArray = true`):
- Add `("needs_utils_include", box true)` to the message header Scriban model
- Also add `("utils_header_name", box utilsHeaderFileName)` where `utilsHeaderFileName` follows the existing `file_prefix` logic for utils naming

---

### Template Specifications

**T-UTILS. `templates/utils.h.scriban`** — Add conditional block

Follow the pattern at ~L28–30 (CRC conditional). Add AFTER existing conditional blocks:

```
{{ if has_valid_array }}
/* ── Valid bitmask helpers (byte-array, >64 signals) ── */
static inline void sc_valid_set(uint8_t* arr, unsigned bit) {
    arr[bit >> 3] |= (uint8_t)(1u << (bit & 7u));
}

static inline void sc_valid_clear(uint8_t* arr, unsigned bit) {
    arr[bit >> 3] &= (uint8_t)~(1u << (bit & 7u));
}

static inline bool sc_valid_test(const uint8_t* arr, unsigned bit) {
    return (arr[bit >> 3] & (uint8_t)(1u << (bit & 7u))) != 0;
}
{{ end }}
```

**T-HDR. `templates/message.h.scriban`** — Add conditional include

After existing `<stdbool.h>` include line:

```
{{ if needs_utils_include }}
#include "{{ utils_header_name }}"
{{ end }}
```

---

### TDD Execution Order

**Phase RED** — Write failing tests FIRST (tests fail because implementation doesn't exist yet):

1. **Refactor existing 65-signal test** (`CodegenTests.fs` ~L1617):
   - CURRENTLY expects `Error(UnsupportedFeature(...))` for 65 signals
   - CHANGE to expect `Ok` with these assertions:
     - Header contains `uint8_t valid[9];`
     - Header contains `#define .*_VALID_BYTES 9`
     - Header contains `#define .*_VALID_BRANCH_0 1` (index constant, NOT `(1u << 1)`)
     - Header contains `#include "sc_utils.h"` (or prefix variant)
     - Source contains `memset(msg->valid, 0, sizeof(msg->valid))`
     - Source contains `sc_valid_set(msg->valid,`
     - Source does NOT contain `msg->valid |=` (for this message)
     - Source does NOT contain `msg->valid = 0` (scalar init)

2. **Add 128-signal test** (new test in `CodegenTests.fs`):
   - Create mux message with 127 branch + 1 switch = 128 signals
   - Header contains `uint8_t valid[16];` (`⌈128/8⌉`)
   - Header contains `#define .*_VALID_BYTES 16`

3. **Add boundary test: 64 signals stays uint64_t** (strengthen existing ~L1596):
   - Assert header contains `uint64_t valid;` (NOT `uint8_t valid[`)
   - Assert header contains `(1ULL <<` (shift macro, NOT index constant)

4. **Add conditional utils emission tests** (new tests):
   - Generate with ONLY ≤64-signal mux messages → `sc_utils.h` does NOT contain `sc_valid_set`
   - Generate with at least one >64-signal mux message → `sc_utils.h` DOES contain all 3 helpers

5. **Add 1025-signal guard test** (new test):
   - Expect `Error(UnsupportedFeature(...))` with "1024" in error message

6. **Add edge case tests**:
   - EC3: 72 signals → `uint8_t valid[9]` (byte-aligned boundary)
   - EC4: 1024 signals → `uint8_t valid[128]` (max supported)
   - EC6: Non-mux message with 100 signals → no valid field

**Phase GREEN** — Implement M1–M8 and T-UTILS, T-HDR until all tests pass.

Implementation order within GREEN:
1. M1 (width selection) + M3 (struct field) — enables correct type in output
2. M2 (macro emission) — changes macro format for >64
3. M4 (decode init) + M5 (per-signal set) — changes decode body for >64
4. M6 (overflow guard) — lifts threshold from 64 to 1024
5. M7 (utils model) + T-UTILS — adds conditional helpers
6. M8 (header model) + T-HDR — adds conditional include

**Phase REFACTOR**:
1. Remove any dead code or temporary scaffolding
2. Run `dotnet fantomas --check src/ tests/` — fix if needed
3. Run full test suite: `dotnet test -c Release -v minimal --nologo`

---

### Golden File Handling

**Existing golden files MUST remain byte-identical**:
- `tests/Signal.CANdy.Core.Tests/golden/mux_msg.h` — ≤32 signal mux
- `tests/Signal.CANdy.Core.Tests/golden/mux_msg.c` — ≤32 signal mux

**New golden files to create**:
- `tests/Signal.CANdy.Core.Tests/golden/mux65_msg.h` — 65-signal mux showing byte-array pattern
- `tests/Signal.CANdy.Core.Tests/golden/mux65_msg.c` — 65-signal mux showing `memset` + `sc_valid_set`

Generate golden files by running the codegen with a 65-signal synthetic mux message, then freezing the output. Follow the existing golden test pattern in `CodegenTests.fs` (search for `assertGeneratedFileMatchesGolden` or similar golden-comparison assertions).

---

### Acceptance Criteria (All Must Pass)

| ID | Criterion | Verification |
|---|---|---|
| AC1 | All existing 152 tests pass | `dotnet test -c Release -v minimal --nologo` → 152+ pass, 0 fail |
| AC2 | Existing golden files byte-identical | `diff` against `mux_msg.h`, `mux_msg.c` in golden/ |
| AC3 | 65-signal test: byte-array correct | New test passes with all assertions from RED phase step 1 |
| AC4 | 128-signal test: byte-array correct | New test passes with all assertions from RED phase step 2 |
| AC5 | 64-signal test: still uint64_t | Existing/strengthened test passes (RED phase step 3) |
| AC6 | Conditional utils emission | New tests pass (RED phase step 4) |
| AC7 | New golden files verified | `assertGeneratedFileMatchesGolden` passes for `mux65_msg.{h,c}` |
| AC8 | C99 compilation | `gcc -std=c99 -Wall -Wextra -Werror -c` on generated >64 code → 0 diagnostics |
| AC9 | C++ compatibility | `g++ -std=c++11 -Wall -Wextra -Werror -c` on generated >64 code → 0 errors |
| AC10 | Build clean | `dotnet build -c Release --nologo` → 0 warnings, 0 errors |
| AC11 | Format check | `dotnet fantomas --check src/ tests/` → no changes needed |

---

### Commit Strategy (7 atomic phases)

| Phase | Commit Message | Contents |
|---|---|---|
| 1 | `test(codegen): add failing tests for >64 valid byte-array` | RED phase: all new/refactored tests (they fail) |
| 2 | `feat(codegen): add 3-tier valid width selection for >64 signals` | M1 (width selection) |
| 3 | `feat(codegen): emit byte-array macros, struct field, and decode body for >64` | M2 + M3 + M4 + M5 |
| 4 | `feat(codegen): lift overflow guard from 64 to 1024 signals` | M6 |
| 5 | `feat(codegen): add conditional sc_valid helpers and utils include` | M7 + M8 + T-UTILS + T-HDR (GREEN: tests should pass now) |
| 6 | `test(codegen): add golden files for 65-signal mux and C compilation checks` | Golden files + AC8/AC9 C compilation validation |
| 7 | `refactor(codegen): clean up and format check` | REFACTOR phase + fantomas compliance |

Each commit should pass `dotnet build -c Release --nologo` (0 errors). Tests may fail in phases 1–4 (TDD RED). All tests must pass starting from phase 5.

---

### Guardrails (Hard Rules for the Executor)

| ID | Rule |
|---|---|
| G1 | **≤64 path produces byte-identical output.** If existing golden tests fail, you broke backward compat. STOP and fix. |
| G2 | **Non-mux messages are untouched.** Do not add valid logic to non-mux paths. |
| G3 | **Encode path is untouched.** Zero diff in `Codegen.fs` L546–650. |
| G4 | **Use `(string * obj) list` pattern for Scriban model.** Follow L104–118, NOT the dead record types at L62–74. |
| G5 | **Helper names use `sc_` prefix** (hardcoded, NOT `file_prefix`). Follow CRC helper naming precedent. |
| G6 | **No extended mux (MUX_M) support.** Out of scope. |
| G7 | **Helpers NOT emitted when no message >64.** `has_valid_array` conditional must work. |
| G8 | **All >64 testing is synthetic.** Do not claim vendor DBC evidence. |
| G9 | **Signal index ordering unchanged.** `List.iteri` contract preserved. Do not re-sort signals. |
| G10 | **Oracle decoder scope boundary.** Do not touch `tests/oracle/` files, `CATEGORY_C_EXCEPTIONS.md`, or any oracle-related code. ROADMAP item #2 is a separate task. |

---

### Scope Exclusions (Explicitly NOT Part of This Task)

| ID | Exclusion |
|---|---|
| SC1 | Extended mux (MUX_M) support |
| SC2 | Oracle reference decoder changes |
| SC3 | User-configurable threshold (always auto at 65) |
| SC4 | Unified API across ≤64 and >64 (discontinuity is accepted) |
| SC5 | README/documentation updates (separate follow-up task) |

---

### Session Report Requirement

After implementation is complete, write a report to `Reports/YYYYMMDD_HHMM_Valid_GT64_ByteArray_구현.md` with 4 required sections:
- 📝 **작업 요약**: Implementation of >64 valid byte-array fallback
- 🛠 **변경 상세**: All modified/created files with specific changes
- ✅ **테스트 결과**: `dotnet test` results, C compilation results, golden file verification
- ⏭ **다음 계획**: README documentation update, ROADMAP item #1 checkbox update

---

## Appendix A: Design Decisions Log

| # | Question | Decision | Rationale |
|---|----------|----------|-----------|
| Q1 | Header include for >64 messages | Auto-include `sc_utils.h` conditionally | Self-contained headers; user doesn't need to know internal detail |
| Q2 | API boundary (scalar → array) | At 65 signals | Clean separation; >64 doesn't work today so no backward compat issue |
| Q3 | Maximum signal cap | 1024 | Generous limit covering any realistic DBC; catches pathological inputs |
| Q4 | Array size representation | Always emit `#define MSG_VALID_BYTES N` + literal in struct | Zero-cost information; no config needed |

## Appendix B: Evidence Sources

| Source | Session/Agent | Key Finding |
|---|---|---|
| Codegen.fs implementation map | explore `ses_30ef47f67ffeuCr50xu9r32IOC` | 9 touch points with exact line numbers |
| Template architecture | explore `ses_30ef43110ffeIxWY5iaAFQcSmi` | Templates are passive containers |
| C99 design space | librarian `ses_30ef3e598ffePObXOoyC2CT3X6` | Apache Arrow/Parquet byte arrays, Linux kernel bitmaps |
| Metis gap analysis | metis `ses_30ee7a997ffeF7vrzeDsWU5Igc` | 7 questions, 9 guardrails, 5 scope locks, 6 edge cases, 11 ACs |
| Vendor DBC frequency | explore (partial) `ses_30ef3ac65ffeAXUHjs1NH4Zhgi` | No confirmed >64-signal mux in corpus; all testing synthetic |
