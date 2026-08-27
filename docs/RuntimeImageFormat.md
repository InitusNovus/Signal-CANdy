# Signal-CANdy runtime image v1 protection contract

This document is normative for the protection extension implemented by `src/Signal.CANdy.Core/Scimg.fs` and `runtime/c99/`. The base v1 tables are specified in [`Plans/Runtime_Image_v1_Format.md`](../Plans/Runtime_Image_v1_Format.md); TX01 and RXQ remain specified by their extension documents under `Plans/`.

All integer fields are little-endian. Reserved bytes and alignment padding are zero. Images contain no timestamp, source path, or build metadata. The final image CRC is CRC-32/ISO-HDLC over every byte before the four-byte footer.

## Feature and EX01 contract

Main-header feature bit `0x0004` means protection is present. It may be combined with TX (`0x0001`) and RXQ (`0x0002`). Protection requires EX01. EX01 flag bit `0x0008` identifies PR01 and its `protection_offset` and `protection_size`; the PR01 section is placed after nested-mux and quality records and before TX01. EX01 `quality_count` is zero when RXQ is absent, including protection-only images.

## PR01 layout

PR01 starts with this dense 48-byte header:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | magic `PR01` (`0x31305250`) |
| 4 | 2 | RX plan count; exactly the RX message count |
| 6 | 2 | TX plan count; exactly the TX message count |
| 8 | 2 | RX counter record count |
| 10 | 2 | coverage-span count |
| 12 | 4 | RX plan offset; exactly 48 |
| 16 | 4 | TX plan offset |
| 20 | 4 | RX counter offset |
| 24 | 4 | coverage-span offset |
| 28 | 4 | PR01 end offset; exactly the section size |
| 32 | 16 | reserved zero |

Tables are contiguous in header order with no gaps. Every RX and TX message has one 16-byte plan, including an empty plan when that message has no protection operation.

### Protection plan (16 bytes)

| Offset | Size | Field |
|---:|---:|---|
| 0 | 1 | flags: bit 0 CRC, bit 1 counter; no other bits |
| 1 | 1 | CRC algorithm: 0 absent, 1 CRC-8/SAE-J1850, 2 CRC-16/CCITT-FALSE |
| 2 | 1 | CRC width in bytes: 0, 1, or 2 matching the algorithm |
| 3 | 1 | CRC field byte order: 0 little, 1 big |
| 4 | 2 | CRC field start bit, byte-aligned; `0xFFFF` when absent |
| 6 | 2 | first coverage-span index; `0xFFFF` when absent |
| 8 | 1 | coverage-span count, 1 or 2 when CRC is present |
| 9 | 1 | data-ID byte count: 0 or 2 |
| 10 | 2 | counter index; `0xFFFF` when absent |
| 12 | 2 | optional data ID; zero when absent |
| 14 | 2 | reserved zero |

RX counter indices refer to PR01 RX-counter records and are dense in RX-plan order. TX counter indices must equal the corresponding TX01 message counter index. Coverage spans are dense in RX-plan then TX-plan order.

### RX counter record (16 bytes)

| Offset | Size | Field |
|---:|---:|---|
| 0 | 2 | start bit |
| 2 | 2 | width, 1..32 |
| 4 | 1 | byte order: 0 little, 1 big |
| 5 | 3 | reserved zero |
| 8 | 4 | modulus; zero means 32-bit arithmetic and is valid only for width 32 |
| 12 | 4 | non-zero increment, less than a non-zero modulus |

A coverage-span record is four bytes: byte offset, non-zero byte count, and two reserved zero bytes. Spans are ordered, non-overlapping, within the 64-byte frame bound, and cannot overlap the CRC field.

## CRC profile

CRC-8/SAE-J1850 uses polynomial `0x1D`, initial value `0xFF`, no reflection, and final XOR `0xFF`. CRC-16/CCITT-FALSE uses polynomial `0x1021`, initial value `0xFFFF`, no reflection, and final XOR `0x0000`. An optional data ID is fed first as two bytes in big-endian order; coverage spans are then fed in order. The received or generated CRC field uses the plan's declared field byte order.

TX encoding writes ordinary programs, inserts the current counter, and calculates/inserts CRC last. RX decoding validates CRC first and counter continuity second, before updating pool slots or persistent RX state.

## Runtime state and atomicity

State is caller-owned, aligned, and sized only through `sc_schema_required_state_bytes`; callers must not infer or serialize its private suffix. `sc_runtime_state_init` binds the state to one opened schema, initializes TX counters from TX01, clears RX counter initialization, and creates no allocation. A schema with an RX counter requires `sc_decode_state` (or `sc_decode_at` when RXQ is also enabled); plain `sc_decode` returns `SC_ERR_STATE` for a matching protected RX message.

The first CRC-valid RX counter seeds the expected next value. Later values must equal `(accepted + increment) mod modulus` (or 32-bit wrap when modulus is zero). `sc_rx_counter_resync` clears initialization for an exact encoded CAN ID and flags tuple without changing pool data.

`SC_ERR_FRAME_CRC` and `SC_ERR_COUNTER` are rejection statuses. Either rejection leaves pool raw values, pool flags, RXQ timestamps, and all counter state unchanged. A bad CRC is reported before any counter decision and does not consume the expected counter.

TX prepare reserves but does not advance a stateful counter. `sc_encode_commit(token, 1)` advances only after the caller's transport reports successful transmission; `sc_encode_commit(token, 0)` cancels without advancing. Both successful commit forms clear the token. Invalid, copied, stale, foreign, or reused tokens return `SC_ERR_TOKEN` without state mutation.
