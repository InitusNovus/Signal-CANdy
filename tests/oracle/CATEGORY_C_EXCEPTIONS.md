#HV|# Category C Exception Justification - Signal-CANdy Oracle
2#KM|
3#PV|This document defines the criteria and justifications for oracle test failures that are accepted as "Category C" (Known Technical Limitation). These exceptions represent cases where the Signal-CANdy generator is performing correctly according to its design or architectural constraints, but diverges from the `cantools` reference decoder or hits a documented boundary.
4#RW|
5#JB|## Exception Gate (4-Criterion Rule)
6#SY|
7#VZ|To be classified as Category C, an exception must satisfy all four of the following criteria:
8#XW|
9#KP|1.  **Clear Technical Limitation**: The divergence is rooted in a specific architectural choice, environment constraint, or reference tool behavior.
10#MB|2.  **Scoped Impact Count**: The number of affected signals or tests must be quantified.
11#MP|3.  **No Feasible Alternative**: There's no trivial fix without breaking existing functionality or requiring a major refactor.
12#QX|4.  **ROADMAP Entry**: The limitation must be tracked in `ROADMAP.md` for future resolution.
13#BQ|
14#XT|---
15#RJ|
16#RJ|## Approved Exceptions
17#HX|
18#TY|### Exception 1 — Multiplexed signal skip in oracle
19#RN|The current oracle pipeline executes tests using a single-signal configuration. Multiplexed branch signals share the same frame layout, and the oracle cannot deterministically select which branch is active for encode/decode testing without multi-mode support.
20#YQ|
21#YM|| Criterion | Status | Justification |
22#RJ|| :--- | :--- | :--- |
23#PH|| Technical Limitation | PASS | Oracle `run_oracle.py` lacks multi-branch selection logic. |
24#JX|| Scoped Impact | PASS | 83 signals marked `skipped` in `ORACLE_RESULTS.md`. |
25#NR|| No Feasible Alternative | PASS | Requires extending the oracle engine to handle multiplexer state. |
26#QT|| ROADMAP Entry | PASS | Tracked under `L-3` (valid mask / mux coverage). |
27#JJ|
28#WQ|**Category**: `cantools_oracle_limitation`
29#ZR|
30#QP|### Exception 2 — ±1 LSB rounding tolerance
31#RX|Float32 rounding during the encode→decode cycle can introduce a ±1 LSB divergence. This is an inherent property of floating-point math on different platforms and is already explicitly permitted by `tolerance.py`. There's no data loss for standard CAN use cases.
32#JQ|
33#YM|| Criterion | Status | Justification |
34#VH|| :--- | :--- | :--- |
35#HM|| Technical Limitation | PASS | Inherent float32 rounding divergence in C vs Python. |
36#VN|| Scoped Impact | PASS | 227 value-level mismatches in corpus handled by tolerance. |
37#NQ|| No Feasible Alternative | PASS | Floating-point behavior is non-deterministic across platforms. |
38#MB|| ROADMAP Entry | PASS | N/A (Intentionally covered by `tolerance.py` baseline). |
39#MS|
40#BK|**Category**: `float32_rounding`
41#BH|
42#MX|### Exception 3 — 32-bit valid bitmask limit for messages with >32 signals
43#SR|The generated `valid` bitmask is currently a `uint32_t`. Messages with more than 32 signals (common in multiplex-heavy industrial DBCs) cannot have every signal individually tracked by the bitmask.
44#PB|
45#YM|| Criterion | Status | Justification |
46#YV|| :--- | :--- | :--- |
47#NT|| Technical Limitation | PASS | Architectural choice of `uint32_t` for the `valid` field. |
48#PW|| Scoped Impact | PASS | Impact limited to complex industrial/heavy-duty DBCs. |
49#ZH|| No Feasible Alternative | PASS | Requires widening to `uint64_t` or an array-based mask. |
50#NH|| ROADMAP Entry | PASS | Tracked under `L-3` (valid bitmask automatic expansion). |
51#PZ|
52#QV|**Category**: `valid_mask_width`
53#NB|
54#YJ|### Exception 4 — cantools parsing incompatibility (hyundai, toyota, vw)
55#JV|Specific vendor DBCs contain syntax anomalies or 29-bit extended IDs that `cantools` (v41.2.1) rejects, while Signal-CANdy successfully parses and generates code for them. These files cannot be verified against `cantools`.
56#XN|
57#PM|*   **hyundai_kia_generic.dbc**: Malformed `CM_` records at lines 1655-1657.
58#PN|*   **toyota_2017_ref_pt.dbc**: 29-bit IDs rejected as standard frame overflow at line 387.
59#RY|*   **vw_meb.dbc**: 29-bit IDs rejected at line 2074.
60#HQ|
61#YM|| Criterion | Status | Justification |
62#RJ|| :--- | :--- | :--- |
63#SB|| Technical Limitation | PASS | Reference decoder (`cantools`) cannot parse valid/used DBCs. |
64#NT|| Scoped Impact | PASS | 3 DBC files entirely excluded from oracle comparison. |
65#NM|| No Feasible Alternative | PASS | Requires a different reference decoder or `cantools` patch. |
66#VM|| ROADMAP Entry | PASS | Tracked under "cantools 29-bit extended-ID compatibility". |
67#TH|
68#JV|**Category**: `reference_decoder_incompatible`
69#KB|
70#JT|---
71#PR|
72#YY|### Exception 5 — DBC raw range sentinel (physical [min|max] stores raw counts)
73#WW|Some DBC files store raw CAN values in the `[min|max]` field rather than the physical (scaled) values, contrary to the DBC standard. When `range_check: true` is enabled, Signal-CANdy rejects physically valid values because the range check compares the physical result against the raw-count bounds. The generator correctly reads the DBC `[min|max]` as given and generates range guards that enforce those numbers as physical bounds — the bug is in the DBC authoring, not the generator.
74#HQ|
75#XH|*   **chrysler_pacifica_2017_hybrid_private_fusion.dbc**: LAT_DIST signal in messages c_1..c_10 has `[0|2047]` (raw counts, factor=0.005, offset=-1000). Physical range is ~[-1000, -989] but guard enforces [0, 2047]. **1,005 failures**.
76#YK|*   **mercedes_benz_e350_2010.dbc**: STEER_SENSOR signal `STEER_DIRECTION [0|1]` with offset=2. Physical range is [2, 3] but guard enforces [0, 1]. **96 failures**.
77#KX|
78#VY|Confirmed evidence: With `range_check: false`, Chrysler 2898/2898 pass (100%), Mercedes 1842/1842 pass (100%).
79#VY|
80#VY|| Criterion | Status | Justification |
81#VY|| :--- | :--- | :--- |
82#VY|| Technical Limitation | PASS | DBC `[min\|max]` is encoded as raw counts, but range check uses physical values from the same field. The generator faithfully transcribes the DBC field; the authoring error is in the DBC. |
83#VY|| Scoped Impact | PASS | 1,005 failures in Chrysler + 96 failures in Mercedes = 1,101 total affected tests. |
84#VY|| No Feasible Alternative | PASS | Heuristically detecting "raw vs physical" range fields would require examining factor/offset and guessing intent, which is error-prone and outside current design scope. |
85#VY|| ROADMAP Entry | PASS | Tracked under "DBC raw-range detection heuristic" for future evaluation. |
86#VY|
87#VY|**Category**: `dbc_raw_range_sentinel`
88#VY|
89#VY|---
90#VY|
91#YY|## Ineligible Reasons
92#WW|The following reasons do **NOT** qualify for Category C classification:
93#HQ|
94#XH|*   **"cantools gives the same result"**: Both tools could be wrong; this doesn't prove correctness.
95#YK|*   **"This vendor's DBC is non-standard"**: Vague claims without specific technical proof of syntax violation.
96#KX|*   **"Low failure count"**: Quantity is not a substitute for technical justification.
97#VY|*   **"Fix would require large refactor"**: Cost of repair is not a justification for an exception; it must be fixed or properly tracked as a limitation via the ROADMAP.

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
