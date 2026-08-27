# Issue #24 Inspect / Map / Diff Host 증거 보고서

## 📝 작업 요약

Signal-CANdy `df7ae2dca5b6199cbab65a47fef0572b875d78ba`의 실제 Release CLI로 issue #23 A/B fixture를 clean build하고, stable `sc.inspect/v1`, `sc.map/v1`, `sc.diff/v1` host evidence를 생성했다. A/B SCIMG는 444 bytes와 기존 SHA-256을 정확히 유지했다. 이 작업은 host artifact/documentation only이며 hardware, daemon, NI-XNET, flash, sibling repository, C runtime, SCIMG fixture bytes, GitHub, commit을 변경하거나 실행하지 않았다.

결론은 다음과 같다.

- Mapped A -> B와 B -> A: `compatible-reset-required`, reason `schema-content-changed`, 18개 resource 모두 delta 0, 정확히 3개 entity 변경.
- Mapless A -> B: `unknown-without-map`, reason `source-map-missing`, attributed changes 없음.
- A -> A: `identical`, reason/changes 없음.
- Issue #23의 target runtime과 A/B image가 그대로이므로 reflash는 필요하지 않다. 기존 one-flash HIL 결과 `Reports/20260827-2120_CC1A_Activation_HIL_검증.md`를 host 문서가 정확히 설명하며, 이번 작업에서 bench를 다시 열지 않았다.

## 🛠 변경 상세

### Project 및 checked-in evidence

`examples/scimg_activation_demo/project_{a,b}.yaml`에 `outputs.map`을 추가했다. README에는 clean build, inspect stdout/`--out`, mapped directional diff, mapless/identical diff, canonical format, create-only atomic publication과 exit contract를 기록했다.

Project-generated map:

| Path | Bytes | SHA-256 |
|---|---:|---|
| `examples/scimg_activation_demo/build/schema_a.map.json` | 6,831 | `ca5587b579b549c44538178345996595134c4cef69b466409bbb88c692e8a0e6` |
| `examples/scimg_activation_demo/build/schema_b.map.json` | 6,831 | `5aed2d365738e5b7c7206638ffe342cee9e79b8fccb8c636d4af8898788d6d85` |

Canonical issue #24 evidence:

| Path | Bytes | SHA-256 |
|---|---:|---|
| `examples/scimg_activation_demo/evidence/schema_a.inspect.json` | 6,800 | `f58105c9a574b4cd6a5b062e800647be0c5d0789bf98cb9f60d76b8aa864aa7c` |
| `examples/scimg_activation_demo/evidence/schema_b.inspect.json` | 6,800 | `cb7613c17fde3348d0850f6fadbf30357cdfccc344442a79cfb0739809454acd` |
| `examples/scimg_activation_demo/evidence/schema_a.map.json` | 6,831 | `ca5587b579b549c44538178345996595134c4cef69b466409bbb88c692e8a0e6` |
| `examples/scimg_activation_demo/evidence/schema_b.map.json` | 6,831 | `5aed2d365738e5b7c7206638ffe342cee9e79b8fccb8c636d4af8898788d6d85` |
| `examples/scimg_activation_demo/evidence/schema_a_to_b.diff.json` | 3,854 | `dc7060282989af044e2f6e0f5879071bb1d534b7c11c8147110581823cf212b0` |
| `examples/scimg_activation_demo/evidence/schema_b_to_a.diff.json` | 3,854 | `d8537e7c877dd134b60e871b9e5eb4cd4dd9140643941785ca3fd94bb79d0c5e` |
| `examples/scimg_activation_demo/evidence/schema_a_to_b.mapless.diff.json` | 2,337 | `7bfa4d53e02cae3e488df83d479e4aef93689a62c54301580729ce910533fe54` |
| `examples/scimg_activation_demo/evidence/schema_a_identical.diff.json` | 2,372 | `46e328c9d40665a6d7f0335b9dd7df6c39ab03644aba03f327a09abd2a8a2173` |

### Real CLI exit/stdout evidence

Release DLL: `src/Signal.CANdy.CLI/bin/Release/net8.0/Signal.CANdy.CLI.dll`.

Clean project builds both exited 0 with empty stderr. Exact stdout was:

```text
Built scimg-activation-a: build/schema_a.scimg (444 bytes)
Wrote header: build/scimg_activation_a.h
Wrote inspect: build/schema_a.inspect.json
Wrote map: build/schema_a.map.json
Wrote activation: build/schema_a.activation.json
```

```text
Built scimg-activation-b: build/schema_b.scimg (444 bytes)
Wrote header: build/scimg_activation_b.h
Wrote inspect: build/schema_b.inspect.json
Wrote map: build/schema_b.map.json
Wrote activation: build/schema_b.activation.json
```

`image inspect` A/B stdout runs exited 0, stderr empty, and emitted exactly 6,800 bytes. Each stdout was byte-identical to its checked-in inspect artifact above. The corresponding `--out` runs exited 0 with exact one-line stdout:

```text
Wrote inspect: examples/scimg_activation_demo/evidence/schema_a.inspect.json
Wrote inspect: examples/scimg_activation_demo/evidence/schema_b.inspect.json
```

Mapped+activation A -> B and B -> A stdout runs exited 0, stderr empty, and emitted exactly 3,854 bytes each. Mapless emitted 2,337 bytes; identical emitted 2,372 bytes. Every stdout was byte-identical to its checked-in diff artifact. Exact `--out` acknowledgements were:

```text
Wrote diff: examples/scimg_activation_demo/evidence/schema_a_to_b.diff.json
Wrote diff: examples/scimg_activation_demo/evidence/schema_b_to_a.diff.json
Wrote diff: examples/scimg_activation_demo/evidence/schema_a_to_b.mapless.diff.json
Wrote diff: examples/scimg_activation_demo/evidence/schema_a_identical.diff.json
```

Failure/atomicity matrix:

| Case | Exit | stdout | stderr token/result | Publication |
|---|---:|---:|---|---|
| missing inspect input | 4 | 0 bytes | `error[SC2404] Input does not exist` | no output |
| malformed 2-byte SCIMG | 3 | 0 bytes | `error[SC2403] [DocumentError "ImageSize"]` | no output |
| existing inspect output | 4 | 0 bytes | `error[SC2404] [ArtifactError "An artifact destination already exists."]` | sentinel unchanged |
| existing diff output | 4 | 0 bytes | same `SC2404` artifact error | sentinel unchanged |
| malformed map (`{`) | 3 | 0 bytes | `error[SC2403] [DocumentError ... open JSON object ...]` | no output |

No `.signal-candy-*.tmp` staging file remained. Contract is exit 0 success, 2 grammar, 3 invalid document, 4 I/O/existing destination; failures do not write stdout or partial evidence.

### Independent byte/map/range audit

A/B image proof:

| Image | Bytes | SHA-256 | CRC32 |
|---|---:|---|---|
| `build/schema_a.scimg` | 444 | `9197bf85693f823f3623f9562a2a892468dc461a1c7cdaf4f60a6dc91cad6d1e` | `0x26474F02` |
| `build/schema_b.scimg` | 444 | `6b1a5bdf3255bff17e12195bea2fd4703ae6427e06f2e701d7fde231e05312f2` | `0x7DB9E52E` |

An independent Python audit used only raw `struct`, `hashlib`, `zlib`, and JSON parsing. It checked `SCIMG01\0`, total length at byte 12, SHA-256, CRC32 over `[0,440)`, footer CRC, inspect image hash, map image hash, every table boundary, every record range, source metadata, direct little-endian semantic values, and directional diff invariants.

Every A/B inspect `regions` and map `tables` range was identical and in-bounds:

| Region | Exact half-open range |
|---|---|
| header | `[0,32)` |
| directory | `[32,64)` |
| rxMessages | `[64,72)` |
| rxPrograms | `[72,88)` |
| conversions | `[88,112)` |
| symbols | `[112,164)` |
| extensionHeader | `[164,204)` |
| nestedMuxRecords | `[204,204)` |
| qualityEntries | `[204,216)` |
| protectionHeader | `[216,264)` |
| rxProtectionPlans | `[264,280)` |
| txProtectionPlans | `[280,296)` |
| rxCounters | `[296,312)` |
| coverageSpans | `[312,320)` |
| txHeader | `[320,352)` |
| txMessages | `[352,376)` |
| txPrograms | `[376,408)` |
| txCounters | `[408,432)` |
| txTemplates | `[432,440)` |
| footer | `[440,444)` |

Exact record ranges were also audited against both bytes and maps: pool symbols `[116,125)`, `[125,134)`, `[134,144)`; conversion `[88,112)`; RX message/program `[64,72)`/`[72,88)`; quality entries `[204,208)`, `[208,212)`, `[212,216)`; RX/TX protection `[264,280)`/`[280,296)`; RX counter `[296,312)`; coverage `[312,316)`, `[316,320)`; TX message `[352,376)`; TX programs `[376,392)`, `[392,408)`; TX counter `[408,432)`; TX template `[432,440)`. The zero-count nested mux table is exactly `[204,204)`.

A/B differ at exactly raw byte offsets `64,157,356,424,440,441,442,443`: RX ID, RX symbol A/B byte, TX ID, TX counter initial value, and four CRC footer bytes. Direct byte decode and maps agree:

- A RX: source `schema-a`, path `schema_a.dbc`, name `ACTIVATION_A_RX`, CAN ID `806` (`0x326`).
- B RX: source `schema-b`, path `schema_b.dbc`, name `ACTIVATION_B_RX`, CAN ID `822` (`0x336`).
- A TX: source `schema-a`, path `schema_a.dbc`, name `ACTIVATION_A_TX`, CAN ID `805` (`0x325`).
- B TX: source `schema-b`, path `schema_b.dbc`, name `ACTIVATION_B_TX`, CAN ID `821` (`0x335`).
- Logical TX key/ID stays `tx:33`/33; initial counter is exactly `0 -> 9`.

A -> B has exactly three changed entities and nine fields: RX `source`, `sourcePath`, `name`, `canId`; TX the same four fields; TX counter `initialValue`. B -> A preserves entity/key/field order and swaps every before/after value exactly. All 18 mapped resources have before == after and delta 0. Both directions are `compatible-reset-required` / `schema-content-changed`.

### Issue #23 HIL exact mapping and no-reflash proof

The 29 observed PASS lines in `.omo/evidence/issue23-hil/hil.txt` map as follows. “Runtime invariant” refers to the byte-identical C runtime from `581e16c`; “host A/B” refers to the independently audited inspect/map/diff evidence above.

| # | Issue #23 observed target behavior | Host prediction/evidence |
|---:|---|---|
| 1 | initial heartbeat | Runtime/firmware unchanged; host-only documents cannot interrupt heartbeat. |
| 2 | A EMIT_TX response | Unchanged activation control path; A remains active initially. |
| 3 | A TX counter 0 | A TX `0x325`, initial 0, unchanged TX programs/template/protection. |
| 4 | A RX counter 0 accepted | A RX `0x326`, unchanged RX program/protection. |
| 5 | initial A snapshot response | Unchanged snapshot protocol and active A. |
| 6 | A snapshot raw `0x5678`, valid flags | Same `pool:1` RX identity and quality layout. |
| 7 | prepare B keeps A active | Compatible same pool ABI/resources permits prepare; transactional runtime retains active A while pending. |
| 8 | A RX counter 1 while B pending | A is still active and A RX program is unchanged. |
| 9 | pending-B snapshot | Runtime invariant: pending B does not publish early. |
| 10 | B RX does not match pending A | Exact RX IDs differ `0x326 -> 0x336`; active ID is still A. |
| 11 | abort leaves A active | Runtime invariant: abort is publication-free. |
| 12 | A EMIT_TX after abort response | Active map remains A. |
| 13 | A TX counter 1 after abort | Abort does not reset active A; A counter continues 0 -> 1. |
| 14 | second prepare B | Same compatible classification/resources remains valid. |
| 15 | commit B generation 2 | `compatible-reset-required` predicts legal publication with reset, not incompatibility rejection. |
| 16 | post-commit snapshot response | Runtime invariant: B is now active, no pending image. |
| 17 | raw preserved, RX flags cleared | Same pool ABI/key/storage permits pool value continuity; reset-required clears B runtime/quality state. |
| 18 | old-A rejection snapshot response | Runtime invariant after atomic B publication. |
| 19 | old A RX no longer matches | Active RX ID changed exactly `0x326 -> 0x336`. |
| 20 | B RX counter 9 accepted | B RX ID/name/source are selected; reset RX counter state accepts the new stream. |
| 21 | B EMIT_TX response | Active TX map is B. |
| 22 | B TX counter 9 | B TX `0x335`, exact initial value 9, unchanged template/protection. |
| 23 | malformed C rejected, B active | Runtime/image hash and CRC validation are unchanged from issue #23. |
| 24 | post-C snapshot response | Transactional failure leaves active B and generation unchanged. |
| 25 | malformed C leaves B pool untouched | Runtime invariant: failed prepare is atomic. |
| 26 | B RX counter 10 after C | B counter state continues after rejected C. |
| 27 | second B EMIT_TX response | B remains active after C rejection. |
| 28 | B TX counter 10 | Initial 9 and unchanged increment/modulus predict next counter 10. |
| 29 | heartbeat continuity, four RX matches, one flash | No target artifact changed and no host command touches target hardware; continuity and no additional flash are the expected result. |

No-reflash identity checks:

- `git diff --exit-code 581e16c..HEAD -- runtime/c99/include/signal_candy_runtime.h runtime/c99/src/signal_candy_runtime.c` -> exit 0.
- Runtime public header is 9,405 bytes, SHA-256 `a2d85334595134a21fb38f014a3a7c8621d51e19a9e9c333353c1a77e8c144bb` at both `581e16c` and current HEAD.
- Runtime source is 103,525 bytes, SHA-256 `09caecd4228004006c9493e914b56d2f44a768e6c689b569cc180b1a4f73d0ea` at both revisions.
- `git diff --exit-code 00491d3..HEAD --` for the two SCIMG fixtures and C runtime files -> exit 0.
- Current A/B image hashes equal the `00491d3` Git blobs exactly: `9197...6d1e` and `6b1a...12f2`.

Therefore the generated host evidence does not change firmware behavior, runtime ABI, C bytes, embedded image bytes, or HIL expectations. Reflash is neither required nor justified.

## ✅ 테스트 결과

- Real CLI clean A/B build, inspect stdout/out, mapped A -> B/B -> A stdout/out, mapless, identical, and failure/atomicity matrix: PASS; all intended exits and byte counts above.
- Independent raw-byte/map/range/diff audit: `INDEPENDENT AUDIT PASS`.
- Focused issue #24 tests: `30 passed, 0 failed, 0 skipped`.
- Solution build: exit 0, 0 errors; 28 existing Scriban `NU1902/NU1903/NU1904` advisory warnings.
- Differential F#/C test: `1 passed, 0 failed`.
- Fantomas `--check src/ tests/`: exit 0.
- C99 runtime suites: RX 25/25, TX 15/15, quality 18/18, protection 17/17, activation 45/45; total 120/120 PASS.
- Native A/B/C fixture: 11/11 named checks + `ALL PASS`.
- Full solution rerun on this Windows checkout: Generator 27/27 PASS; Core 395 PASS, 8 FAIL, 2 SKIP. The eight failures are existing CRLF-sensitive multiline-literal tests in `RuntimeCapabilitiesTests` and `ProjectManifestTests`, outside issue #24. The same focused two-module run at baseline `00491d3` reproduces exactly 8 failures (55 PASS, 2 SKIP), proving they were not introduced here. The issue #24 focused30 and differential sets are clean.
- `git diff --check`: PASS (Git only reports the checkout's configured future LF-to-CRLF warning).
- YAML/Markdown LSP is unavailable on this workstation; both manifests were parsed and built successfully by the real CLI. No source file was changed in this task.
- Generated native executable, C objects, test temp roots, malformed/sentinel files, and staging files were removed.

## ⏭ 다음 계획

This immutable report and the checked-in canonical artifacts are the final host evidence for issue #24. No target operation, reflash, sibling change, GitHub action, or commit remains in this task.
