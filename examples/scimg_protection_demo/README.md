# CRC/counter protection runtime-image fixture

This issue #21 fixture combines one protected RX message and one protected TX message:

- TX standard `0x325`, logical ID `33`, counter bits `0..3` (modulus 16, increment 1, initial 0), CRC-8/SAE-J1850 over bytes `0..7` excluding byte 7.
- RX standard `0x326`, counter bits `0..3` (modulus 16, increment 1), CRC-16/CCITT-FALSE over bytes `0..7` excluding bytes 6 and 7; the CRC field is little-endian.
- Pool ABI order is `RxValue`, `TxValue`, `MarkerA5`. The HIL caller initializes the TX slots to `0x1234` and `0xA5`.

Generate and inspect with the CLI:

```sh
dotnet run --project src/Signal.CANdy.CLI -c Release -- scimg \
  -d examples/scimg_protection_demo/protection_demo.dbc \
  -p examples/scimg_protection_demo/pool.json \
  -b examples/scimg_protection_demo/binding.json \
  -o protection_demo.scimg --inspect protection_demo.inspect.json
```

The deterministic image is 428 bytes with SHA-256 `26e6f8529af6c840d294a87cb967a490b9cd78394b2c9911fee32681660fe7df`, image CRC32 `0x5B65B079`, and feature flags `0x0005` (`TX|PROTECTION`). Inspection reports one RX message/program, three pool slots, one conversion, one TX message, two TX programs, one TX counter, and no RXQ entries. The EX01 section begins at offset 160; PR01 begins at 200 and is 104 bytes; TX01 begins at 304 and is 120 bytes; the image CRC32 is at offset 424.

The sibling runtime-test sync tool renders a timestamp-free 3,138-byte C header with SHA-256 `3b82c26c39f8bbd47ae56a1cb797ecbf5fcd0ea1ad37b867caef9463cc251e35`. It embeds the image exactly once as `gScimgProtectionDemoBytes` and defines `GSCIMG_PROTECTION_DEMO_BYTE_COUNT` as 428.

Fixed vectors:

```text
TX counter 0:       00 34 12 A5 00 00 00 A5
TX counter 1:       01 34 12 A5 00 00 00 F8
RX valid:           00 78 56 BC 00 00 87 C8
RX bad CRC:         00 78 56 BC 00 00 87 C9
RX counter jump 2:  02 78 56 BC 00 00 C7 43
```
