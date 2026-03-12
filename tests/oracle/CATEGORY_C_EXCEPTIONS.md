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

## Ineligible Reasons
The following reasons do **NOT** qualify for Category C classification:

*   **"cantools gives the same result"**: Both tools could be wrong; this doesn't prove correctness.
*   **"This vendor's DBC is non-standard"**: Vague claims without specific technical proof of syntax violation.
*   **"Low failure count"**: Quantity is not a substitute for technical justification.
*   **"Fix would require large refactor"**: Cost of repair is not a justification for an exception; it must be fixed or properly tracked as a limitation via the ROADMAP.
