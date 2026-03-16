# Oracle Reference Decoder Incompatibility Strategy

## Problem Statement
Three vendor DBC files from the testing corpus are currently excluded from oracle verification because the primary reference decoder, `cantools` (v41.2.1), fails to parse them. These files were classified as Exception 4 in `tests/oracle/CATEGORY_C_EXCEPTIONS.md`.

**Affected Files and Errors:**
1.  **hyundai_kia_generic.dbc**: Malformed `CM_` records at lines 1655-1657. `cantools` fails to handle unexpected formatting in comment records.
2.  **toyota_2017_ref_pt.dbc**: 29-bit extended IDs rejected at line 387. `cantools` throws a "Standard frame id is more than 11 bits" error, suggesting it incorrectly treats extended frames as standard frames or fails to recognize the bit-31 extended flag.
3.  **vw_meb.dbc**: 29-bit extended IDs rejected at line 2074. Same root cause as the Toyota DBC.

Signal-CANdy successfully parses these files and generates valid C99 code, but the lack of a reference decoder prevents signal-level oracle verification for these specific targets.

## Current State and Impact
- **Exclusion Rate**: 3 significant vendor DBC files (Hyundai, Toyota, VW) are entirely excluded from the automated oracle pipeline.
- **Verification Gap**: Changes to core logic affecting extended IDs or specific parsing edge cases cannot be cross-validated against a gold standard for these files.
- **Classification**: These files are permanently assigned to `Category C` (reference\_decoder\_incompatible), which lowers the effective oracle coverage of the project.

## Decision Context
The project needs to decide whether to invest in a more robust or alternative reference decoder to achieve 100% oracle coverage or maintain the current status quo where `cantools` remains the sole, albeit limited, source of truth.

## Option A: Maintain cantools-only + Category C policy (Status Quo)
### Description
Continue using `cantools` v41.2.1 as the exclusive reference decoder. Files that fail to parse are documented as Category C exceptions and excluded from oracle runs.

### Evidence/Rationale
`cantools` is the industry standard for Python-based CAN toolchains. Exception 4 documents that these failures are external to Signal-CANdy.

### Pros
- Zero additional implementation or maintenance effort.
- Consistent environment (only one reference tool to manage).
- Clear boundary: if the industry standard cannot parse it, exclusion is defensible.

### Cons
- Permanent verification gaps for high-value vendor DBCs.
- Reduced confidence in generated code for extended ID messages (J1939/CAN FD style).

### Effort: None
### Recommendation Score: 6/10

## Option B: Replace cantools with an alternative reference decoder (e.g., canmatrix)
### Description
Migrate the `run_oracle.py` and `engine.py` logic to use `canmatrix` or a similar library as the primary reference.

### Evidence/Rationale
Research indicates `canmatrix` is generally more lenient with DBC syntax anomalies (like the Hyundai `CM_` issue) and has a more flexible internal model for extended IDs.

### Pros
- Potential to resolve all 3 current Exception 4 cases.
- `canmatrix` supports a wider range of formats (.arxml, .kcd), potentially expanding future test scope.

### Cons
- High migration effort: `run_oracle.py` is tightly coupled with the `cantools` API.
- `canmatrix` is often cited as being slower and having a less "mature" high-level API than `cantools`.
- Risk of introducing new Category C exceptions for files `cantools` handled but `canmatrix` might struggle with.

### Effort: High
### Recommendation Score: 3/10

## Option C: Hybrid approach (cantools with fallback or canmatrix-based patching)
### Description
Keep `cantools` as the primary engine but implement a secondary path for Exception 4 files. This could involve using `canmatrix` just for the problematic files or implementing a pre-processing script to "fix" the DBCs before feeding them to `cantools`.

### Evidence/Rationale
The Toyota/VW errors ("Standard frame id is more than 11 bits") often stem from DBCs lacking the bit-31 flag that tools like `cantools` expect for extended IDs. A simple Python script could set these flags or clean malformed `CM_` records.

### Pros
- Resolves the verification gap for the 3 current files.
- Retains the performance and stability of `cantools` for the bulk of the corpus.
- Incremental effort.

### Cons
- Increases complexity of the test harness.
- "Fixing" DBCs for the reference tool might mask genuine parsing bugs if not handled carefully.

### Effort: Medium
### Recommendation Score: 8/10

## Recommended Path
**Implement Option C (Hybrid/Pre-processing fallback).**

Evidence suggests the current failures are solvable with targeted interventions:
1.  **For Extended IDs (Toyota/VW)**: A pre-processing step can identify IDs > 0x7FF and ensure the extended bit is set (or use `canmatrix` to load and re-save the DBC in a "standardized" format).
2.  **For Malformed CM_ (Hyundai)**: A simple regex-based "sanitizer" can fix the syntax anomalies before `cantools` reads the file.

This approach provides the highest verification gain (resolving all 3 exclusions) with significantly less risk and effort than a full migration to a new decoder.

## Decision Criteria
- **Coverage**: Does it resolve the 3 excluded files?
- **Stability**: Does it preserve existing verification for the rest of the corpus?
- **Maintenance**: How much extra code is added to the test suite?
- **Speed**: Does it impact CI run times?

## Open Questions
- Can a simple `sed`/`regex` fix the Hyundai `CM_` records without changing signal data?
- Does `cantools` have an undocumented "lenient" mode that could be enabled via flags?
- Is the extended ID issue in Toyota/VW DBCs a bug in the DBC files themselves (missing flag) or a strictness issue in `cantools`?

## References
- `tests/oracle/CATEGORY_C_EXCEPTIONS.md` Exception 4
- `tests/oracle/ORACLE_RESULTS.md` Remaining Category C, Recommendation #4
- `Plans/ROADMAP.md` Oracle decoder strategy item
- [cantools Issue #301: Standard frame id is more than 11 bits](https://github.com/cantools/cantools/issues/301)
- [canmatrix documentation: Lenient DBC parsing](https://canmatrix.readthedocs.io/en/latest/formats.html)
