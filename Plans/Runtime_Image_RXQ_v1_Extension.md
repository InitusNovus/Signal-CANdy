# Runtime Image v1 RXQ Extension

## Status and compatibility

This document normatively defines feature bit 1 of `.scimg` format version 1 for bounded nested receive multiplexing and per-slot freshness. All integers are little-endian.

Feature bits are:

- bit 0: `SCIMG_FEATURE_TX`, as defined by `Runtime_Image_TX_v1_Extension.md`;
- bit 1: `SCIMG_FEATURE_RXQ`, this extension;
- bits 2 through 15: reserved and rejected.

An image with no mux path deeper than one predicate and no freshness threshold omits RXQ. Consequently flags-zero RX images and flags-one TX images retain their exact existing bytes. When RXQ and TX are both present, `TX01` is embedded unchanged in `EX01`; its internal offsets remain relative to the start of `TX01`.

## Main header

When RXQ is set, header offset 22 is the pool slot count, offsets 24 and 28 are the absolute `EX01` offset and size, and the four legacy directory sections end densely at the extension offset. The CRC32 footer immediately follows the extension. The magic, version, MSG, PRG, CNV, and SYM formats are unchanged.

## EX01 container

The extension header is exactly 40 bytes. Contained offsets are relative to the start of `EX01`.

| Offset | Size | Meaning |
|---:|---:|---|
| 0 | 4 | `EX01`, value `0x31305845` |
| 4 | 2 | extension flags |
| 6 | 1 | maximum mux depth, exactly 4 |
| 7 | 1 | zero |
| 8 | 2 | NMX record count |
| 10 | 2 | quality count, exactly pool slot count |
| 12 | 4 | NMX offset, exactly 40 |
| 16 | 4 | quality offset, `40 + 36 * N` |
| 20 | 4 | embedded TX offset, `quality_offset + 4 * Q` |
| 24 | 4 | embedded TX size |
| 28 | 12 | zero |

Extension flags are bit 0 NMX present, bit 1 quality present, and bit 2 embedded TX present. Bit 1 is mandatory. Flags must exactly describe the contained tables. Offsets are dense; no dynamic storage, optional padding, or unreferenced bytes exist.

## Nested mux table (NMX)

One sparse 36-byte record exists for each RX program with path depth 2 through 4. Records are strictly sorted by target global RX program index.

| Offset | Size | Meaning |
|---:|---:|---|
| 0 | 2 | target global RX program index |
| 2 | 1 | predicate count, 2..4 |
| 3 | 1 | zero |
| 4 | 32 | four 8-byte predicate entries |

Each predicate contains selector global RX program index (u16), selector pool slot (u16), and exact expected raw value (u32). Predicates are outermost first. Unused entries are `0xFFFF`, `0xFFFF`, `0xFFFFFFFF`. The target PRG legacy mux fields repeat predicate zero exactly. Depth-zero programs retain unconditional sentinels; depth-one programs use only the legacy PRG fields.

A selector must be in the same RX message, unsigned, 1..32 bits, identity-scaled, integer-backed, and earlier in topological program order. Selector paths must be exact prefixes of dependent paths. References must be in range, non-self-referential, acyclic, and no path may repeat a selector. Paths deeper than four are rejected.

Runtime predicate evaluation extracts each selector from the current frame. It never reads a selector from prior pool state. A short selector or failed outer predicate makes the dependent program inactive without modifying value, flags, or timestamp.

## Quality table

Quality has one u32 freshness threshold per pool slot in ABI order. Zero disables expiry. Nonzero values are `1..INT32_MAX` milliseconds. TX-only slots contain zero.

`SC_SLOT_STALE` is `0x08`. `sc_decode_at` records caller-provided u32 monotonic milliseconds for active slots and clears stale. `sc_expire` sets stale only when a valid, seen, enabled slot has `now - last_update >= threshold`. Disabled, unseen, invalid, inactive, and short slots are unchanged. Plain `sc_decode` neither reads nor modifies timestamps and preserves stale.

Caller time accepts equal values and forward deltas below `0x80000000`, including u32 wrap. Delta at or above the half range returns `SC_ERR_TIME` without mutation.

## Caller-owned state and reset

The existing public state prefix and TX counter array are unchanged. Let `B = offsetof(sc_runtime_state_t, counters) + counter_count * sizeof(sc_tx_counter_state_t)`. RXQ appends at B:

- last accepted API time u32;
- clock initialized flags u32;
- for each pool slot: last-update u32 and initialized flags u32.

Required RXQ state bytes are `B + 8 + 8 * pool_slot_count`. Legacy RX-only remains zero-state, and TX-only state size is unchanged.

`sc_runtime_reset` restores TX counters and clears RX timestamp state. It clears valid, updated, changed, and stale only for RX-written slots while preserving their raw values and unknown flag bits. TX-only slots are unchanged.

## Validation and limits

Existing limits remain: 4096 RX or TX messages, 8192 RX or TX programs and pool slots, 1024 conversions, and 1 MiB total image size. NMX is limited to 8192 records and depth 4. Readers reject unknown features, nonzero reserved bytes, non-dense offsets, inconsistent flags/counts, malformed sentinels, unsorted or duplicate targets, invalid indices or prefixes, thresholds above `INT32_MAX`, section overflow, and CRC mismatch.
