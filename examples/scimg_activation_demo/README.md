# Transactional activation A/B/C runtime-image fixture

This issue #23 fixture builds two compatible compiler-generated schemas for the same three-slot pool ABI used by `scimg_protection_demo`:

| Slot | Pool signal | Direction | Default/freshness |
|---:|---|---|---|
| 0 | `RxValue` (`u16`) | RX | freshness 30 ms |
| 1 | `TxValue` (`u16`) | TX | caller default `0x1234` |
| 2 | `MarkerA5` (`u8`) | TX | caller default `0xA5` |

The frozen pool ABI is `sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947`. Both schemas require a 444-byte image, 60-byte ILP32 runtime state, 8-byte TX scratch, and three pool slots. Their image flags are `0x0007` (`TX|RXQ|PROTECTION`) and semantic features are RX, TX, RX quality, CRC-8/SAE-J1850, CRC-16/CCITT-FALSE, RX counter, and TX counter.

## Build with the actual project CLI

Paths are manifest-relative. Validation writes nothing; each build atomically emits its image, generated activation-aware C header, compatibility inspection JSON, canonical `sc.map/v1` source map, and activation descriptor under `build/`. These deterministic fixture outputs are checked in as evidence.

```sh
dotnet run --project src/Signal.CANdy.CLI -c Release -- \
  project validate examples/scimg_activation_demo/project_a.yaml
dotnet run --project src/Signal.CANdy.CLI -c Release -- \
  project validate examples/scimg_activation_demo/project_b.yaml
dotnet run --project src/Signal.CANdy.CLI -c Release -- \
  project build examples/scimg_activation_demo/project_a.yaml
dotnet run --project src/Signal.CANdy.CLI -c Release -- \
  project build examples/scimg_activation_demo/project_b.yaml
bash examples/scimg_activation_demo/run_native_test.sh
```

The native driver executes A -> prepare B -> abort -> A -> prepare B -> commit B -> malformed C -> B against the real C99 runtime. Its temporary executable is removed on every exit.

## Stable inspect, map, and diff evidence

Build the Release CLI once, remove the generated destinations for a clean regeneration, and run both manifests. Artifact publication is create-only, so an existing destination is rejected rather than overwritten.

```sh
CLI=src/Signal.CANdy.CLI/bin/Release/net8.0/Signal.CANdy.CLI.dll
dotnet build src/Signal.CANdy.CLI/Signal.CANdy.CLI.fsproj -c Release --nologo
rm -rf examples/scimg_activation_demo/build
dotnet "$CLI" project build examples/scimg_activation_demo/project_a.yaml
dotnet "$CLI" project build examples/scimg_activation_demo/project_b.yaml
```

`project build` is the map-generation command. Both manifests now declare `outputs.map`; the resulting `build/schema_{a,b}.map.json` documents are canonical `sc.map/v1`, bind to the exact image SHA-256 and pool ABI, retain manifest-relative DBC provenance, and use absolute half-open image ranges `[start,end)`.

The stable detailed inspections are canonical `sc.inspect/v1` documents. Without `--out`, the complete JSON is written to stdout. With `--out`, publication is atomic and stdout contains only `Wrote inspect: <path>`.

```sh
dotnet "$CLI" image inspect examples/scimg_activation_demo/build/schema_a.scimg
dotnet "$CLI" image inspect examples/scimg_activation_demo/build/schema_b.scimg
dotnet "$CLI" image inspect examples/scimg_activation_demo/build/schema_a.scimg \
  --out examples/scimg_activation_demo/evidence/schema_a.inspect.json
dotnet "$CLI" image inspect examples/scimg_activation_demo/build/schema_b.scimg \
  --out examples/scimg_activation_demo/evidence/schema_b.inspect.json
```

The fully attributed directional diffs require the matching maps and activation descriptors. They are canonical `sc.diff/v1`; A -> B and B -> A are exact inverses with class `compatible-reset-required`, reason `schema-content-changed`, 18 zero resource deltas, and only `rx-message`, `tx-message`, and `tx-counter` changed entities.

```sh
dotnet "$CLI" image diff \
  examples/scimg_activation_demo/build/schema_a.scimg \
  examples/scimg_activation_demo/build/schema_b.scimg \
  --before-map examples/scimg_activation_demo/build/schema_a.map.json \
  --after-map examples/scimg_activation_demo/build/schema_b.map.json \
  --before-activation examples/scimg_activation_demo/build/schema_a.activation.json \
  --after-activation examples/scimg_activation_demo/build/schema_b.activation.json

dotnet "$CLI" image diff \
  examples/scimg_activation_demo/build/schema_b.scimg \
  examples/scimg_activation_demo/build/schema_a.scimg \
  --before-map examples/scimg_activation_demo/build/schema_b.map.json \
  --after-map examples/scimg_activation_demo/build/schema_a.map.json \
  --before-activation examples/scimg_activation_demo/build/schema_b.activation.json \
  --after-activation examples/scimg_activation_demo/build/schema_a.activation.json
```

Omitting maps intentionally produces `unknown-without-map` / `source-map-missing`. Comparing A with itself using the A map and activation produces `identical`, no reasons, and no changes. Add `--out <new.diff.json>` to either form for atomic file publication. The checked-in `evidence/` directory contains the exact `--out` results plus byte-identical copies of the project-generated maps. Remove an evidence destination before deliberately regenerating it; the CLI never overwrites evidence.

CLI exit contracts are stable: `0` success, `2` grammar/usage, `3` malformed or semantically invalid document, and `4` missing input, existing destination, or other I/O failure. Failures write no stdout, preserve an existing destination byte-for-byte, and leave neither the requested output nor a staging file.

## Exact generated provenance

The compiler, activation descriptor renderer, and C99 activation runtime are from Signal-CANdy commit `581e16ce9b5c940fa35102935f901353699cc9ad` (`581e16c`). The fixture inputs in this directory are the issue #23 preparation inputs. Generation is timestamp-free and deterministic.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `build/schema_a.scimg` | 444 | `9197bf85693f823f3623f9562a2a892468dc461a1c7cdaf4f60a6dc91cad6d1e` |
| `build/schema_a.activation.json` | 557 | `fc7a44d1b989026173831cbc6c936f92031b17977fb272e6ec3f2869c9fd55f7` |
| `build/schema_a.inspect.json` | 458 | `10914a41cdefd4808095258791516f1bbde34e17e97b4b65247f70d5f93f8469` |
| `build/schema_a.map.json` | 6,831 | `ca5587b579b549c44538178345996595134c4cef69b466409bbb88c692e8a0e6` |
| `build/scimg_activation_a.h` | 5,011 | `8608191506901030fb66e9669e103df76e5500a4e68d836ea4dbee4083d7c57d` |
| `build/schema_b.scimg` | 444 | `6b1a5bdf3255bff17e12195bea2fd4703ae6427e06f2e701d7fde231e05312f2` |
| `build/schema_b.activation.json` | 557 | `fe65f0d46a6dea1e0847e1cb428c9ab9d4c2f9adc48442f9e8cff8c459f43d4f` |
| `build/schema_b.inspect.json` | 458 | `c7d44ef4d7311dcc6c4113e143d5d388f6bf66df43618d49b1cbc1728bad7803` |
| `build/schema_b.map.json` | 6,831 | `5aed2d365738e5b7c7206638ffe342cee9e79b8fccb8c636d4af8898788d6d85` |
| `build/scimg_activation_b.h` | 5,011 | `2d748d4ff200afee0038895730185164dfc797ea5f6a238cb755fc851c34d265` |

Issue #24 canonical evidence:

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `evidence/schema_a.inspect.json` | 6,800 | `f58105c9a574b4cd6a5b062e800647be0c5d0789bf98cb9f60d76b8aa864aa7c` |
| `evidence/schema_b.inspect.json` | 6,800 | `cb7613c17fde3348d0850f6fadbf30357cdfccc344442a79cfb0739809454acd` |
| `evidence/schema_a.map.json` | 6,831 | `ca5587b579b549c44538178345996595134c4cef69b466409bbb88c692e8a0e6` |
| `evidence/schema_b.map.json` | 6,831 | `5aed2d365738e5b7c7206638ffe342cee9e79b8fccb8c636d4af8898788d6d85` |
| `evidence/schema_a_to_b.diff.json` | 3,854 | `dc7060282989af044e2f6e0f5879071bb1d534b7c11c8147110581823cf212b0` |
| `evidence/schema_b_to_a.diff.json` | 3,854 | `d8537e7c877dd134b60e871b9e5eb4cd4dd9140643941785ca3fd94bb79d0c5e` |
| `evidence/schema_a_to_b.mapless.diff.json` | 2,337 | `7bfa4d53e02cae3e488df83d479e4aef93689a62c54301580729ce910533fe54` |
| `evidence/schema_a_identical.diff.json` | 2,372 | `46e328c9d40665a6d7f0335b9dd7df6c39ab03644aba03f327a09abd2a8a2173` |

Schema A uses standard RX `0x326`, standard TX `0x325`, logical TX ID 33, TX counter initial 0, and image CRC32 `0x26474F02`. Schema B uses standard RX `0x336`, standard TX `0x335`, logical TX ID 33, TX counter initial 9, and image CRC32 `0x7DB9E52E`. Exact vectors are:

```text
A TX counter 0: 00 34 12 A5 00 00 00 A5
A TX counter 1: 01 34 12 A5 00 00 00 F8
A RX counter 0: 00 78 56 BC 00 00 87 C8
A RX counter 1: 01 79 56 BC 00 00 76 27
A RX counter 2: 02 7A 56 BC 00 00 44 07
B TX counter 9: 09 34 12 A5 00 00 00 2A
B TX counter10: 0A 34 12 A5 00 00 00 CD
B RX counter 9: 09 68 24 BC 00 00 22 2B
B RX counter10: 0A 69 24 BC 00 00 93 4F
```

Schema C is not a third host-generated artifact. Firmware copies embedded B into caller-owned RAM and flips image byte 64 without updating either the B descriptor SHA-256 or SCIMG CRC32, so prepare must return `SC_ERR_CRC` before changing active B, staging storage, token, or pool.

## Exact one-flash HIL sequence

The prepared CC1A activation firmware embeds both generated headers plus malformed C. A future bench run must use TT_Host UART/IAP to flash that binary exactly once at application origin `0x08008000`; ST-Link/SWD is forbidden. No later schema transition flashes or resets the target.

1. Start NI-XNET CAN3 receive before any command and observe classic heartbeat `0x7F1`.
2. Send `EMIT_TX`; observe A `0x325` counter 0.
3. Send A RX counter 0 and require the exact `0x7A1` accepted diagnostic; snapshot confirms raw `0x5678`.
4. `PREPARE B`; response reports active A, pending B, generation 1.
5. Send A RX counter 1 and require acceptance; send B RX counter 9 and snapshot unchanged A raw `0x5679`, proving only A is active while B is pending.
6. `ABORT`; response reports active A, no pending schema, generation 1; `EMIT_TX` produces A counter 1.
7. `PREPARE B`, then `COMMIT`; response reports active B, no pending schema, generation 2.
8. Snapshot immediately: raw remains `0x5679`, while RX-target flags are zero.
9. Send valid A RX counter 2 and snapshot again; unchanged B state proves A no longer matches.
10. Send B RX counter 9 and require acceptance; `EMIT_TX` must produce B TX counter 9, proving RX and TX state reset.
11. `PREPARE C`; response is `SC_ERR_CRC`, active B, no pending schema, generation 2.
12. Snapshot proves B state is unchanged; B RX counter 10 and TX counter 10 still pass.
13. Observe heartbeat continuity with four accepted RX frames and record that the single initial TT_Host operation was the only flash.

Control is standard classic ID `0x7A2`, exactly eight bytes: `[version=1, opcode, candidate, request-sequence, 0, 0, 0, 0]`. Opcodes are PREPARE=1, ABORT=2, COMMIT=3, SNAPSHOT=4, EMIT_TX=5; candidates are none=0, A=1, B=2, C=3. Response `0x7A3` is `[1, opcode, candidate, signed-sc_status, active, pending, generation-lo, generation-hi]`. Snapshot `0x7A4` is `[1, active, slot0-flags-low-nibble, 0, slot0-raw-u32-LE]`.
