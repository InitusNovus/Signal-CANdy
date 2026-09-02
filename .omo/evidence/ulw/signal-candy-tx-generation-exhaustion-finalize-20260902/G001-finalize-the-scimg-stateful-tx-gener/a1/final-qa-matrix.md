# Final Manual QA Matrix — TX Generation Exhaustion

Frozen identities:

- Signal-CANdy HEAD `07d8883cb164b715d88d1d38464e64020dfdd437`
- Signal-CANdy tree `9201457f0d2c3fe11304a8fdc5c50ea89bbd4c73`
- implementation commit `7861508b6778703f0c3a4a6e954cb5b80f8b2e1f`
- RuntimeTest HEAD `b423160e34cc9f406838aca523b26e4929172e0e`
- firmware BIN SHA-256 `f2893951aa3247ace7888102a5de2834f1b19278e30cea5b0b53f8380ee466c5`
  (69,496 bytes, deterministic across repeated clean builds)

## Surface evidence (channel: CLI)

| id | criterion | invocation | verdict | artifacts |
| --- | --- | --- | --- | --- |
| surface-protection | C001 | `bash runtime/c99/tests/run_protection_tests.sh` | passed (21/21) | a1/C001-runtime-exhaustion.txt |
| surface-tx | C001 | `bash runtime/c99/tests/run_tx_tests.sh` | passed (15/15) | a1/C001-runtime-exhaustion.txt |
| surface-managed | C003 | `dotnet test --configuration Release --no-build` | passed (449, 2 platform skips) | Reports/20260902-1014_SCIMG_TX_Generation_통합완료.md |
| surface-fantomas | C003 | `fantomas --check src/ tests/` | passed | Reports/20260902-1014_SCIMG_TX_Generation_통합완료.md |
| surface-contract | C002 | `host/test_p0_p1_limit_contract.py` | passed (4/4) | a1/C002-canonical-integration.txt |
| surface-expect | C002 | `host/sc_validate_p0_p1_gas.py --list-expectations` | passed (EXPECT-only, zero PASS) | a1/C002-canonical-integration.txt |
| surface-sync | C002 | `tools/sync_signal_candy.py --fixture rx --overwrite-runtime` | passed (drift 0, fixtures unchanged) | a1/C002-canonical-integration.txt |

## Surface evidence (channel: hardware — CLI receipt-bound)

| id | criterion | invocation | verdict | artifacts |
| --- | --- | --- | --- | --- |
| surface-flash | C003 | TT_Host `firmware update --port COM22 --file sc_runtime_test.bin` | passed (single flash, Succeeded/NoError) | a1/receipts/final-authoritative-session.txt |
| surface-can3 | C003 | `python host/sc_validate_p0_p1_gas.py --interface CAN3` | passed (50 PASS, ALL PASS, exit 0) | a1/receipts/final-authoritative-session.txt |

The authoritative session transcript binds, in one continuous record: pre-flash
BIN identity (path, SHA-256, 69,496 bytes), linker FLASH ORIGIN `0x08008000`,
exact connect/flash/release/reset invocations on COM22, the single
FirmwareUpdate operation `21d44c65`, the same-session CAN3 acceptance run, and
disconnect `65cbb9b6`.

## Adversarial cases

| id | criterion | scenario | expected behavior | verdict | artifacts |
| --- | --- | --- | --- | --- | --- |
| adv-wrap-reuse | C001 | restore old wrap-to-1 logic | protection suite fails | passed (mutation detected, 1/21 fail) | a1/C001-runtime-exhaustion.txt |
| adv-busy-precedence | C001 | check LIMIT before pending BUSY | protection suite fails | passed (mutation detected) | a1/C001-runtime-exhaustion.txt |
| adv-reset-epoch | C001 | copied token aliasing generation 1 after reset | rejected inside epoch; cross-reset reuse documented as requiring token-copy revocation | passed | a1/final-code-review.md |
| adv-ordinary-error | C002 | SC_ERR_POOL probe before exhaustion | ordinary failure counter increments, no latch | passed (on-wire receipt) | a1/receipts/final-authoritative-session.txt |
| adv-latch-persistence | C002 | repeated probes and heartbeat after exhaustion | LIMIT + maintenance bit, attempt count frozen, latch survives heartbeat | passed (3 receipts) | a1/receipts/final-authoritative-session.txt |

## Cleanup receipts

- `PROVENANCE SESSION CLEANUP PASS`: firmware build cleaned, host `__pycache__`
  removed, TT_Host daemon stopped, related process scan empty.
- NI-XNET sessions closed by runner context managers; disconnect `65cbb9b6`
  Succeeded/NoError.
- No ST-Link/SWD operation at any point.
- Claim `cd519cfa-20a5-4570-a0cb-c9a56304018b` retained active per plan.
