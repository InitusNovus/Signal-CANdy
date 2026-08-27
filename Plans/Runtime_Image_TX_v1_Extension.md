# Runtime Image v1 TX Extension

## Status and compatibility

This document defines the optional transmit extension for `.scimg` format version 1. The image magic remains `SCIMG01\0` and the format version remains `1`.

RX-only images have feature flags zero and retain their existing byte representation exactly. A TX-capable runtime opens those images without migration. TX-enabled images set feature bit 0; legacy runtimes reject their non-zero reserved header fields rather than interpreting them as RX-only images.

## v1 header extension

All integers are little-endian.

| Offset | Size | RX-only | TX-enabled |
|---:|---:|---|---|
| 0 | 8 | `SCIMG01\0` | unchanged |
| 8 | 2 | version `1` | unchanged |
| 10 | 2 | zero | bit 0 `SCIMG_FEATURE_TX` |
| 12 | 4 | total image size | unchanged |
| 16 | 2 | RX message count | unchanged |
| 18 | 2 | RX program count | unchanged |
| 20 | 2 | conversion count | RX and TX conversions |
| 22 | 2 | zero | pool slot count |
| 24 | 4 | zero | TX section offset |
| 28 | 4 | zero | TX section size |
| 32 | 32 | four-entry MSG/PRG/CNV/SYM directory | unchanged |

Unknown feature bits are rejected. The TX section offset is four-byte aligned, follows the fixed legacy directory sections and zero padding, and ends before the CRC32 footer. The existing CRC32 continues to cover every byte before the four-byte footer.

## TX section

Offsets in the TX section are relative to its start. Tables are ordered exactly as header, TXM, TXP, CTR, templates, then zero alignment padding.

### Header (32 bytes)

| Offset | Type | Meaning |
|---:|---|---|
| 0 | u32 | `TX01` (`0x31305854`) |
| 4 | u16 | TX message count |
| 6 | u16 | TX program count |
| 8 | u16 | counter count |
| 10 | u16 | zero |
| 12 | u32 | TXM offset |
| 16 | u32 | TXP offset |
| 20 | u32 | CTR offset |
| 24 | u32 | template offset |
| 28 | u32 | template byte count |

### TX message entry (TXM, 24 bytes)

| Offset | Type | Meaning |
|---:|---|---|
| 0 | u32 | explicit logical message ID |
| 4 | u32 | encoded CAN ID; bit 31 denotes extended |
| 8 | u8 | payload length |
| 9 | u8 | frame flags: bit 0 extended, bit 1 CAN FD |
| 10 | u16 | program count |
| 12 | u16 | first program index |
| 14 | u16 | counter index, `0xFFFF` when absent |
| 16 | u32 | template offset relative to TX start |
| 20 | u32 | zero |

TXM entries are strictly sorted by logical ID. Program and template ranges are dense and ordered. Classic payload lengths are 0 through 8. FD lengths are 12, 16, 20, 24, 32, 48, and 64. Frame flags must agree with the encoded ID and payload length.

### TX program entry (TXP, 16 bytes)

TXP reuses the v1 PRG representation:

| Offset | Type | Meaning |
|---:|---|---|
| 0 | u16 | normalized start bit |
| 2 | u16 | bit length |
| 4 | u8 | bit 0 big-endian, bit 1 signed |
| 5 | u8 | pool storage discriminator |
| 6 | u16 | conversion index |
| 8 | u16 | pool slot index |
| 10 | u16 | mux selector slot, `0xFFFF` if unconditional |
| 12 | u32 | mux expected value, `0xFFFFFFFF` if unconditional |

The mux selector is first. Inactive branches are not read or validated. Overlap is legal only between branches of the same selector with different expected values.

### Counter entry (CTR, 24 bytes)

| Offset | Type | Meaning |
|---:|---|---|
| 0 | u16 | start bit |
| 2 | u16 | length, 1 through 32 |
| 4 | u8 | bit 0 big-endian |
| 5 | 3 | zero |
| 8 | u32 | modulus; zero denotes `2^32` |
| 12 | u32 | non-zero increment |
| 16 | u32 | initial value |
| 20 | u32 | zero |

A counter is referenced by exactly one TX message and cannot overlap a TX program. Zero modulus is valid only for a 32-bit field. Increment and initial value must fit the effective modulus and wire width.

## Deterministic lowering

RX messages and conversions preserve the legacy traversal and ordering. TX messages are then sorted by logical ID. Within each TX message the selector is first, followed by start bit, mux expectation, and pool slot. TX conversions are interned only after all RX conversions. Counter entries and zero templates follow TX logical-ID order. Reserved bytes and padding are zero.

## Runtime encode contract

`sc_encode_prepare` looks up the explicit logical ID, copies the message template to caller-owned scratch, encodes active pool programs, inserts the current counter value, and only then publishes the frame and reserves the counter. Any failure leaves the caller frame, token, and persistent state unchanged; scratch contents are unspecified. Unknown logical IDs return `SC_OK_NO_MATCH`.

Numeric encoding reads the declared pool representation, rejects non-finite values and invalid slots, applies the inverse affine conversion, rounds floating paths to nearest with ties away from zero, validates signed or unsigned raw limits before casting, and inserts LE or BE bits. Bytes beyond frame length are zero.

A successful prepare for a counter message creates one outstanding caller-owned token. A second prepare for that counter returns `SC_ERR_BUSY`. `sc_encode_commit(token, true)` advances modulo the configured profile; `false` cancels without advancing. Both clear the reservation and token. Foreign, stale, copied, or reused tokens return `SC_ERR_TOKEN` without changing state. Counterless messages require no runtime state and commit as a no-op.

The runtime allocates no memory. Schema, pool, state, scratch, frame, and token storage are caller-owned. Calls involving one runtime state are single-writer.
