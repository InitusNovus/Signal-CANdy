# Oracle Test Pipeline - Integration Results

**Date**: 2026-02-13T10:45:39Z  
**Commit**: `a64fb45`

## Executive Summary

- **Total tests executed (examples + matrix + corpus)**: 97,346
- **Total signal-runs covered**: 4,975 (4,847 from examples+corpus, plus 128 from 8-config matrix)
- **Passed**: 89,986 (92.44%)
- **Failed**: 7,277 (7.48%)
- **Skipped**: 83 (0.09%)
- **Key outcome**: All 7 example DBC runs completed; 5/7 fully passed, 2/7 are skipped-only due to current single-config multiplex handling.

## Test Coverage Breakdown

### Example DBCs (7 files)

| DBC File | Messages | Signals | Passed | Failed | Skipped | Notes |
|----------|----------|---------|--------|--------|---------|-------|
| `sample.dbc` | 1 | 2 | 42 | 0 | 0 | Baseline LE/basic signals passed. |
| `comprehensive_test.dbc` | 6 | 16 | 588 | 0 | 0 | LE/BE/signed/non-aligned/packed/scale all passed. |
| `motorola_lsb_suite.dbc` | 1 | 4 | 138 | 0 | 0 | Ran with `motorola_start_bit=msb` path as required. |
| `fixed_suite.dbc` | 2 | 5 | 138 | 0 | 0 | Fixed-point scaling coverage passed. |
| `value_table.dbc` | 1 | 4 | 0 | 0 | 4 | Skipped: message is multiplexed in single-config mode. |
| `canfd_test.dbc` | 1 | 3 | 105 | 0 | 0 | CAN FD payload path covered. |
| `multiplex_suite.dbc` | 1 | 4 | 0 | 0 | 4 | Skipped: multiplexed message in single-config mode. |

### Config Matrix (8 configurations on `comprehensive_test.dbc`)

| Config | phys_type | phys_mode | range_check | Passed | Failed | Skipped |
|--------|-----------|-----------|-------------|--------|--------|---------|
| 0 | float | double | false | 588 | 0 | 0 |
| 1 | float | double | true | 588 | 0 | 0 |
| 2 | float | float | false | 588 | 0 | 0 |
| 3 | float | float | true | 588 | 0 | 0 |
| 4 | fixed | fixed_double | false | 588 | 0 | 0 |
| 5 | fixed | fixed_double | true | 588 | 0 | 0 |
| 6 | fixed | fixed_float | false | 588 | 0 | 0 |
| 7 | fixed | fixed_float | true | 588 | 0 | 0 |

- Matrix summary: **8/8 configs passed**, **4,704 passed**, **0 failed**, **0 skipped**.

### Vendor Corpus (15 DBCs)

- **Total DBCs**: 15
- **Passed**: 5
- **Failed**: 10
- **Skipped**: 0
- **Top failing files by failed test count**:
  - `tesla_can.dbc` (3,594 failed)
  - `ford_fusion_2018_pt.dbc` (1,188 failed)
  - `chrysler_pacifica_2017_hybrid_private_fusion.dbc` (1,005 failed)
  - `ford_lincoln_base_pt.dbc` (641 failed)
  - `bmw_e9x_e8x.dbc` (504 failed)

## Signal Category Analysis (examples + corpus)

| Category | Tests | Passed | Failed | Skipped | Pass Rate |
|----------|-------|--------|--------|---------|-----------|
| Little-endian (LE) | 27,608 | 24,219 | 3,306 | 83 | 87.72% |
| Big-endian (BE) | 65,031 | 61,063 | 3,968 | 0 | 93.90% |
| Signed integers | 4,521 | 4,309 | 212 | 0 | 95.31% |
| Multiplexed | 83 | 0 | 0 | 83 | 0.00% (currently skipped) |
| CAN FD | 1,221 | 824 | 397 | 0 | 67.49% |
| Fixed/scaled | 27,610 | 25,802 | 1,786 | 22 | 93.45% |
| Value table signals | 27,642 | 26,589 | 1,050 | 3 | 96.19% |

## Known Divergences

### 1) Rounding strategy differences

- **Issue**: Signal-CANdy and cantools can diverge at exact half-step boundaries.
- **Mitigation in place**: byte comparison allows `+/-1` LSB.
- **Observed frequency**: no byte mismatch regressions in examples/matrix; corpus still shows value-level mismatches (`encode/decode value differs from input`) 227 times, mixed with other vendor-specific failures.

### 2) Float precision differences

- **Issue**: C-side uses float32 value paths while reference calculations may retain higher precision.
- **Mitigation in place**: dynamic tolerance (`max(abs(factor)*0.5, abs(expected)*FLT_EPSILON*8)`).
- **Observed frequency**: examples and matrix remained stable; precision-sensitive failures appear in corpus alongside message-level encode/decode failures.

## Failures Requiring Investigation

1. **Message-level encode/decode failures on vendor corpus**
   - Frequent errors: `encode failed for <message>`, `decode failed for <message>`.
   - Concentrated in Tesla/Ford/Chrysler/BMW datasets.

2. **Vector generation edge overflows for some vendor signals**
   - Example failure text: `cantools encode failed: int too big to convert` and signed out-of-range values.

3. **Cantools parse incompatibilities for specific vendor files**
   - `hyundai_kia_generic.dbc`: invalid DBC syntax in comment block.
   - `toyota_2017_ref_pt.dbc`, `vw_meb.dbc`: frame IDs interpreted as invalid standard IDs by cantools.

4. **Current multiplex handling in single-config mode**
   - `value_table.dbc` and `multiplex_suite.dbc` produce skip-only outcomes in `run_oracle.py` path.

## Recommendations

1. Add corpus-specific prefilters for unsupported parse patterns (syntax anomalies, 29-bit ID handling) to classify as known-skip before pipeline fail.
2. Extend vector generation bounds for large signed/scaled vendor signals to reduce invalid test input generation.
3. Add targeted multiplex execution mode to single-DBC oracle run path, so multiplex suites are tested instead of skipped.
4. Introduce CI split: keep examples+matrix as required gate, track corpus pass-rate and failure taxonomy as non-blocking trend metric.

## Reproduction Commands

### Run oracle on single DBC

```bash
python tests/oracle/run_oracle.py --dbc examples/sample.dbc --config examples/config.yaml --out-dir tmp/oracle_single --verbose
```

### Run config matrix

```bash
python tests/oracle/run_matrix.py --dbc examples/comprehensive_test.dbc --out-dir tmp/oracle_matrix --verbose
```

### Run vendor corpus

```bash
python tests/oracle/run_corpus.py --corpus-dir tests/oracle/vendor_dbc --out-dir tmp/oracle_corpus --config examples/config.yaml --verbose
```

### Run full integration test (all 7 DBCs)

```bash
for dbc in examples/sample.dbc examples/comprehensive_test.dbc examples/motorola_lsb_suite.dbc examples/fixed_suite.dbc examples/value_table.dbc examples/canfd_test.dbc examples/multiplex_suite.dbc; do
  python tests/oracle/run_oracle.py --dbc $dbc --config examples/config.yaml --out-dir tmp/oracle_final/$(basename $dbc .dbc) --verbose
done
```
