# Oracle Test Pipeline — cantools Cross-Validation for Signal-CANdy

## TL;DR

> **Quick Summary**: Build a Python+C oracle test pipeline that uses cantools as ground truth to validate Signal-CANdy's generated C pack/unpack code across all edge cases, real-world DBCs, and config permutations.
> 
> **Deliverables**:
> - Python oracle harness (`tests/oracle/`) with cantools-based test vector generation
> - Template-generated C oracle harness (`oracle_harness.c`) with JSON stdin/stdout protocol
> - Config matrix runner testing all phys_type/phys_mode/range_check combinations
> - opendbc corpus runner (67 real-world DBCs) with graceful skip/fail reporting
> - Vendored DBC subset for offline testing
> - pytest suite for the Python harness itself
> - Dynamic per-signal float tolerance framework
> 
> **Estimated Effort**: XL
> **Parallel Execution**: YES — 4 waves
> **Critical Path**: Task 1 → Task 2 → Task 3 → Task 4 → Task 5 → Task 6 → Task 7 → Task 8 → Task 9

---

## Context

### Original Request
User wants Oracle Testing using Python `cantools` as ground truth to cross-validate Signal-CANdy's generated C code, targeting "hell zones" (Motorola byte order, byte-boundary crossings, signed integers, factor/offset scaling).

### Interview Summary
**Key Discussions**:
- Scope: Full coverage from day 1 — hell zones + opendbc corpus + mux + CAN FD + adversarial DBCs + all config permutations
- Integration: Subprocess + JSON protocol (no shared-lib/ctypes ABI complexity)
- C harness: New dedicated `oracle_harness.c`, NOT modifying `examples/main.c`
- opendbc: Both clone + vendor. Default = use both; fallback = vendor-only when offline
- CI: Local-only first. CI integration deferred to a separate future task
- Config matrix: Full — all valid combinations of phys_type, phys_mode, range_check, motorola_start_bit
- Float tolerance: Dynamic per-signal (computed from factor, offset, bit length, float32 precision)
- Python: 3.12+, pytest for the harness itself

**Research Findings**:
- cantools is mature (2.2k stars), follows Vector DBC spec, handles all signal types
- No existing OSS project does cantools↔C oracle testing — this is novel
- Signal-CANdy's codegen is in `Codegen.fs` (not Scriban templates)
- Exact C code patterns mapped: `get_bits_le/be`, sign extension idiom, factor/offset math, power-of-ten fast-path
- Config knobs: `motorola_start_bit` (msb|lsb), `phys_type` (float|fixed), `phys_mode` (double|float|fixed_double|fixed_float), `range_check` (true|false)
- opendbc has 67 DBC files from real vehicles

### Metis Review
**Identified Gaps** (addressed):

1. **Motorola LSB-cantools incompatibility** (CRITICAL): cantools always interprets DBC start bits as MSB-convention for Motorola signals. `motorola_start_bit=lsb` configs cannot be validated against cantools. 
   → **Resolution**: Exclude `motorola_start_bit=lsb` from cantools oracle matrix. Test LSB via existing F# unit tests only.

2. **Rounding strategy divergence** (CRITICAL): Signal-CANdy uses round-half-away-from-zero; cantools/Python uses banker's rounding (round-half-to-even). At exact 0.5 boundaries, encoded bytes may differ by 1 LSB.
   → **Resolution**: Allow ±1 raw LSB tolerance in byte comparison for encode tests. Document as known divergence.

3. **Float32 struct fields vs Python double** (HIGH): All C struct fields are `float` (32-bit) regardless of config. Python uses 64-bit double. Systematic precision mismatch.
   → **Resolution**: Dynamic per-signal tolerance: `tol = max(abs(factor) * 0.5, abs(expected_phys) * FLT_EPSILON * 8)` where FLT_EPSILON ≈ 1.19e-7.

4. **DbcParserLib ↔ cantools parsing divergence** (MEDIUM): Two different parsers may interpret edge-case DBCs differently.
   → **Resolution**: Metadata comparison step before encode/decode comparison. Extract and diff signal attributes from both sides. Skip divergent signals with diagnostic log.

5. **opendbc corpus quality** (MEDIUM): Some files may have malformed signals, extended mux, or `SIG_VALTYPE_` float signals that Signal-CANdy doesn't support.
   → **Resolution**: Parse-only pre-pass. Categorize each DBC as parseable/unparseable. Skip unsupported features gracefully.

6. **Config matrix runtime** (LOW-MEDIUM): 67 DBCs × 8 configs = 536 codegen+compile+test cycles could take hours.
   → **Resolution**: Full matrix is nightly/manual. Default quick mode uses only repo example DBCs × key configs.

7. **oracle_harness.c must be generated per-DBC** (MEDIUM): Each DBC produces different message headers. The harness must `#include` exactly the right headers.
   → **Resolution**: Python generates oracle_harness.c from a template string, inserting the correct `#include` directives.

8. **Non-atomic encode behavior** (LOW): With `range_check=true`, encode returns false but may partially update output buffer.
   → **Resolution**: When C encode returns false, don't compare byte output. Separate negative-test path.

---

## Work Objectives

### Core Objective
Build a Python-driven oracle test pipeline that validates Signal-CANdy's generated C pack/unpack code against cantools (Python) as ground truth, covering all signal types, byte orders, scaling modes, and config permutations.

### Concrete Deliverables
- `tests/oracle/` — Complete Python package with:
  - `run_oracle.py` — Single-DBC oracle test runner
  - `run_matrix.py` — Config matrix runner
  - `run_corpus.py` — Multi-DBC corpus runner
  - `oracle_harness_template.c` — C harness template string
  - `tolerance.py` — Dynamic per-signal tolerance calculator
  - `metadata_compare.py` — Signal metadata comparison (cantools vs generated C)
  - `vector_gen.py` — Test vector generation (boundary, random, adversarial)
  - `conftest.py` + test files — pytest suite for the harness itself
  - `requirements.txt` — Python dependencies (cantools, pytest)
- `tests/oracle/vendor_dbc/` — Curated subset of opendbc files for offline testing
- Structured JSON report output per test run

### Definition of Done
- [x] `python tests/oracle/run_oracle.py --dbc examples/sample.dbc --config examples/config.yaml --out-dir tmp/oracle_test --assert-pass` exits 0
- [x] `python tests/oracle/run_oracle.py --dbc examples/comprehensive_test.dbc --config examples/config.yaml --out-dir tmp/oracle_test --assert-pass` exits 0
- [x] `python tests/oracle/run_matrix.py --dbc examples/comprehensive_test.dbc --out-dir tmp/oracle_matrix` covers ≥8 config combinations and exits 0
- [x] `python tests/oracle/run_corpus.py --corpus-dir external_test/opendbc --out-dir tmp/oracle_corpus --report-only` produces corpus_report.json with pass/fail/skip counts
- [x] `pytest tests/oracle/ -v` passes all Python harness tests
- [x] All 7 example DBCs (sample, comprehensive_test, motorola_lsb_suite, fixed_suite, value_table, canfd_test, multiplex_suite) pass oracle test with default config

### Must Have
- cantools as ground truth oracle
- JSON stdin/stdout protocol between Python and C harness
- Dynamic per-signal float tolerance accounting for float32 precision
- ±1 raw LSB tolerance for encode byte comparison (rounding divergence)
- Metadata comparison before encode/decode comparison
- Config matrix: phys_type × phys_mode × range_check (8 valid combos for msb)
- Graceful skip for unsupported DBC features (extended mux, SIG_VALTYPE_ float)
- opendbc corpus testing with both clone + vendored fallback
- pytest suite for the Python harness itself
- Structured JSON report per run

### Must NOT Have (Guardrails)
- Do NOT modify `examples/main.c`, any F# source code, Scriban templates, or existing CI pipeline
- Do NOT test `motorola_start_bit=lsb` configs against cantools (known incompatibility)
- Do NOT treat `dispatch` config as a test matrix dimension (doesn't affect encode/decode math)
- Do NOT add opendbc as a git submodule — clone into `tmp/` or gitignored `external_test/opendbc/`
- Do NOT run the full oracle matrix in CI (local-only per user's decision)
- Do NOT generate test vectors with physical values outside [min, max] for `range_check=true` (separate negative test path)
- Do NOT compare byte output when C encode returns false (non-atomic writes)
- Do NOT use shared-lib/ctypes approach (subprocess+JSON only)
- Do NOT require internet connectivity for basic tests (vendored DBC fallback)
- Do NOT use Python versions below 3.12

---

## Verification Strategy (MANDATORY)

> **UNIVERSAL RULE: ZERO HUMAN INTERVENTION**
>
> ALL tasks in this plan MUST be verifiable WITHOUT any human action.

### Test Decision
- **Infrastructure exists**: NO (new Python test infrastructure)
- **Automated tests**: YES (Tests-after) — pytest for Python harness
- **Framework**: pytest (Python) + gcc compilation (C harness)

### Agent-Executed QA Scenarios (MANDATORY — ALL tasks)

Verification tool mapping:

| Type | Tool | How Agent Verifies |
|------|------|-------------------|
| Python oracle scripts | Bash (python/pytest) | Run scripts, parse JSON output, assert fields |
| C harness compilation | Bash (gcc) | Compile, check exit code, run binary |
| JSON protocol | Bash (echo + pipe) | Pipe JSON to C binary, parse stdout |
| Report output | Bash (python -c) | Load JSON report, assert structure |
| opendbc integration | Bash (git clone + python) | Clone, run corpus, verify report |

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Start Immediately):
├── Task 1: Python project scaffold (requirements, structure, pyproject.toml)
└── Task 2: C oracle harness template (JSON protocol, template string)

Wave 2 (After Wave 1):
├── Task 3: Core oracle engine (single-DBC, single-config, end-to-end)
└── Task 4: Dynamic tolerance framework + metadata comparison

Wave 3 (After Wave 2):
├── Task 5: Test vector generation (boundary, random, adversarial)
├── Task 6: Config matrix runner
└── Task 7: Vendored DBC subset + opendbc corpus runner

Wave 4 (After Wave 3):
├── Task 8: pytest suite for Python harness
└── Task 9: Full integration test with all example DBCs + matrix

Critical Path: Task 1 → Task 3 → Task 5 → Task 8 → Task 9
Parallel Speedup: ~35% faster than sequential
```

### Dependency Matrix

| Task | Depends On | Blocks | Can Parallelize With |
|------|------------|--------|---------------------|
| 1 | None | 3, 4 | 2 |
| 2 | None | 3 | 1 |
| 3 | 1, 2 | 5, 6, 7, 8, 9 | 4 |
| 4 | 1 | 5, 6 | 3 |
| 5 | 3, 4 | 8, 9 | 6, 7 |
| 6 | 3, 4 | 9 | 5, 7 |
| 7 | 3 | 9 | 5, 6 |
| 8 | 3, 5 | 9 | 6, 7 |
| 9 | 5, 6, 7, 8 | None | None (final) |

### Agent Dispatch Summary

| Wave | Tasks | Recommended Agents |
|------|-------|-------------------|
| 1 | 1, 2 | task(category="quick", ...) — scaffold files |
| 2 | 3, 4 | task(category="ultrabrain", ...) — core logic |
| 3 | 5, 6, 7 | task(category="unspecified-high", ...) — feature work |
| 4 | 8, 9 | task(category="deep", ...) — integration + tests |

---

## TODOs

- [x] 1. Python Project Scaffold

  **What to do**:
  - Create `tests/oracle/` directory structure:
    ```
    tests/oracle/
    ├── __init__.py
    ├── requirements.txt          # cantools, pytest
    ├── pyproject.toml             # project metadata, pytest config
    ├── run_oracle.py              # single-DBC oracle runner (CLI entrypoint)
    ├── run_matrix.py              # config matrix runner (CLI entrypoint)
    ├── run_corpus.py              # corpus runner (CLI entrypoint)
    ├── oracle/                    # internal package
    │   ├── __init__.py
    │   ├── engine.py              # core oracle logic (placeholder)
    │   ├── harness.py             # C harness generation + compilation (placeholder)
    │   ├── tolerance.py           # tolerance framework (placeholder)
    │   ├── metadata_compare.py    # signal metadata comparison (placeholder)
    │   ├── vector_gen.py          # test vector generation (placeholder)
    │   └── report.py              # structured report generation (placeholder)
    ├── templates/
    │   └── oracle_harness.c       # C harness template (placeholder, filled in Task 2)
    ├── vendor_dbc/                # vendored opendbc subset (filled in Task 7)
    │   └── .gitkeep
    └── tests/                     # pytest tests (filled in Task 8)
        ├── __init__.py
        └── conftest.py
    ```
  - `requirements.txt`: `cantools>=39.0`, `pytest>=8.0`
  - `pyproject.toml`: Python 3.12+ requirement, pytest config, package metadata
  - Create stub CLI scripts (`run_oracle.py`, `run_matrix.py`, `run_corpus.py`) with argparse scaffolding
  - Add `tests/oracle/` to `.gitignore` entries for generated artifacts: `tmp/`, `*.pyc`, `__pycache__/`

  **Must NOT do**:
  - Do NOT implement any actual oracle logic yet — stubs and placeholders only
  - Do NOT modify existing project files
  - Do NOT install packages globally — assume virtualenv usage

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: This is file scaffolding — creating directory structure and stub files. No complex logic.
  - **Skills**: []
    - No specialized skills needed for creating empty Python files
  - **Skills Evaluated but Omitted**:
    - `frontend-ui-ux`: No UI involved
    - `playwright`: No browser involved

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Task 2)
  - **Blocks**: Tasks 3, 4
  - **Blocked By**: None

  **References**:

  **Pattern References**:
  - `tests/Signal.CANdy.Core.Tests/` — existing test directory structure to align with naming conventions
  - `examples/config.yaml` — YAML config format to understand what config files look like

  **External References**:
  - Official docs: `https://docs.pytest.org/en/stable/goodpractices.html` — pytest project layout best practices
  - cantools: `https://pypi.org/project/cantools/` — version and dependency info

  **WHY Each Reference Matters**:
  - The test directory structure should mirror existing patterns in the repo for consistency
  - Config file format understanding needed for argparse arguments

  **Acceptance Criteria**:

  - [ ] Directory `tests/oracle/` exists with all listed files
  - [ ] `python -c "import sys; assert sys.version_info >= (3,12)"` passes
  - [ ] `pip install -r tests/oracle/requirements.txt` succeeds
  - [ ] `python tests/oracle/run_oracle.py --help` prints usage and exits 0
  - [ ] `python tests/oracle/run_matrix.py --help` prints usage and exits 0
  - [ ] `python tests/oracle/run_corpus.py --help` prints usage and exits 0
  - [ ] `python -c "from oracle import engine, harness, tolerance, metadata_compare, vector_gen, report"` succeeds (when run from `tests/oracle/`)

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Python scaffold is importable and CLI works
    Tool: Bash
    Preconditions: Python 3.12+ available, pip available
    Steps:
      1. pip install -r tests/oracle/requirements.txt
      2. python tests/oracle/run_oracle.py --help
      3. Assert: exit code 0, stdout contains "--dbc" and "--config"
      4. python tests/oracle/run_matrix.py --help
      5. Assert: exit code 0, stdout contains "--dbc" and "--out-dir"
      6. python tests/oracle/run_corpus.py --help
      7. Assert: exit code 0, stdout contains "--corpus-dir"
      8. python -c "import cantools; print(cantools.__version__)"
      9. Assert: exit code 0, prints version string
    Expected Result: All CLI scripts show help, cantools is importable
    Evidence: Terminal output captured
  ```

  **Commit**: YES
  - Message: `feat(oracle): scaffold Python oracle test project structure`
  - Files: `tests/oracle/**`
  - Pre-commit: `python tests/oracle/run_oracle.py --help`

---

- [x] 2. C Oracle Harness Template

  **What to do**:
  - Create `tests/oracle/templates/oracle_harness.c` as a C template string (with `{{PLACEHOLDER}}` markers) that:
    - Reads JSON lines from stdin (one command per line)
    - Supports two actions: `"decode"` and `"encode"`
    - For decode: accepts `{"message": "MSG", "action": "decode", "data": [0,1,...], "dlc": 8}`, calls `MSG_decode()`, prints `{"ok": true, "signals": {"Sig1": 1.0, ...}}`
    - For encode: accepts `{"message": "MSG", "action": "encode", "signals": {"Sig1": 1.0}}`, calls `MSG_encode()`, prints `{"ok": true, "data": [0,1,...], "dlc": 8}`
    - On failure (decode/encode returns false): prints `{"ok": false, "error": "..."}`
    - Template placeholders:
      - `{{INCLUDES}}` — `#include "msg1.h"\n#include "msg2.h"\n...`
      - `{{DECODE_DISPATCH}}` — `if (strcmp(msg_name, "MSG1") == 0) { ... } else if ...`
      - `{{ENCODE_DISPATCH}}` — same pattern for encode
      - `{{SIGNAL_TO_JSON}}` — per-message signal-to-JSON printing code
      - `{{JSON_TO_SIGNAL}}` — per-message JSON-to-signal parsing code
  - Create `tests/oracle/oracle/harness.py` with:
    - `generate_harness_c(messages: list[MessageInfo], include_dir: str, src_dir: str) -> str` — fills template placeholders
    - `compile_harness(c_source: str, gen_dir: str, output_binary: str) -> bool` — runs gcc
    - `run_harness(binary: str, commands: list[dict]) -> list[dict]` — subprocess with JSONL stdin/stdout
    - `MessageInfo` dataclass: name, signals (name, type), dlc
  - The C harness must use ONLY:
    - `<stdio.h>`, `<stdlib.h>`, `<string.h>`, `<stdint.h>`, `<stdbool.h>` — standard C99
    - Simple JSON parsing: manual `sscanf`/`strstr`-based parsing (no external JSON library)
    - No additional dependencies beyond the generated message headers and utils
  - JSON parsing in C must be minimal but correct for the defined protocol:
    - Parse message name string
    - Parse action string
    - Parse data array (array of integers)
    - Parse signals object (key-value pairs of string:float)
    - Print JSON output (sprintf-based)

  **Must NOT do**:
  - Do NOT use any external C JSON library (cJSON, jansson, etc.)
  - Do NOT include `<math.h>` beyond what generated code already includes
  - Do NOT modify `examples/main.c` or any template files
  - Do NOT make the harness depend on any Signal-CANdy infrastructure — it's standalone C that includes generated headers

  **Recommended Agent Profile**:
  - **Category**: `ultrabrain`
    - Reason: The C harness requires careful JSON parsing in C99 without external libraries, correct dispatch logic generation, and precise protocol adherence. This is logic-heavy.
  - **Skills**: []
    - No specialized skills needed — this is pure C and Python logic
  - **Skills Evaluated but Omitted**:
    - `playwright`: No browser involved
    - `frontend-ui-ux`: No UI involved

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Task 1)
  - **Blocks**: Task 3
  - **Blocked By**: None

  **References**:

  **Pattern References**:
  - `examples/main.c:1-50` — C coding style for the project (includes, memset patterns, print_bytes)
  - `examples/main.c:196-230` — assert_equal_bytes and assertion patterns for reference
  - `src/Signal.CANdy.Core/Codegen.fs:225-260` — Generated decode/encode function signatures to match:
    - `bool MSG_decode(MSG_t* msg, const uint8_t data[], uint8_t dlc)`
    - `bool MSG_encode(uint8_t data[], uint8_t* out_dlc, const MSG_t* msg)`
  - `templates/message.h.scriban` — Generated header structure (typedef struct, function prototypes)

  **API/Type References**:
  - `src/Signal.CANdy.Core/Ir.fs` — Signal record type (Name, StartBit, Length, Factor, Offset, IsSigned, ByteOrder, etc.) — needed to understand what MessageInfo must contain
  - `src/Signal.CANdy.Core/Codegen.fs:1-50` — Utils module with struct field naming convention

  **WHY Each Reference Matters**:
  - `examples/main.c` shows the C coding conventions and how generated functions are called
  - `Codegen.fs` function signatures are what the harness must call — mismatch = compilation failure
  - `Ir.fs` Signal fields determine what metadata the Python harness generator needs
  - `message.h.scriban` shows the generated struct layout (field names, types)

  **Acceptance Criteria**:

  - [ ] `tests/oracle/templates/oracle_harness.c` exists with all placeholder markers
  - [ ] Template compiles successfully when placeholders are filled with a simple test message
  - [ ] `harness.py` has `generate_harness_c`, `compile_harness`, `run_harness` functions
  - [ ] Manual smoke test: generate a harness for `examples/sample.dbc`, compile with gcc, pipe a decode command, get JSON output

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: C harness template compiles with a test message
    Tool: Bash
    Preconditions: gcc available, Signal-CANdy repo built, examples/sample.dbc available
    Steps:
      1. Run: dotnet run --project src/Generator -- --dbc examples/sample.dbc --out tmp/oracle_harness_test --config examples/config.yaml --emit-main false
      2. Assert: exit code 0, tmp/oracle_harness_test/include/ and tmp/oracle_harness_test/src/ exist
      3. Run Python script to generate oracle_harness.c from template for the generated headers
      4. Compile: gcc -std=c99 -Wall -Wextra -I tmp/oracle_harness_test/include -o tmp/oracle_harness_test/oracle_harness tmp/oracle_harness_test/src/*.c tests/oracle/tmp/oracle_harness.c -lm
      5. Assert: exit code 0, binary exists
      6. Pipe: echo '{"message":"MESSAGE_1","action":"decode","data":[0,0,0,0,0,0,0,0],"dlc":8}' | tmp/oracle_harness_test/oracle_harness
      7. Assert: stdout is valid JSON with "ok" field
    Expected Result: Template generates compilable C, binary processes JSON commands
    Evidence: Terminal output + binary existence

  Scenario: C harness handles invalid input gracefully
    Tool: Bash
    Preconditions: oracle_harness binary compiled from above
    Steps:
      1. Pipe: echo '{"message":"NONEXISTENT","action":"decode","data":[0],"dlc":1}' | tmp/oracle_harness_test/oracle_harness
      2. Assert: stdout JSON has "ok": false, "error" field present
      3. Pipe: echo 'not json at all' | tmp/oracle_harness_test/oracle_harness
      4. Assert: does not crash (exit code 0 or graceful error JSON)
    Expected Result: Invalid inputs produce error JSON, no crash
    Evidence: Terminal output captured
  ```

  **Commit**: YES
  - Message: `feat(oracle): add C harness template and Python harness generation`
  - Files: `tests/oracle/templates/oracle_harness.c`, `tests/oracle/oracle/harness.py`
  - Pre-commit: `python -c "from oracle.harness import generate_harness_c, compile_harness, run_harness"`

---

- [x] 3. Core Oracle Engine (Single-DBC, Single-Config)

  **What to do**:
  - Implement `tests/oracle/oracle/engine.py` — the core end-to-end oracle pipeline:
    1. `load_dbc_cantools(dbc_path: str) -> cantools.Database` — load DBC with cantools
    2. `run_codegen(dbc_path: str, config_path: str, out_dir: str) -> bool` — call `dotnet run --project src/Generator -- --dbc <dbc> --out <dir> --config <config>` via subprocess
    3. `extract_message_info(gen_dir: str) -> list[MessageInfo]` — parse generated headers to extract message names, signal names/types, DLCs
    4. `build_oracle_binary(gen_dir: str, messages: list[MessageInfo]) -> str` — generate harness C, compile, return binary path
    5. `run_oracle_test(db: cantools.Database, binary: str, vectors: list[TestVector]) -> list[TestResult]` — for each vector: encode with cantools → decode with C (and vice versa) → compare
    6. `oracle_pipeline(dbc_path: str, config_path: str, out_dir: str) -> OracleReport` — orchestrates all steps
  - Implement `run_oracle.py` CLI:
    - `--dbc <path>` — input DBC file (required)
    - `--config <path>` — YAML config file (optional, uses defaults)
    - `--out-dir <path>` — output directory for artifacts and reports (required)
    - `--assert-pass` — exit with code 1 if any test fails (optional flag)
    - `--vectors-per-signal <N>` — number of random test vectors per signal (default 10)
    - `--verbose` — print per-signal results
  - The engine must:
    - Generate test vectors using cantools: boundary values (0, max_raw, min_raw for signed) + N random values
    - For each test vector (decode direction): cantools.encode(phys_values) → get bytes → pipe to C harness decode → compare C decoded values vs original phys_values
    - For each test vector (encode direction): set phys_values in C harness encode → get bytes → cantools.decode(c_bytes) → compare decoded values vs original phys_values
    - Also compare bytes directly: cantools.encode(phys) vs C harness encode(phys) → byte-by-byte comparison with ±1 LSB tolerance
    - Skip signals with unsupported features (check: `SIG_VALTYPE_`, extended mux markers)
    - Output structured JSON report

  **Must NOT do**:
  - Do NOT implement tolerance logic here — use placeholder tolerance from Task 4
  - Do NOT implement config matrix logic — that's Task 6
  - Do NOT implement corpus logic — that's Task 7
  - Do NOT test `motorola_start_bit=lsb` configs against cantools
  - Do NOT modify any existing project files

  **Recommended Agent Profile**:
  - **Category**: `ultrabrain`
    - Reason: Core pipeline orchestration with subprocess management, JSON parsing, cantools API usage, and precise comparison logic. Logic-heavy integration work.
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `playwright`: No browser involvement

  **Parallelization**:
  - **Can Run In Parallel**: NO (depends on Tasks 1 and 2)
  - **Parallel Group**: Wave 2
  - **Blocks**: Tasks 5, 6, 7, 8, 9
  - **Blocked By**: Tasks 1, 2

  **References**:

  **Pattern References**:
  - `tests/oracle/oracle/harness.py` (from Task 2) — C harness generation and subprocess protocol
  - `tests/oracle/oracle/tolerance.py` (placeholder from Task 1) — tolerance API to call
  - `.github/workflows/ci.yml:40-55` — How codegen is invoked in CI (dotnet run command pattern)
  - `examples/config.yaml` — Default config format for codegen invocation

  **API/Type References**:
  - cantools API: `db.get_message_by_name(name).encode(signals_dict)` — encode physical→bytes
  - cantools API: `msg.decode(data_bytes)` — decode bytes→physical dict
  - cantools API: `signal.byte_order`, `signal.is_signed`, `signal.conversion.scale/.offset`, `signal.minimum`, `signal.maximum`
  - `src/Signal.CANdy.Core/Ir.fs` — Signal record fields to map to cantools Signal attributes

  **Documentation References**:
  - `README.md` — "Usage" section for codegen command-line syntax
  - `README.md` — "Output layout and naming" section for generated file structure

  **External References**:
  - cantools docs: `https://cantools.readthedocs.io/en/latest/#cantools.database.load_file` — Load and encode/decode API

  **WHY Each Reference Matters**:
  - CI workflow shows the exact `dotnet run` command and flags needed to invoke the generator
  - cantools API is the ground truth implementation — must call it correctly
  - `Ir.fs` Signal fields must map to cantools Signal attributes for metadata comparison
  - README output layout tells us where to find generated headers/sources

  **Acceptance Criteria**:

  - [ ] `python tests/oracle/run_oracle.py --dbc examples/sample.dbc --config examples/config.yaml --out-dir tmp/oracle_test` exits 0
  - [ ] `tmp/oracle_test/report.json` exists and is valid JSON
  - [ ] Report contains at least 1 message with encode and decode test results
  - [ ] Report contains per-signal pass/fail status
  - [ ] `--assert-pass` flag causes exit code 1 when a test fails (verify by corrupting expected value)

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Oracle pipeline runs end-to-end with sample.dbc
    Tool: Bash
    Preconditions: Python 3.12+, cantools installed, dotnet SDK 8+, gcc available
    Steps:
      1. python tests/oracle/run_oracle.py --dbc examples/sample.dbc --config examples/config.yaml --out-dir tmp/oracle_e2e --verbose
      2. Assert: exit code 0
      3. python -c "import json; r=json.load(open('tmp/oracle_e2e/report.json')); print(f'Messages: {len(r[\"results\"])}'); assert len(r['results']) > 0"
      4. Assert: at least 1 message tested
      5. python -c "import json; r=json.load(open('tmp/oracle_e2e/report.json')); fails=[t for t in r['results'] if not t['passed']]; print(f'Failures: {len(fails)}')"
      6. Assert: 0 failures
    Expected Result: sample.dbc passes oracle with default config
    Evidence: report.json content + terminal output

  Scenario: --assert-pass exits 1 on failure
    Tool: Bash
    Preconditions: Oracle pipeline working
    Steps:
      1. Create a deliberately broken DBC or corrupt the tolerance to force failure
      2. python tests/oracle/run_oracle.py --dbc <broken.dbc> --out-dir tmp/oracle_fail --assert-pass
      3. Assert: exit code 1
    Expected Result: Non-zero exit on failure
    Evidence: Terminal output showing exit code
  ```

  **Commit**: YES
  - Message: `feat(oracle): implement core oracle engine with single-DBC pipeline`
  - Files: `tests/oracle/oracle/engine.py`, `tests/oracle/run_oracle.py`
  - Pre-commit: `python tests/oracle/run_oracle.py --dbc examples/sample.dbc --config examples/config.yaml --out-dir tmp/oracle_test`

---

- [x] 4. Dynamic Tolerance Framework + Metadata Comparison

  **What to do**:
  - Implement `tests/oracle/oracle/tolerance.py`:
    - `compute_tolerance(factor: float, offset: float, expected_phys: float, bit_length: int, is_signed: bool) -> float`
      - Formula: `tol = max(abs(factor) * 0.5, abs(expected_phys) * FLT_EPSILON * 8)`
      - Where `FLT_EPSILON ≈ 1.19209e-07` (float32 machine epsilon)
      - For integer signals (factor=1, offset=0): tolerance = 0.0 (exact match)
      - For very small factors: lower-bound tolerance = `abs(factor) * 0.5` (can't be more precise than 1 raw LSB)
    - `compare_physical(cantools_val: float, c_val: float, tolerance: float) -> bool`
    - `compare_bytes(cantools_bytes: bytes, c_bytes: bytes, signal_bit_positions: dict) -> tuple[bool, list[str]]`
      - Allow ±1 in bytes that contain signals at rounding boundaries
      - Return (match, list of difference descriptions)
  - Implement `tests/oracle/oracle/metadata_compare.py`:
    - `compare_signal_metadata(cantools_signal, candy_signal_info: dict) -> list[str]`
      - Compare: start_bit, length, byte_order, is_signed, factor, offset, min, max
      - Return list of divergence descriptions (empty = match)
    - `extract_cantools_metadata(db: cantools.Database) -> dict[str, dict[str, dict]]`
      - Extract per-message per-signal metadata from cantools database
    - `extract_candy_metadata(gen_dir: str) -> dict[str, dict[str, dict]]`
      - Parse generated C headers to extract signal metadata (regex on struct fields + function patterns)
      - OR parse the DBC directly with cantools to get the same metadata (simpler approach)
    - `compare_all(cantools_meta: dict, candy_meta: dict) -> ComparisonReport`
      - Per-signal comparison; flag divergences as warnings
  - Design decision: for metadata extraction from Signal-CANdy side, the simplest approach is to parse the DBC with cantools AND also with Signal-CANdy (via dotnet), then compare the IR. Since we don't have a direct IR dump, compare at the byte level instead: if cantools.encode() and C harness encode produce the same bytes for the same physical values, the metadata interpretation is consistent.

  **Must NOT do**:
  - Do NOT modify any existing project files
  - Do NOT implement a full DBC parser in Python — use cantools for metadata extraction
  - Do NOT use tolerances that are too loose (would miss real bugs)

  **Recommended Agent Profile**:
  - **Category**: `ultrabrain`
    - Reason: Floating-point precision analysis, tolerance computation, and metadata comparison are mathematically precise tasks.
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `playwright`: No browser involved

  **Parallelization**:
  - **Can Run In Parallel**: YES (partially)
  - **Parallel Group**: Wave 2 (with Task 3, but can start after Task 1 only)
  - **Blocks**: Tasks 5, 6
  - **Blocked By**: Task 1

  **References**:

  **Pattern References**:
  - `src/Signal.CANdy.Core/Codegen.fs:130-180` — Factor/offset math patterns in generated C (to understand precision requirements)
  - `src/Signal.CANdy.Core/Codegen.fs:90-120` — `tryPowerOfTenScale` — power-of-ten detection affects precision characteristics
  - `src/Signal.CANdy.Core/Ir.fs:1-50` — Signal record type fields (Factor, Offset, etc.)

  **External References**:
  - IEEE 754: `https://en.wikipedia.org/wiki/Single-precision_floating-point_format` — float32 precision characteristics
  - cantools signal properties: `https://cantools.readthedocs.io/en/latest/#cantools.database.can.Signal`

  **WHY Each Reference Matters**:
  - `Codegen.fs` math patterns show exactly how precision loss occurs in the C code (float casts, intermediate double→float)
  - `tryPowerOfTenScale` determines whether integer fast-path is used (which has different precision characteristics)
  - IEEE 754 float32 precision is the fundamental constraint on tolerance computation

  **Acceptance Criteria**:

  - [ ] `compute_tolerance(0.1, 0, 25.0, 12, False)` returns a value > 0 that accounts for float32 precision
  - [ ] `compute_tolerance(1, 0, 100, 8, False)` returns 0.0 (exact integer match expected)
  - [ ] `compare_physical(25.0, 25.000002, tol)` returns True for computed tolerance
  - [ ] `compare_bytes(b'\x12\x34', b'\x12\x35', ...)` handles ±1 LSB correctly
  - [ ] Metadata comparison catches byte_order mismatch (returns non-empty divergence list)

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Tolerance correctly handles float32 precision edge cases
    Tool: Bash (python -c)
    Preconditions: tolerance.py implemented
    Steps:
      1. python -c "from oracle.tolerance import compute_tolerance; t = compute_tolerance(0.01, 250, 250.5, 12, False); assert t > 0; assert t < 0.01; print(f'tol={t}')"
      2. Assert: tolerance is positive but less than factor
      3. python -c "from oracle.tolerance import compute_tolerance; t = compute_tolerance(1, 0, 42, 8, False); assert t == 0.0; print('exact')"
      4. Assert: integer signals have zero tolerance
    Expected Result: Tolerance values are physically meaningful
    Evidence: Terminal output
  ```

  **Commit**: YES
  - Message: `feat(oracle): implement dynamic tolerance framework and metadata comparison`
  - Files: `tests/oracle/oracle/tolerance.py`, `tests/oracle/oracle/metadata_compare.py`
  - Pre-commit: `python -c "from oracle.tolerance import compute_tolerance; print(compute_tolerance(0.1, 0, 10.0, 12, False))"`

---

- [x] 5. Test Vector Generation

  **What to do**:
  - Implement `tests/oracle/oracle/vector_gen.py`:
    - `generate_vectors_for_signal(signal: cantools.Signal, count: int = 10) -> list[float]`
      - Boundary values: raw=0 (decode to phys), raw=max (2^length - 1 for unsigned, 2^(length-1) - 1 for signed)
      - Sign boundaries (if signed): raw=-1 (phys), raw=min (-2^(length-1) as phys), raw=0
      - Zero frame: all zeros → decode all signals
      - All-ones frame: all 0xFF → decode all signals
      - Mid-range value: `(min + max) / 2`
      - Random within [min, max]: N values using `random.uniform(min, max)`
      - Scaling edge cases: physical values where `(phys - offset) / factor` is close to 0.5 (rounding boundary) — these should be flagged as "rounding_boundary" for relaxed tolerance
    - `generate_vectors_for_message(message: cantools.Message, count_per_signal: int = 10) -> list[TestVector]`
      - For each signal, generate physical values
      - Combine into full-message test vectors (all signals populated simultaneously)
      - Also generate per-signal isolated tests (only one signal set, others at default/zero)
    - `generate_adversarial_vectors(signal: cantools.Signal) -> list[float]`
      - Values at exact rounding boundaries: `offset + factor * (raw + 0.5)` for each boundary raw
      - Values requiring maximum bit-shift in get_bits_be (Motorola signals crossing most bytes)
      - Large absolute values close to float32 overflow
    - `TestVector` dataclass: message_name, signal_values (dict), expected_bytes (optional), direction ("encode"|"decode"|"both"), tags (list: "boundary", "random", "adversarial", "rounding_boundary")
  - Implement multiplexed message vector generation:
    - For each mux branch, generate vectors that set the switch value and only the active branch's signals
    - Verify that non-active branch signals are not decoded

  **Must NOT do**:
  - Do NOT generate vectors with physical values outside [min, max] for `range_check=true` configs
  - Do NOT generate rounding-boundary vectors for exact-match comparison — flag them for relaxed tolerance
  - Do NOT generate more than 100 random vectors per signal (diminishing returns)

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: Test vector generation requires understanding CAN signal semantics, boundary analysis, and mux logic. Medium-high complexity.
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `playwright`: No browser involved

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 6, 7)
  - **Blocks**: Tasks 8, 9
  - **Blocked By**: Tasks 3, 4

  **References**:

  **Pattern References**:
  - `examples/main.c:240-400` — Existing hand-calculated test vectors showing boundary value patterns
  - `examples/main.c:600-800` — Motorola LSB comprehensive test vectors for reference edge cases
  - `examples/comprehensive_test.dbc` — DBC with LE/BE/Signed/NonAligned/Packed/Scale signals

  **API/Type References**:
  - cantools Signal: `.minimum`, `.maximum`, `.conversion.scale`, `.conversion.offset`, `.length`, `.is_signed`, `.byte_order`

  **External References**:
  - cantools encode: `https://cantools.readthedocs.io/en/latest/#cantools.database.can.Message.encode`

  **WHY Each Reference Matters**:
  - `examples/main.c` test vectors show exactly what boundary values are interesting for CAN signals
  - `comprehensive_test.dbc` is the primary test DBC — vectors must cover its signals
  - cantools Signal properties determine valid ranges for vector generation

  **Acceptance Criteria**:

  - [ ] `generate_vectors_for_signal(unsigned_12bit_signal, 10)` produces ≥14 vectors (4 boundary + 10 random)
  - [ ] `generate_vectors_for_signal(signed_16bit_signal, 10)` includes negative boundary values
  - [ ] `generate_vectors_for_message(mux_message, 5)` produces per-branch vector sets
  - [ ] All generated physical values are within [signal.minimum, signal.maximum]
  - [ ] Rounding-boundary vectors are tagged with "rounding_boundary"

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Boundary vectors cover the right values
    Tool: Bash (python)
    Preconditions: cantools installed, vector_gen.py implemented
    Steps:
      1. python -c "
         import cantools
         from oracle.vector_gen import generate_vectors_for_signal
         db = cantools.database.load_file('examples/comprehensive_test.dbc')
         msg = db.get_message_by_name('LE_TEST')
         sig = msg.get_signal_by_name('LE_12_CROSS')
         vecs = generate_vectors_for_signal(sig, 10)
         print(f'Vectors: {len(vecs)}')
         assert len(vecs) >= 14
         print('OK')
         "
      2. Assert: exits 0, prints OK
    Expected Result: Correct number of boundary + random vectors generated
    Evidence: Terminal output
  ```

  **Commit**: YES
  - Message: `feat(oracle): implement test vector generation with boundary and adversarial cases`
  - Files: `tests/oracle/oracle/vector_gen.py`
  - Pre-commit: `python -c "from oracle.vector_gen import generate_vectors_for_signal"`

---

- [x] 6. Config Matrix Runner

  **What to do**:
  - Implement `tests/oracle/run_matrix.py`:
    - Generates all valid config combinations:
      - `phys_type: float` × `phys_mode: double | float` × `range_check: true | false` = 4 configs
      - `phys_type: fixed` × `phys_mode: fixed_double | fixed_float` × `range_check: true | false` = 4 configs
      - Total: 8 configs × `motorola_start_bit: msb` only = 8 combinations
      - `dispatch: direct_map` always (doesn't affect signal math)
    - For each config combination:
      1. Generate YAML config file in temp dir
      2. Run `oracle_pipeline()` from engine.py
      3. Collect results
    - CLI: `--dbc <path>` (required), `--out-dir <path>` (required), `--parallel <N>` (optional, default 1)
    - Output: matrix_report.json with per-config results + overall summary
    - Support for parallel execution: use `concurrent.futures.ProcessPoolExecutor` for parallelism across configs
  - Implement negative tests for `range_check=true`:
    - Generate out-of-range physical values
    - Verify C encode returns false (`{"ok": false}`)
    - Verify C decode handles out-of-range raw values correctly

  **Must NOT do**:
  - Do NOT include `motorola_start_bit=lsb` in the matrix (known cantools incompatibility)
  - Do NOT include `dispatch` as a matrix dimension
  - Do NOT run full matrix in CI

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: Config matrix generation, parallel execution, YAML generation, and negative testing. Medium-high complexity.
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `playwright`: No browser involved

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 5, 7)
  - **Blocks**: Task 9
  - **Blocked By**: Tasks 3, 4

  **References**:

  **Pattern References**:
  - `examples/config.yaml` — Config YAML format
  - `examples/config_range_check.yaml` — Range check config example
  - `examples/config_fixed.yaml` — Fixed phys_type config example
  - `src/Signal.CANdy.Core/Config.fs` — Config validation logic (valid phys_type/phys_mode combinations)

  **WHY Each Reference Matters**:
  - Config YAML examples show the exact format the generator expects
  - `Config.fs` defines which phys_type/phys_mode combinations are valid — must match matrix

  **Acceptance Criteria**:

  - [ ] `python tests/oracle/run_matrix.py --dbc examples/sample.dbc --out-dir tmp/oracle_matrix` exits 0
  - [ ] `tmp/oracle_matrix/matrix_report.json` contains results for exactly 8 config combinations
  - [ ] Each config combination shows pass/fail/skip counts
  - [ ] range_check=true configs include negative test results (out-of-range values rejected)

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Matrix runner covers all 8 valid configs
    Tool: Bash
    Preconditions: Core oracle engine working, sample.dbc available
    Steps:
      1. python tests/oracle/run_matrix.py --dbc examples/sample.dbc --out-dir tmp/oracle_matrix --verbose
      2. Assert: exit code 0
      3. python -c "import json; r=json.load(open('tmp/oracle_matrix/matrix_report.json')); print(f'Configs: {len(r[\"configs\"])}'); assert len(r['configs']) == 8"
      4. Assert: exactly 8 config combinations tested
    Expected Result: All valid config permutations exercised
    Evidence: matrix_report.json content
  ```

  **Commit**: YES
  - Message: `feat(oracle): implement config matrix runner with all valid permutations`
  - Files: `tests/oracle/run_matrix.py`
  - Pre-commit: `python tests/oracle/run_matrix.py --help`

---

- [x] 7. Vendored DBC Subset + opendbc Corpus Runner

  **What to do**:
  - **Vendor DBC subset** in `tests/oracle/vendor_dbc/`:
    - Curate 10-15 DBC files from opendbc that cover diverse signals:
      - Toyota (common, well-structured)
      - Honda/Hyundai (different conventions)
      - VW/BMW (European-style, often complex)
      - Ford (large files with many messages)
    - Selection criteria: files that Signal-CANdy can parse successfully + have both LE and BE signals + have signed signals + have scaling
    - Add `tests/oracle/vendor_dbc/README.md` noting source (opendbc), license (MIT), and commit hash
  - **Implement `run_corpus.py`**:
    - `--corpus-dir <path>` — directory containing DBC files (required)
    - `--out-dir <path>` — output directory (required)
    - `--config <path>` — single config to test (optional, default config)
    - `--full-matrix` — run all 8 config combinations per DBC (optional)
    - `--assert-pass` — exit 1 if any testable DBC fails (optional)
    - `--report-only` — don't assert, just produce report (optional)
    - `--clone-opendbc` — clone opendbc into tmp dir and include those DBCs too (optional)
    - Pipeline per DBC:
      1. Try codegen → skip if fails (log reason)
      2. Try compile → skip if fails (log reason)
      3. Run oracle tests → report pass/fail per message/signal
      4. Aggregate results into corpus_report.json
    - opendbc clone logic:
      - `git clone --depth 1 https://github.com/commaai/opendbc.git tmp/opendbc`
      - Find all `*.dbc` files under `tmp/opendbc/opendbc/dbc/`
      - If clone fails (no internet), fall back to vendor_dbc only
    - Default behavior: use `vendor_dbc/` + cloned opendbc (if `--clone-opendbc` specified)
    - Fallback: vendor_dbc only (no internet required)
  - **Unsupported feature detection**:
    - Before running oracle, check cantools signals for:
      - `signal.is_float` → skip (SIG_VALTYPE_ not supported by Signal-CANdy)
      - Extended multiplexing indicators → skip
    - Log skipped signals/messages with reason

  **Must NOT do**:
  - Do NOT add opendbc as git submodule
  - Do NOT vendor all 67 files — curate a representative 10-15
  - Do NOT store opendbc clone in a non-gitignored location
  - Do NOT commit proprietary DBC files

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: File curation, corpus management, graceful error handling, and git clone logic. Medium complexity but many moving parts.
  - **Skills**: [`git-master`]
    - `git-master`: Needed for opendbc cloning logic and vendored file management
  - **Skills Evaluated but Omitted**:
    - `playwright`: No browser involved

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 5, 6)
  - **Blocks**: Task 9
  - **Blocked By**: Task 3

  **References**:

  **Pattern References**:
  - `external_test/README.md` — How external test data is handled in this repo (gitignored, policy)
  - `scripts/fetch_dbcs.md` — Tips and sources for finding public DBC files
  - `.gitignore` — Current gitignore patterns to extend

  **External References**:
  - opendbc repo: `https://github.com/commaai/opendbc` — DBC file source
  - opendbc DBC dir: `https://github.com/commaai/opendbc/tree/master/opendbc/dbc` — 67 DBC files

  **WHY Each Reference Matters**:
  - `external_test/README.md` establishes the project's policy for external test data
  - `scripts/fetch_dbcs.md` may have guidance on which opendbc files are most useful
  - `.gitignore` needs updating to exclude `tmp/opendbc/` and vendor_dbc artifacts

  **Acceptance Criteria**:

  - [ ] `tests/oracle/vendor_dbc/` contains 10-15 DBC files from opendbc
  - [ ] `tests/oracle/vendor_dbc/README.md` exists with source attribution
  - [ ] `python tests/oracle/run_corpus.py --corpus-dir tests/oracle/vendor_dbc --out-dir tmp/oracle_corpus --report-only` exits 0
  - [ ] `tmp/oracle_corpus/corpus_report.json` has pass/fail/skip counts per DBC
  - [ ] `python tests/oracle/run_corpus.py --corpus-dir tests/oracle/vendor_dbc --out-dir tmp/oracle_corpus --clone-opendbc --report-only` clones opendbc and includes additional DBCs (with internet)
  - [ ] Without internet, vendor-only fallback works

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Vendor corpus runs without internet
    Tool: Bash
    Preconditions: vendor_dbc/ populated, no --clone-opendbc flag
    Steps:
      1. python tests/oracle/run_corpus.py --corpus-dir tests/oracle/vendor_dbc --out-dir tmp/oracle_vendor --report-only
      2. Assert: exit code 0
      3. python -c "import json; r=json.load(open('tmp/oracle_vendor/corpus_report.json')); print(f'DBCs tested: {r[\"total\"]}, passed: {r[\"passed\"]}, failed: {r[\"failed\"]}, skipped: {r[\"skipped\"]}')"
      4. Assert: total > 0
    Expected Result: Vendor corpus produces valid report
    Evidence: corpus_report.json content

  Scenario: opendbc clone + vendor combined run
    Tool: Bash
    Preconditions: Internet available
    Steps:
      1. python tests/oracle/run_corpus.py --corpus-dir tests/oracle/vendor_dbc --out-dir tmp/oracle_full --clone-opendbc --report-only
      2. Assert: exit code 0
      3. python -c "import json; r=json.load(open('tmp/oracle_full/corpus_report.json')); print(f'Total: {r[\"total\"]}'); assert r['total'] > 15"
      4. Assert: more DBCs tested than vendor-only
    Expected Result: Combined corpus includes opendbc files
    Evidence: corpus_report.json with higher total count
  ```

  **Commit**: YES
  - Message: `feat(oracle): add vendored DBC subset and corpus runner with opendbc integration`
  - Files: `tests/oracle/vendor_dbc/*`, `tests/oracle/run_corpus.py`
  - Pre-commit: `python tests/oracle/run_corpus.py --help`

---

- [x] 8. pytest Suite for Python Harness

  **What to do**:
  - Create `tests/oracle/tests/` with pytest test files:
    - `test_tolerance.py`:
      - Test `compute_tolerance` with known signal configurations
      - Test integer signals (factor=1, offset=0) return tolerance=0
      - Test small-factor signals return physically meaningful tolerance
      - Test `compare_physical` with edge cases (exact match, within tolerance, outside tolerance)
      - Test `compare_bytes` with ±1 LSB differences
    - `test_vector_gen.py`:
      - Test boundary value generation for unsigned/signed/BE/LE signals
      - Test random value generation stays within [min, max]
      - Test multiplexed message vector generation produces per-branch vectors
      - Test adversarial vector generation includes rounding boundary values
    - `test_harness.py`:
      - Test `generate_harness_c` produces compilable C for a known message
      - Test JSON protocol: encode command → valid JSON response
      - Test JSON protocol: decode command → valid JSON response
      - Test error handling: unknown message → error JSON
    - `test_engine.py`:
      - Integration test: full pipeline with `examples/sample.dbc`
      - Integration test: full pipeline with `examples/comprehensive_test.dbc`
      - Test report structure has required fields
    - `test_metadata.py`:
      - Test metadata extraction from cantools matches known signal properties
      - Test divergence detection (mock a signal with wrong byte_order)
    - `conftest.py`:
      - Shared fixtures: `sample_dbc_path`, `comprehensive_dbc_path`, `default_config_path`
      - Skip markers for slow tests (`@pytest.mark.slow`)

  **Must NOT do**:
  - Do NOT duplicate oracle logic in tests — test the PUBLIC interface
  - Do NOT require internet for unit tests (use fixtures/mocks for opendbc)
  - Do NOT mark integration tests (that need dotnet/gcc) as regular — use `@pytest.mark.integration`

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: Comprehensive test suite covering multiple modules with fixtures, markers, edge cases, and integration tests. Requires understanding the full oracle architecture.
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `playwright`: No browser involved

  **Parallelization**:
  - **Can Run In Parallel**: YES (partially)
  - **Parallel Group**: Wave 4 (with Task 9, but can start after Task 5)
  - **Blocks**: Task 9
  - **Blocked By**: Tasks 3, 5

  **References**:

  **Pattern References**:
  - `tests/oracle/oracle/tolerance.py` (from Task 4) — tolerance API to test
  - `tests/oracle/oracle/vector_gen.py` (from Task 5) — vector gen API to test
  - `tests/oracle/oracle/harness.py` (from Task 2) — harness API to test
  - `tests/oracle/oracle/engine.py` (from Task 3) — engine API to test

  **External References**:
  - pytest docs: `https://docs.pytest.org/en/stable/how-to/fixtures.html` — fixture patterns
  - pytest markers: `https://docs.pytest.org/en/stable/how-to/mark.html` — custom markers

  **WHY Each Reference Matters**:
  - Each oracle module needs tests for its public interface
  - pytest fixtures enable sharing DBC paths and config across tests
  - Markers separate fast unit tests from slow integration tests

  **Acceptance Criteria**:

  - [ ] `pytest tests/oracle/tests/ -v -m "not integration"` passes (unit tests only, no dotnet/gcc needed)
  - [ ] `pytest tests/oracle/tests/ -v` passes (all tests including integration, needs dotnet+gcc)
  - [ ] At least 20 test cases total across all test files
  - [ ] Test coverage for tolerance, vector_gen, harness, and engine modules

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: pytest suite passes
    Tool: Bash
    Preconditions: All oracle modules implemented, dotnet+gcc available
    Steps:
      1. pytest tests/oracle/tests/ -v --tb=short
      2. Assert: exit code 0
      3. Assert: stdout shows ≥20 tests passed
    Expected Result: All tests pass
    Evidence: pytest output

  Scenario: Unit tests run without dotnet/gcc
    Tool: Bash
    Preconditions: Only Python+cantools available
    Steps:
      1. pytest tests/oracle/tests/ -v -m "not integration" --tb=short
      2. Assert: exit code 0, no tests marked as ERROR
    Expected Result: Unit tests pass independently of build tools
    Evidence: pytest output showing only unit tests
  ```

  **Commit**: YES
  - Message: `test(oracle): add comprehensive pytest suite for oracle harness`
  - Files: `tests/oracle/tests/*`
  - Pre-commit: `pytest tests/oracle/tests/ -v -m "not integration"`

---

- [x] 9. Full Integration Test: All Example DBCs + Matrix

  **What to do**:
  - Run the complete oracle pipeline against ALL 7 example DBCs with default config:
    1. `examples/sample.dbc` — basic signals
    2. `examples/comprehensive_test.dbc` — LE/BE/Signed/NonAligned/Packed/Scale
    3. `examples/motorola_lsb_suite.dbc` — Motorola signals (test with `motorola_start_bit=msb` config only against cantools)
    4. `examples/fixed_suite.dbc` — fixed-point scaling
    5. `examples/value_table.dbc` — VAL_ signals
    6. `examples/canfd_test.dbc` — CAN FD 64-byte messages
    7. `examples/multiplex_suite.dbc` — multiplexed messages
  - Run config matrix on `comprehensive_test.dbc` (most diverse signals)
  - Run vendor corpus with default config
  - Fix any failures discovered during integration:
    - If cantools and C disagree, determine which is correct
    - If tolerance is too tight, adjust formula
    - If a DBC feature is unsupported, add to skip list
  - Produce a summary report documenting:
    - Total signals tested
    - Pass/fail/skip counts per category (LE, BE, signed, mux, FD, etc.)
    - Any known divergences (rounding, etc.)
    - Recommendations for future improvements

  **Must NOT do**:
  - Do NOT modify existing F# or C code to fix bugs found — document them as issues
  - Do NOT paper over real failures by loosening tolerance — investigate root cause
  - Do NOT skip motorola_lsb_suite.dbc entirely — test it with msb config (it still has useful BE signals)

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: End-to-end integration testing, debugging failures, root cause analysis, and summary documentation. Requires full system understanding.
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `playwright`: No browser involved

  **Parallelization**:
  - **Can Run In Parallel**: NO (final integration task)
  - **Parallel Group**: Wave 4 (sequential after all others)
  - **Blocks**: None (final)
  - **Blocked By**: Tasks 5, 6, 7, 8

  **References**:

  **Pattern References**:
  - All files created in Tasks 1-8
  - `examples/*.dbc` — all 7 example DBC files
  - `examples/config*.yaml` — all config variants

  **WHY Each Reference Matters**:
  - This task exercises the entire pipeline end-to-end with all inputs

  **Acceptance Criteria**:

  - [ ] All 7 example DBCs pass oracle with default config (or failures are documented as known issues)
  - [ ] Config matrix on comprehensive_test.dbc covers 8 combinations
  - [ ] Vendor corpus report produced with pass/fail/skip counts
  - [ ] Summary report exists at `tests/oracle/ORACLE_RESULTS.md` documenting findings
  - [ ] `pytest tests/oracle/tests/ -v` passes after any tolerance adjustments
  - [ ] No regressions: existing `dotnet test` still passes

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: All example DBCs pass oracle
    Tool: Bash
    Preconditions: Full oracle pipeline implemented and passing
    Steps:
      1. for dbc in examples/sample.dbc examples/comprehensive_test.dbc examples/motorola_lsb_suite.dbc examples/fixed_suite.dbc examples/value_table.dbc examples/canfd_test.dbc examples/multiplex_suite.dbc; do
           python tests/oracle/run_oracle.py --dbc $dbc --config examples/config.yaml --out-dir tmp/oracle_final/$(basename $dbc .dbc) --assert-pass --verbose
         done
      2. Assert: all 7 exit with code 0
    Expected Result: Complete oracle coverage of all example DBCs
    Evidence: Per-DBC report.json files

  Scenario: Config matrix produces comprehensive results
    Tool: Bash
    Preconditions: Matrix runner working
    Steps:
      1. python tests/oracle/run_matrix.py --dbc examples/comprehensive_test.dbc --out-dir tmp/oracle_final_matrix --verbose
      2. Assert: exit code 0
      3. python -c "import json; r=json.load(open('tmp/oracle_final_matrix/matrix_report.json')); configs=len(r['configs']); passed=sum(1 for c in r['configs'] if c['all_passed']); print(f'{passed}/{configs} configs passed')"
      4. Assert: all 8 configs pass (or documented known issues)
    Expected Result: Full config matrix exercised
    Evidence: matrix_report.json

  Scenario: Existing tests not regressed
    Tool: Bash
    Preconditions: dotnet SDK available
    Steps:
      1. dotnet test --configuration Release -v minimal --nologo
      2. Assert: exit code 0, all tests pass
    Expected Result: No regressions in existing F# test suite
    Evidence: dotnet test output
  ```

  **Commit**: YES
  - Message: `feat(oracle): complete integration testing with all example DBCs and config matrix`
  - Files: `tests/oracle/ORACLE_RESULTS.md`, any tolerance/skip adjustments
  - Pre-commit: `dotnet test --configuration Release -v minimal --nologo`

---

## Commit Strategy

| After Task | Message | Key Files | Verification |
|------------|---------|-----------|--------------|
| 1 | `feat(oracle): scaffold Python oracle test project structure` | tests/oracle/**init** | `python run_oracle.py --help` |
| 2 | `feat(oracle): add C harness template and Python harness generation` | tests/oracle/templates/, oracle/harness.py | gcc compile test |
| 3 | `feat(oracle): implement core oracle engine with single-DBC pipeline` | oracle/engine.py, run_oracle.py | `run_oracle.py --dbc sample.dbc --assert-pass` |
| 4 | `feat(oracle): implement dynamic tolerance framework and metadata comparison` | oracle/tolerance.py, oracle/metadata_compare.py | tolerance unit test |
| 5 | `feat(oracle): implement test vector generation with boundary and adversarial cases` | oracle/vector_gen.py | vector gen unit test |
| 6 | `feat(oracle): implement config matrix runner with all valid permutations` | run_matrix.py | matrix on sample.dbc |
| 7 | `feat(oracle): add vendored DBC subset and corpus runner with opendbc integration` | vendor_dbc/, run_corpus.py | corpus report |
| 8 | `test(oracle): add comprehensive pytest suite for oracle harness` | tests/oracle/tests/* | pytest passes |
| 9 | `feat(oracle): complete integration testing with all example DBCs and config matrix` | ORACLE_RESULTS.md | all DBCs pass |

---

## Success Criteria

### Verification Commands
```bash
# 1. Python environment
pip install -r tests/oracle/requirements.txt
python -c "import cantools; print(cantools.__version__)"  # Expected: version string

# 2. Single DBC oracle
python tests/oracle/run_oracle.py --dbc examples/sample.dbc --config examples/config.yaml --out-dir tmp/oracle --assert-pass
# Expected: exit 0

# 3. Comprehensive DBC oracle
python tests/oracle/run_oracle.py --dbc examples/comprehensive_test.dbc --config examples/config.yaml --out-dir tmp/oracle --assert-pass
# Expected: exit 0

# 4. Config matrix
python tests/oracle/run_matrix.py --dbc examples/comprehensive_test.dbc --out-dir tmp/oracle_matrix
# Expected: exit 0, 8 configs tested

# 5. Vendor corpus
python tests/oracle/run_corpus.py --corpus-dir tests/oracle/vendor_dbc --out-dir tmp/oracle_corpus --report-only
# Expected: exit 0, report with pass/fail/skip

# 6. pytest suite
pytest tests/oracle/tests/ -v
# Expected: ≥20 tests pass

# 7. No regressions
dotnet test --configuration Release -v minimal --nologo
# Expected: all existing tests pass
```

### Final Checklist
- [x] All "Must Have" items present
- [x] All "Must NOT Have" guardrails respected
- [x] All 7 example DBCs pass oracle with default config
- [x] Config matrix covers 8 valid combinations
- [x] Vendor DBC corpus produces structured report
- [x] pytest suite passes
- [x] No modifications to existing F#, C, or CI files
- [x] ORACLE_RESULTS.md documents findings and known divergences
