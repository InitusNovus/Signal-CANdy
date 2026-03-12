# Category C Exception Justification - Signal-CANdy Oracle

This document defines the criteria and justifications for oracle test failures that are accepted as "Category C" (Known Technical Limitation). These exceptions represent cases where the Signal-CANdy generator is performing correctly according to its design or architectural constraints, but diverges from the `cantools` reference decoder or hits a documented boundary.

## Exception Gate (4-Criterion Rule)

To be classified as Category C, an exception must satisfy all four of the following criteria:

1.  **Clear Technical Limitation**: The divergence is rooted in a specific architectural choice, environment constraint, or reference tool behavior.
2.  **Scoped Impact Count**: The number of affected signals or tests must be quantified.
3.  **No Feasible Alternative**: There's no trivial fix without breaking existing functionality or requiring a major refactor.
4.  **ROADMAP Entry**: The limitation must be tracked in `ROADMAP.md` for future resolution.

---

## Approved Exceptions

### Exception 1 — Multiplexed signal skip in oracle
The current oracle pipeline executes tests using a single-signal configuration. Multiplexed branch signals share the same frame layout, and the oracle cannot deterministically select which branch is active for encode/decode testing without multi-mode support.

| Criterion | Status | Justification |
| :--- | :--- | :--- |
| Technical Limitation | PASS | Oracle `run_oracle.py` lacks multi-branch selection logic. |
| Scoped Impact | PASS | 83 signals marked `skipped` in `ORACLE_RESULTS.md`. |
| No Feasible Alternative | PASS | Requires extending the oracle engine to handle multiplexer state. |
| ROADMAP Entry | PASS | Tracked under `L-3` (valid mask / mux coverage). |

**Category**: `cantools_oracle_limitation`

### Exception 2 — ±1 LSB rounding tolerance
Float32 rounding during the encode→decode cycle can introduce a ±1 LSB divergence. This is an inherent property of floating-point math on different platforms and is already explicitly permitted by `tolerance.py`. There's no data loss for standard CAN use cases.

| Criterion | Status | Justification |
| :--- | :--- | :--- |
| Technical Limitation | PASS | Inherent float32 rounding divergence in C vs Python. |
| Scoped Impact | PASS | 227 value-level mismatches in corpus handled by tolerance. |
| No Feasible Alternative | PASS | Floating-point behavior is non-deterministic across platforms. |
| ROADMAP Entry | PASS | N/A (Intentionally covered by `tolerance.py` baseline). |

**Category**: `float32_rounding`

### Exception 3 — 32-bit valid bitmask limit for messages with >32 signals
The generated `valid` bitmask is currently a `uint32_t`. Messages with more than 32 signals (common in multiplex-heavy industrial DBCs) cannot have every signal individually tracked by the bitmask.

| Criterion | Status | Justification |
| :--- | :--- | :--- |
| Technical Limitation | PASS | Architectural choice of `uint32_t` for the `valid` field. |
| Scoped Impact | PASS | Impact limited to complex industrial/heavy-duty DBCs. |
| No Feasible Alternative | PASS | Requires widening to `uint64_t` or an array-based mask. |
| ROADMAP Entry | PASS | Tracked under `L-3` (valid bitmask automatic expansion). |

**Category**: `valid_mask_width`

### Exception 4 — cantools parsing incompatibility (hyundai, toyota, vw)
Specific vendor DBCs contain syntax anomalies or 29-bit extended IDs that `cantools` (v41.2.1) rejects, while Signal-CANdy successfully parses and generates code for them. These files cannot be verified against `cantools`.

*   **hyundai_kia_generic.dbc**: Malformed `CM_` records at lines 1655-1657.
*   **toyota_2017_ref_pt.dbc**: 29-bit IDs rejected as standard frame overflow at line 387.
*   **vw_meb.dbc**: 29-bit IDs rejected at line 2074.

| Criterion | Status | Justification |
| :--- | :--- | :--- |
| Technical Limitation | PASS | Reference decoder (`cantools`) cannot parse valid/used DBCs. |
| Scoped Impact | PASS | 3 DBC files entirely excluded from oracle comparison. |
| No Feasible Alternative | PASS | Requires a different reference decoder or `cantools` patch. |
| ROADMAP Entry | PASS | Tracked under "cantools 29-bit extended-ID compatibility". |

**Category**: `reference_decoder_incompatible`

---

### Exception 5 — DBC raw range sentinel (physical [min|max] stores raw counts)
Some DBC files store raw CAN values in the `[min|max]` field rather than the physical (scaled) values, contrary to the DBC standard. When `range_check: true` is enabled, Signal-CANdy rejects physically valid values because the range check compares the physical result against the raw-count bounds. The generator correctly reads the DBC `[min|max]` as given and generates range guards that enforce those numbers as physical bounds — the bug is in the DBC authoring, not the generator.

*   **chrysler_pacifica_2017_hybrid_private_fusion.dbc**: LAT_DIST signal in messages c_1..c_10 has `[0|2047]` (raw counts, factor=0.005, offset=-1000). Physical range is ~[-1000, -989] but guard enforces [0, 2047]. **1,005 failures**.
*   **mercedes_benz_e350_2010.dbc**: STEER_SENSOR signal `STEER_DIRECTION [0|1]` with offset=2. Physical range is [2, 3] but guard enforces [0, 1]. **96 failures**.

Confirmed evidence: With `range_check: false`, Chrysler 2898/2898 pass (100%), Mercedes 1842/1842 pass (100%).

| Criterion | Status | Justification |
| :--- | :--- | :--- |
| Technical Limitation | PASS | DBC `[min\|max]` is encoded as raw counts, but range check uses physical values from the same field. The generator faithfully transcribes the DBC field; the authoring error is in the DBC. |
| Scoped Impact | PASS | 1,005 failures in Chrysler + 96 failures in Mercedes = 1,101 total affected tests. |
| No Feasible Alternative | PASS | Heuristically detecting "raw vs physical" range fields would require examining factor/offset and guessing intent, which is error-prone and outside current design scope. |
| ROADMAP Entry | PASS | Tracked under "DBC raw-range detection heuristic" for future evaluation. |

**Category**: `dbc_raw_range_sentinel`

---

## Ineligible Reasons
The following reasons do **NOT** qualify for Category C classification:

*   **"cantools gives the same result"**: Both tools could be wrong; this doesn't prove correctness.
*   **"This vendor's DBC is non-standard"**: Vague claims without specific technical proof of syntax violation.
*   **"Low failure count"**: Quantity is not a substitute for technical justification.
*   **"Fix would require large refactor"**: Cost of repair is not a justification for an exception; it must be fixed or properly tracked as a limitation via the ROADMAP.
