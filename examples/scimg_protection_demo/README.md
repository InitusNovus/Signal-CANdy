# CRC/counter protection runtime-image fixture

This issue #21 fixture combines one protected RX message and one protected TX message:

- TX standard `0x325`, logical ID `33`, counter bits `0..3` (modulus 16, increment 1, initial 0), CRC-8/SAE-J1850 over bytes `0..7` excluding byte 7.
- RX standard `0x326`, counter bits `0..3` (modulus 16, increment 1), CRC-16/CCITT-FALSE over bytes `0..7` excluding bytes 6 and 7; the CRC field is little-endian.
- Pool ABI order is `RxValue`, `TxValue`, `MarkerA5`. The HIL caller initializes the TX slots to `0x1234` and `0xA5`.

The canonical issue #22 project and CC1A-Test-1 capability are `project.yaml` and `cc1a-test-1.runtime.json`. Paths are resolved relative to the manifest, so validation and builds work from any current working directory. Validate without writing output, then build the declared image, C header, and inspection JSON under the untracked `build/` directory:

```sh
dotnet run --project src/Signal.CANdy.CLI -c Release -- \
  project validate examples/scimg_protection_demo/project.yaml
dotnet run --project src/Signal.CANdy.CLI -c Release -- \
  project build examples/scimg_protection_demo/project.yaml
```

`project-insufficient.yaml` differs only by selecting `cc1a-test-1.insufficient.runtime.json`, whose runtime-state limit is 27 rather than the required 28 bytes. Both `project validate` and `project build` reject it with exit code 3 and `error[SC2207]` before creating `build/` or a staging file.

The capability pins image/state/scratch to 428/28/8 bytes; RX messages/programs to 1/1; TX messages/programs to 1/2; pool slots/conversions to 3/1; nested mux/depth/quality to 0/0/0; protection plans, TX counters, RX counters, and coverage spans to 2/1/1/2; and TX template/payload bytes to 8/8. Its exact feature set is RX, TX, CRC-8/SAE-J1850, CRC-16/CCITT-FALSE, RX counter, and TX counter. The required pool ABI SHA-256 is `3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947`.

The deterministic image is 428 bytes with SHA-256 `26e6f8529af6c840d294a87cb967a490b9cd78394b2c9911fee32681660fe7df`, image CRC32 `0x5B65B079`, and feature flags `0x0005` (`TX|PROTECTION`). Inspection reports one RX message/program, three pool slots, one conversion, one TX message, two TX programs, one TX counter, and no RXQ entries. The EX01 section begins at offset 160; PR01 begins at 200 and is 104 bytes; TX01 begins at 304 and is 120 bytes; the image CRC32 is at offset 424.

The deterministic project-built header is 3,105 bytes with SHA-256 `f07304bebbf627d64955c77221e786470d0d5abe49b449a13b024af5d17dc3bb`. It embeds the image exactly once as `gScimgProtectionDemoBytes` and defines `GSCIMG_PROTECTION_DEMO_BYTE_COUNT` as 428. The older direct command remains available when only an image and inspection document are needed:

```sh
dotnet run --project src/Signal.CANdy.CLI -c Release -- scimg \
  -d examples/scimg_protection_demo/protection_demo.dbc \
  -p examples/scimg_protection_demo/pool.json \
  -b examples/scimg_protection_demo/binding.json \
  -o protection_demo.scimg --inspect protection_demo.inspect.json
```

Fixed vectors:

```text
TX counter 0:       00 34 12 A5 00 00 00 A5
TX counter 1:       01 34 12 A5 00 00 00 F8
RX valid:           00 78 56 BC 00 00 87 C8
RX bad CRC:         00 78 56 BC 00 00 87 C9
RX counter jump 2:  02 78 56 BC 00 00 C7 43
```
