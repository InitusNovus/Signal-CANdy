#include "signal_candy_runtime.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define IMAGE_SIZE 500u
#define MESSAGE_OFFSET 64u
#define PROGRAM_OFFSET 72u
#define CONVERSION_OFFSET 184u
#define SYMBOL_OFFSET 208u
#define EXTENSION_OFFSET 248u
#define CRC_OFFSET 496u
#define SLOT_COUNT 8u
#define PROGRAM_COUNT 7u

#define EX_HEADER_SIZE 40u
#define NESTED_RECORD_SIZE 36u
#define QUALITY_OFFSET 112u
#define TX_OFFSET 144u
#define TX_SIZE 104u

#define DEPTH_IMAGE_SIZE 376u
#define DEPTH_MESSAGE_OFFSET 64u
#define DEPTH_PROGRAM_OFFSET 72u
#define DEPTH_CONVERSION_OFFSET 152u
#define DEPTH_SYMBOL_OFFSET 176u
#define DEPTH_EXTENSION_OFFSET 204u
#define DEPTH_QUALITY_OFFSET 148u
#define DEPTH_CRC_OFFSET 372u
#define DEPTH_SLOT_COUNT 5u
#define DEPTH_PROGRAM_COUNT 5u
#define DEPTH_NESTED_COUNT 3u
/* UINT32_MAX is the SCIMG v1 unconditional-program sentinel. */
#define V1_MAX_BRANCH_VALUE UINT32_C(0xFFFFFFFE)

#define RX_QUALITY_MASK                                                       \
    (SC_SLOT_VALID | SC_SLOT_UPDATED | SC_SLOT_CHANGED | SC_SLOT_STALE)

typedef union {
    void *pointer_alignment;
    uint64_t integer_alignment;
    double double_alignment;
    unsigned char bytes[512];
} aligned_storage_t;

static unsigned test_count;
static unsigned failure_count;

static void put_u16(uint8_t *p, uint16_t value)
{
    p[0] = (uint8_t)value;
    p[1] = (uint8_t)(value >> 8);
}

static void put_u32(uint8_t *p, uint32_t value)
{
    p[0] = (uint8_t)value;
    p[1] = (uint8_t)(value >> 8);
    p[2] = (uint8_t)(value >> 16);
    p[3] = (uint8_t)(value >> 24);
}

static void put_u64(uint8_t *p, uint64_t value)
{
    put_u32(p, (uint32_t)value);
    put_u32(p + 4, (uint32_t)(value >> 32));
}

static uint32_t fixture_crc32(const uint8_t *bytes, size_t count)
{
    uint32_t crc = UINT32_C(0xFFFFFFFF);
    size_t i;

    for (i = 0u; i < count; ++i) {
        unsigned bit;
        crc ^= bytes[i];
        for (bit = 0u; bit < 8u; ++bit) {
            uint32_t mask = (uint32_t)(0u - (crc & 1u));
            crc = (crc >> 1) ^ (UINT32_C(0xEDB88320) & mask);
        }
    }
    return crc ^ UINT32_C(0xFFFFFFFF);
}

static void put_program(uint8_t *entry, uint16_t start_bit,
                        uint16_t length_bits, uint16_t slot,
                        uint16_t selector_slot, uint32_t expected)
{
    put_u16(entry, start_bit);
    put_u16(entry + 2, length_bits);
    entry[4] = 0u;
    entry[5] = 0u;
    put_u16(entry + 6, 0u);
    put_u16(entry + 8, slot);
    put_u16(entry + 10, selector_slot);
    put_u32(entry + 12, expected);
}

static void put_predicate(uint8_t *entry, uint16_t program,
                          uint16_t slot, uint32_t expected)
{
    put_u16(entry, program);
    put_u16(entry + 2, slot);
    put_u32(entry + 4, expected);
}

static void put_nested_record(uint8_t *entry, uint16_t target,
                              uint32_t inner_expected)
{
    memset(entry, 0xFF, NESTED_RECORD_SIZE);
    put_u16(entry, target);
    entry[2] = 2u;
    entry[3] = 0u;
    put_predicate(entry + 4, 0u, 0u, 1u);
    put_predicate(entry + 12, 1u, 1u, inner_expected);
}

static void put_nested_path_record(uint8_t *entry, uint16_t target,
                                   uint8_t depth)
{
    uint8_t predicate;

    memset(entry, 0xFF, NESTED_RECORD_SIZE);
    put_u16(entry, target);
    entry[2] = depth;
    entry[3] = 0u;
    for (predicate = 0u; predicate < depth && predicate < 4u;
         ++predicate) {
        put_predicate(entry + 4u + (size_t)predicate * 8u,
                      predicate, predicate, V1_MAX_BRANCH_VALUE);
    }
}

static void append_name(uint8_t *image, size_t *cursor, const char *name)
{
    size_t length = strlen(name);
    put_u16(image + *cursor, (uint16_t)length);
    *cursor += 2u;
    memcpy(image + *cursor, name, length);
    *cursor += length;
}

static void build_fixture(uint8_t *image)
{
    uint8_t *extension;
    uint8_t *tx;
    size_t cursor;
    unsigned i;

    memset(image, 0, IMAGE_SIZE);
    memcpy(image, "SCIMG01\0", 8u);
    put_u16(image + 8, 1u);
    put_u16(image + 10, 3u);
    put_u32(image + 12, IMAGE_SIZE);
    put_u16(image + 16, 1u);
    put_u16(image + 18, PROGRAM_COUNT);
    put_u16(image + 20, 1u);
    put_u16(image + 22, SLOT_COUNT);
    put_u32(image + 24, EXTENSION_OFFSET);
    put_u32(image + 28, IMAGE_SIZE - EXTENSION_OFFSET - 4u);

    put_u32(image + 32, MESSAGE_OFFSET);
    put_u32(image + 36, 8u);
    put_u32(image + 40, PROGRAM_OFFSET);
    put_u32(image + 44, 16u * PROGRAM_COUNT);
    put_u32(image + 48, CONVERSION_OFFSET);
    put_u32(image + 52, 24u);
    put_u32(image + 56, SYMBOL_OFFSET);
    put_u32(image + 60, 40u);

    put_u32(image + MESSAGE_OFFSET, UINT32_C(0x324));
    put_u16(image + MESSAGE_OFFSET + 4u, PROGRAM_COUNT);
    put_u16(image + MESSAGE_OFFSET + 6u, 0u);

    put_program(image + PROGRAM_OFFSET, 0u, 2u, 0u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(image + PROGRAM_OFFSET + 16u, 2u, 2u, 1u, 0u, 1u);
    put_program(image + PROGRAM_OFFSET + 32u, 16u, 8u, 2u, 0u, 1u);
    put_program(image + PROGRAM_OFFSET + 48u, 16u, 8u, 3u, 0u, 1u);
    put_program(image + PROGRAM_OFFSET + 64u, 24u, 8u, 4u, 0u, 2u);
    put_program(image + PROGRAM_OFFSET + 80u, 32u, 8u, 5u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(image + PROGRAM_OFFSET + 96u, 40u, 8u, 6u, 0u, 3u);

    image[CONVERSION_OFFSET] = 0u;
    put_u64(image + CONVERSION_OFFSET + 8u, UINT64_C(0x3FF0000000000000));
    put_u64(image + CONVERSION_OFFSET + 16u, 0u);

    put_u16(image + SYMBOL_OFFSET, SLOT_COUNT);
    put_u16(image + SYMBOL_OFFSET + 2u, 1u);
    cursor = SYMBOL_OFFSET + 4u;
    for (i = 0u; i < SLOT_COUNT; ++i) {
        char name[3];
        name[0] = 's';
        name[1] = (char)('0' + i);
        name[2] = '\0';
        append_name(image, &cursor, name);
    }
    append_name(image, &cursor, "m0");

    extension = image + EXTENSION_OFFSET;
    put_u32(extension, UINT32_C(0x31305845));
    put_u16(extension + 4u, 7u);
    extension[6] = 4u;
    extension[7] = 0u;
    put_u16(extension + 8u, 2u);
    put_u16(extension + 10u, SLOT_COUNT);
    put_u32(extension + 12u, EX_HEADER_SIZE);
    put_u32(extension + 16u, QUALITY_OFFSET);
    put_u32(extension + 20u, TX_OFFSET);
    put_u32(extension + 24u, TX_SIZE);

    put_nested_record(extension + EX_HEADER_SIZE, 2u, 1u);
    put_nested_record(extension + EX_HEADER_SIZE + NESTED_RECORD_SIZE,
                      3u, 2u);

    put_u32(extension + QUALITY_OFFSET + 0u, 0u);
    put_u32(extension + QUALITY_OFFSET + 4u, 0u);
    put_u32(extension + QUALITY_OFFSET + 8u, 32u);
    put_u32(extension + QUALITY_OFFSET + 12u, 32u);
    put_u32(extension + QUALITY_OFFSET + 16u, 32u);
    put_u32(extension + QUALITY_OFFSET + 20u, 0u);
    put_u32(extension + QUALITY_OFFSET + 24u, 50u);
    put_u32(extension + QUALITY_OFFSET + 28u, 0u);

    tx = extension + TX_OFFSET;
    put_u32(tx, UINT32_C(0x31305854));
    put_u16(tx + 4u, 1u);
    put_u16(tx + 6u, 1u);
    put_u16(tx + 8u, 1u);
    put_u32(tx + 12u, 32u);
    put_u32(tx + 16u, 56u);
    put_u32(tx + 20u, 72u);
    put_u32(tx + 24u, 96u);
    put_u32(tx + 28u, 8u);

    put_u32(tx + 32u, 77u);
    put_u32(tx + 36u, UINT32_C(0x321));
    tx[40] = 8u;
    tx[41] = 0u;
    put_u16(tx + 42u, 1u);
    put_u16(tx + 44u, 0u);
    put_u16(tx + 46u, 0u);
    put_u32(tx + 48u, 96u);

    put_program(tx + 56u, 8u, 8u, 7u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_u16(tx + 72u, 0u);
    put_u16(tx + 74u, 4u);
    tx[76] = 0u;
    put_u32(tx + 80u, 16u);
    put_u32(tx + 84u, 1u);
    put_u32(tx + 88u, 3u);

    put_u32(image + CRC_OFFSET, fixture_crc32(image, CRC_OFFSET));
}

static void build_depth_four_fixture(uint8_t *image)
{
    uint8_t *extension;
    size_t cursor;
    unsigned i;

    memset(image, 0, DEPTH_IMAGE_SIZE);
    memcpy(image, "SCIMG01\0", 8u);
    put_u16(image + 8u, 1u);
    put_u16(image + 10u, 2u);
    put_u32(image + 12u, DEPTH_IMAGE_SIZE);
    put_u16(image + 16u, 1u);
    put_u16(image + 18u, DEPTH_PROGRAM_COUNT);
    put_u16(image + 20u, 1u);
    put_u16(image + 22u, DEPTH_SLOT_COUNT);
    put_u32(image + 24u, DEPTH_EXTENSION_OFFSET);
    put_u32(image + 28u,
            DEPTH_IMAGE_SIZE - DEPTH_EXTENSION_OFFSET - 4u);

    put_u32(image + 32u, DEPTH_MESSAGE_OFFSET);
    put_u32(image + 36u, 8u);
    put_u32(image + 40u, DEPTH_PROGRAM_OFFSET);
    put_u32(image + 44u, 16u * DEPTH_PROGRAM_COUNT);
    put_u32(image + 48u, DEPTH_CONVERSION_OFFSET);
    put_u32(image + 52u, 24u);
    put_u32(image + 56u, DEPTH_SYMBOL_OFFSET);
    put_u32(image + 60u, DEPTH_EXTENSION_OFFSET - DEPTH_SYMBOL_OFFSET);

    put_u32(image + DEPTH_MESSAGE_OFFSET, UINT32_C(0x325));
    put_u16(image + DEPTH_MESSAGE_OFFSET + 4u, DEPTH_PROGRAM_COUNT);
    put_u16(image + DEPTH_MESSAGE_OFFSET + 6u, 0u);

    put_program(image + DEPTH_PROGRAM_OFFSET, 0u, 32u, 0u,
                UINT16_MAX, UINT32_MAX);
    for (i = 1u; i < DEPTH_PROGRAM_COUNT; ++i) {
        uint16_t start = i == DEPTH_PROGRAM_COUNT - 1u
            ? 128u : (uint16_t)(i * 32u);
        uint16_t length = i == DEPTH_PROGRAM_COUNT - 1u ? 8u : 32u;
        put_program(image + DEPTH_PROGRAM_OFFSET + i * 16u,
                    start, length, (uint16_t)i, 0u,
                    V1_MAX_BRANCH_VALUE);
    }

    image[DEPTH_CONVERSION_OFFSET] = 0u;
    put_u64(image + DEPTH_CONVERSION_OFFSET + 8u,
            UINT64_C(0x3FF0000000000000));
    put_u64(image + DEPTH_CONVERSION_OFFSET + 16u, 0u);

    put_u16(image + DEPTH_SYMBOL_OFFSET, DEPTH_SLOT_COUNT);
    put_u16(image + DEPTH_SYMBOL_OFFSET + 2u, 1u);
    cursor = DEPTH_SYMBOL_OFFSET + 4u;
    for (i = 0u; i < DEPTH_SLOT_COUNT; ++i) {
        char name[3];
        name[0] = 'd';
        name[1] = (char)('0' + i);
        name[2] = '\0';
        append_name(image, &cursor, name);
    }
    append_name(image, &cursor, "m4");

    extension = image + DEPTH_EXTENSION_OFFSET;
    put_u32(extension, UINT32_C(0x31305845));
    put_u16(extension + 4u, 3u);
    extension[6] = 4u;
    extension[7] = 0u;
    put_u16(extension + 8u, DEPTH_NESTED_COUNT);
    put_u16(extension + 10u, DEPTH_SLOT_COUNT);
    put_u32(extension + 12u, EX_HEADER_SIZE);
    put_u32(extension + 16u, DEPTH_QUALITY_OFFSET);
    put_u32(extension + 20u,
            DEPTH_QUALITY_OFFSET + DEPTH_SLOT_COUNT * 4u);
    put_u32(extension + 24u, 0u);

    put_nested_path_record(extension + EX_HEADER_SIZE, 2u, 2u);
    put_nested_path_record(extension + EX_HEADER_SIZE + NESTED_RECORD_SIZE,
                           3u, 3u);
    put_nested_path_record(extension + EX_HEADER_SIZE +
                               2u * NESTED_RECORD_SIZE,
                           4u, 4u);

    put_u32(image + DEPTH_CRC_OFFSET,
            fixture_crc32(image, DEPTH_CRC_OFFSET));
}

static void set_mux_frame(sc_frame_t *frame, uint8_t outer, uint8_t inner,
                          uint8_t nested, uint8_t outer_value,
                          uint8_t disabled, uint8_t gated)
{
    memset(frame, 0, sizeof(*frame));
    frame->id = UINT32_C(0x324);
    frame->len = 6u;
    frame->data[0] = (uint8_t)(outer | (uint8_t)(inner << 2));
    frame->data[2] = nested;
    frame->data[3] = outer_value;
    frame->data[4] = disabled;
    frame->data[5] = gated;
}

static void report(const char *name, int passed)
{
    ++test_count;
    if (passed) {
        printf("PASS: %s\n", name);
    } else {
        ++failure_count;
        printf("FAIL: %s\n", name);
    }
}

int main(void)
{
    uint8_t image[IMAGE_SIZE];
    uint8_t depth_image[DEPTH_IMAGE_SIZE];
    uint8_t depth_five_image[DEPTH_IMAGE_SIZE];
    aligned_storage_t schema_storage;
    aligned_storage_t other_schema_storage;
    aligned_storage_t depth_schema_storage;
    aligned_storage_t depth_reject_schema_storage;
    aligned_storage_t state_storage;
    aligned_storage_t wrap_state_storage;
    aligned_storage_t foreign_state_storage;
    sc_schema_t *schema = (sc_schema_t *)(void *)schema_storage.bytes;
    sc_schema_t *other = (sc_schema_t *)(void *)other_schema_storage.bytes;
    sc_schema_t *depth_schema =
        (sc_schema_t *)(void *)depth_schema_storage.bytes;
    sc_schema_t *depth_reject_schema =
        (sc_schema_t *)(void *)depth_reject_schema_storage.bytes;
    sc_runtime_state_t *state =
        (sc_runtime_state_t *)(void *)state_storage.bytes;
    sc_runtime_state_t *wrap_state =
        (sc_runtime_state_t *)(void *)wrap_state_storage.bytes;
    sc_runtime_state_t *foreign_state =
        (sc_runtime_state_t *)(void *)foreign_state_storage.bytes;
    sc_slot_t pool[SLOT_COUNT];
    sc_slot_t snapshot[SLOT_COUNT];
    sc_slot_t depth_pool[DEPTH_SLOT_COUNT];
    sc_frame_t frame;
    size_t state_bytes;
    uint8_t state_snapshot[512];
    uint64_t raw_snapshot[SLOT_COUNT];
    unsigned i;
    int passed;
    int first_cycle_passed;
    int first_refresh_passed;
    int depth_decode_passed;

    build_fixture(image);
    memset(&schema_storage, 0, sizeof(schema_storage));
    memset(&other_schema_storage, 0, sizeof(other_schema_storage));
    report("open combined nested quality and TX image",
           sc_schema_open(schema, image, sizeof(image)) == SC_OK &&
               sc_schema_message_count(schema) == 1u &&
               sc_schema_signal_count(schema) == SLOT_COUNT &&
               sc_schema_tx_message_count(schema) == 1u);

    state_bytes = sc_schema_required_state_bytes(schema);
    passed = state_bytes > 0u && state_bytes <= sizeof(state_storage.bytes) &&
             sc_runtime_state_init(schema, state, state_bytes) == SC_OK;
    report("caller owned quality state initializes", passed);

    memset(pool, 0, sizeof(pool));
    pool[7].raw = UINT64_C(0x77);
    pool[7].flags = SC_SLOT_VALID | SC_SLOT_STALE | UINT32_C(0x100);
    set_mux_frame(&frame, 1u, 1u, 0xAAu, 0x44u, 0x55u, 0x66u);
    passed = sc_decode_at(schema, state, 100u, &frame, pool, SLOT_COUNT) ==
                 SC_OK &&
             pool[0].raw == 1u && pool[0].flags == 3u &&
             pool[1].raw == 1u && pool[1].flags == 3u &&
             pool[2].raw == UINT64_C(0xAA) && pool[2].flags == 3u &&
             pool[3].flags == 0u && pool[4].flags == 0u &&
             pool[5].raw == UINT64_C(0x55) && pool[5].flags == 3u &&
             pool[6].flags == 0u && pool[7].raw == UINT64_C(0x77) &&
             pool[7].flags ==
                 (SC_SLOT_VALID | SC_SLOT_STALE | UINT32_C(0x100));
    report("outer and inner predicates gate nested branch", passed);

    snapshot[1] = pool[1];
    snapshot[2] = pool[2];
    set_mux_frame(&frame, 2u, 1u, 0xCCu, 0x45u, 0x56u, 0x67u);
    passed = sc_decode_at(schema, state, 101u, &frame, pool, SLOT_COUNT) ==
                 SC_OK &&
             pool[1].raw == snapshot[1].raw &&
             pool[1].flags == snapshot[1].flags &&
             pool[2].raw == snapshot[2].raw &&
             pool[2].flags == snapshot[2].flags &&
             pool[4].raw == UINT64_C(0x45) && pool[4].flags == 3u;
    report("outer mismatch gates inner selector and descendants", passed);

    set_mux_frame(&frame, 1u, 2u, 0xBBu, 0x46u, 0x57u, 0x68u);
    passed = sc_decode_at(schema, state, 102u, &frame, pool, SLOT_COUNT) ==
                 SC_OK &&
             pool[2].raw == UINT64_C(0xAA) &&
             pool[3].raw == UINT64_C(0xBB) && pool[3].flags == 3u;
    report("inner predicate selects only its current branch", passed);

    pool[0].raw = 1u;
    pool[1].raw = 1u;
    snapshot[2] = pool[2];
    set_mux_frame(&frame, 2u, 1u, 0xEEu, 0x47u, 0x58u, 0x69u);
    passed = sc_decode_at(schema, state, 103u, &frame, pool, SLOT_COUNT) ==
                 SC_OK &&
             pool[2].raw == snapshot[2].raw &&
             pool[2].flags == snapshot[2].flags;
    report("gating extracts selectors from current frame never prior pool", passed);

    passed = sc_expire(schema, state, 131u, pool, SLOT_COUNT) == SC_OK &&
             (pool[2].flags & SC_SLOT_STALE) == 0u &&
             sc_expire(schema, state, 132u, pool, SLOT_COUNT) == SC_OK &&
             (pool[2].flags & SC_SLOT_STALE) != 0u &&
             pool[2].raw == UINT64_C(0xAA);
    first_cycle_passed = passed;
    report("exact freshness expiry boundary", passed);

    set_mux_frame(&frame, 1u, 1u, 0xAAu, 0x48u, 0x59u, 0x6Au);
    passed = sc_decode_at(schema, state, 140u, &frame, pool, SLOT_COUNT) ==
                 SC_OK &&
             pool[2].raw == UINT64_C(0xAA) && pool[2].flags == 3u;
    first_refresh_passed = passed;
    report("same value refresh clears stale without changed", passed);

    passed = sc_expire(schema, state, 172u, pool, SLOT_COUNT) == SC_OK &&
             (pool[2].flags & SC_SLOT_STALE) != 0u;
    set_mux_frame(&frame, 1u, 1u, 0xABu, 0x49u, 0x5Au, 0x6Bu);
    passed = passed &&
             sc_decode_at(schema, state, 180u, &frame, pool, SLOT_COUNT) ==
                 SC_OK &&
             pool[2].raw == UINT64_C(0xAB) && pool[2].flags == 7u;
    passed = first_cycle_passed && first_refresh_passed && passed;
    report("repeated stale/refresh cycles", passed);

    snapshot[5] = pool[5];
    pool[6].flags = SC_SLOT_VALID;
    passed = sc_expire(schema, state, 230u, pool, SLOT_COUNT) == SC_OK &&
             pool[5].raw == snapshot[5].raw &&
             pool[5].flags == snapshot[5].flags &&
             pool[6].flags == SC_SLOT_VALID;
    report("disabled and unseen freshness entries do not expire", passed);

    set_mux_frame(&frame, 3u, 0u, 0u, 0u, 0x5Bu, 0x6Cu);
    passed = sc_decode_at(schema, state, 240u, &frame, pool, SLOT_COUNT) ==
                 SC_OK &&
             pool[6].raw == UINT64_C(0x6C) &&
             (pool[6].flags & SC_SLOT_VALID) != 0u;
    pool[6].flags &= ~SC_SLOT_VALID;
    passed = passed &&
             sc_expire(schema, state, 290u, pool, SLOT_COUNT) == SC_OK &&
             (pool[6].flags & SC_SLOT_STALE) == 0u;
    report("invalid slot with timestamp does not expire", passed);

    memset(&wrap_state_storage, 0, sizeof(wrap_state_storage));
    report("second caller state initializes",
           sc_runtime_state_init(schema, wrap_state, state_bytes) == SC_OK);
    set_mux_frame(&frame, 1u, 1u, 0x10u, 0u, 0u, 0u);
    passed = sc_decode_at(schema, wrap_state, UINT32_C(0xFFFFFFF0), &frame,
                          pool, SLOT_COUNT) == SC_OK &&
             sc_expire(schema, wrap_state, UINT32_C(0x0000000F), pool,
                       SLOT_COUNT) == SC_OK &&
             (pool[2].flags & SC_SLOT_STALE) == 0u &&
             sc_expire(schema, wrap_state, UINT32_C(0x00000010), pool,
                       SLOT_COUNT) == SC_OK &&
             (pool[2].flags & SC_SLOT_STALE) != 0u;
    report("uint32 now_ms wrap boundary", passed);

    memcpy(snapshot, pool, sizeof(pool));
    memcpy(state_snapshot, wrap_state, state_bytes);
    passed = sc_expire(schema, wrap_state, UINT32_C(0x0000000F), pool,
                       SLOT_COUNT) == SC_ERR_TIME &&
             memcmp(snapshot, pool, sizeof(pool)) == 0 &&
             memcmp(state_snapshot, wrap_state, state_bytes) == 0;
    report("uint32 now_ms regression handling", passed);

    memcpy(snapshot, pool, sizeof(pool));
    memcpy(state_snapshot, wrap_state, state_bytes);
    passed = sc_expire(schema, wrap_state, UINT32_C(0x80000010), pool,
                       SLOT_COUNT) == SC_ERR_TIME &&
             memcmp(snapshot, pool, sizeof(pool)) == 0 &&
             memcmp(state_snapshot, wrap_state, state_bytes) == 0;
    report("exact half range is rejected without mutation", passed);

    memset(&other_schema_storage, 0, sizeof(other_schema_storage));
    memset(&foreign_state_storage, 0, sizeof(foreign_state_storage));
    passed = sc_schema_open(other, image, sizeof(image)) == SC_OK &&
             sc_runtime_state_init(other, foreign_state, state_bytes) == SC_OK;
    memcpy(snapshot, pool, sizeof(pool));
    memcpy(state_snapshot, foreign_state, state_bytes);
    passed = passed &&
             sc_expire(schema, foreign_state, 1u, pool, SLOT_COUNT) ==
                 SC_ERR_STATE &&
             memcmp(snapshot, pool, sizeof(pool)) == 0 &&
             memcmp(state_snapshot, foreign_state, state_bytes) == 0;
    report("foreign caller state is rejected without mutation", passed);

    for (i = 0u; i < SLOT_COUNT; ++i) {
        raw_snapshot[i] = pool[i].raw;
        pool[i].flags |= RX_QUALITY_MASK | UINT32_C(0x100);
    }
    pool[7].raw = UINT64_C(0x7777);
    pool[7].flags = SC_SLOT_VALID | SC_SLOT_STALE | UINT32_C(0x200);
    state->counters[0].current = 9u;
    state->counters[0].pending_generation = 10u;
    state->counters[0].next_generation = 11u;
    passed = sc_runtime_reset(schema, state, state_bytes, pool, SLOT_COUNT) ==
             SC_OK;
    for (i = 0u; i < PROGRAM_COUNT; ++i) {
        passed = passed && pool[i].raw == raw_snapshot[i] &&
                 (pool[i].flags & RX_QUALITY_MASK) == 0u &&
                 (pool[i].flags & UINT32_C(0x100)) != 0u;
    }
    passed = passed && pool[7].raw == UINT64_C(0x7777) &&
             pool[7].flags == (SC_SLOT_VALID | UINT32_C(0x200)) &&
             state->counters[0].current == 3u &&
             state->counters[0].pending_generation == 0u &&
             state->counters[0].next_generation == 1u;
    report("reset clears transient flags and restores counters", passed);

    set_mux_frame(&frame, 1u, 1u, 0x20u, 0u, 0u, 0u);
    passed = sc_decode_at(schema, state, 0u, &frame, pool, SLOT_COUNT) ==
                 SC_OK &&
             sc_expire(schema, state, 32u, pool, SLOT_COUNT) == SC_OK &&
             (pool[2].flags & SC_SLOT_STALE) != 0u;
    memcpy(state_snapshot, state, state_bytes);
    frame.data[2] = 0x21u;
    passed = passed && sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
             pool[2].raw == UINT64_C(0x21) &&
             (pool[2].flags & SC_SLOT_STALE) != 0u &&
             memcmp(state_snapshot, state, state_bytes) == 0;
    report("plain decode preserves stale and freshness state", passed);

    build_depth_four_fixture(depth_image);
    memset(&depth_schema_storage, 0, sizeof(depth_schema_storage));
    passed = sc_schema_open(depth_schema, depth_image,
                            sizeof(depth_image)) == SC_OK &&
             sc_schema_message_count(depth_schema) == 1u &&
             sc_schema_signal_count(depth_schema) == DEPTH_SLOT_COUNT;
    report("open runtime depth-four v1 image", passed);

    memset(depth_pool, 0, sizeof(depth_pool));
    memset(&frame, 0, sizeof(frame));
    frame.id = UINT32_C(0x325);
    frame.len = 17u;
    for (i = 0u; i < 4u; ++i) {
        put_u32(frame.data + i * 4u, V1_MAX_BRANCH_VALUE);
    }
    frame.data[16] = UINT8_C(0xA5);
    depth_decode_passed =
        passed && sc_decode(depth_schema, &frame, depth_pool,
                            DEPTH_SLOT_COUNT) == SC_OK;
    passed = depth_decode_passed;
    for (i = 0u; i < 4u; ++i) {
        passed = passed &&
                 depth_pool[i].raw == V1_MAX_BRANCH_VALUE &&
                 depth_pool[i].flags ==
                     (SC_SLOT_VALID | SC_SLOT_UPDATED);
    }
    report("v1 maximum 32-bit selector and branch value", passed);
    passed = depth_decode_passed &&
             depth_pool[4].raw == UINT64_C(0xA5) &&
             depth_pool[4].flags ==
                 (SC_SLOT_VALID | SC_SLOT_UPDATED);
    report("runtime depth-four nested mux happy path", passed);

    memcpy(depth_five_image, depth_image, sizeof(depth_image));
    depth_five_image[DEPTH_EXTENSION_OFFSET + EX_HEADER_SIZE +
                     2u * NESTED_RECORD_SIZE + 2u] = 5u;
    put_u32(depth_five_image + DEPTH_CRC_OFFSET,
            fixture_crc32(depth_five_image, DEPTH_CRC_OFFSET));
    memset(&depth_reject_schema_storage, 0,
           sizeof(depth_reject_schema_storage));
    report("runtime rejects nested mux depth five",
           sc_schema_open(depth_reject_schema, depth_five_image,
                          sizeof(depth_five_image)) == SC_ERR_TABLE);

    if (failure_count != 0u) {
        printf("FAILED (%u of %u tests)\n", failure_count, test_count);
        return EXIT_FAILURE;
    }

    printf("ALL PASS (%u tests)\n", test_count);
    return EXIT_SUCCESS;
}
