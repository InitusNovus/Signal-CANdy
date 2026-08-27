#include "signal_candy_runtime.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CLASSIC_IMAGE_SIZE 328u
#define CLASSIC_TX_OFFSET 140u
#define CLASSIC_TX_SIZE 184u
#define CLASSIC_POOL_COUNT 6u
#define FD_IMAGE_SIZE 236u
#define FD_TX_OFFSET 96u
#define FD_TX_SIZE 136u
#define FD_POOL_COUNT 1u

#define TX_HEADER_SIZE 32u
#define TX_MESSAGE_SIZE 24u
#define TX_PROGRAM_SIZE 16u
#define TX_COUNTER_SIZE 24u

#define LOGICAL_CLASSIC UINT32_C(10)
#define LOGICAL_FD UINT32_C(20)

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

static uint32_t get_u32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint64_t double_bits(double value)
{
    uint64_t bits;
    memcpy(&bits, &value, sizeof(bits));
    return bits;
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

static void fix_crc(uint8_t *image)
{
    uint32_t total = get_u32(image + 12);
    put_u32(image + total - 4u, fixture_crc32(image, total - 4u));
}

static void put_conversion(uint8_t *entry, uint8_t affine,
                           double factor, double offset)
{
    entry[0] = affine;
    put_u64(entry + 8, double_bits(factor));
    put_u64(entry + 16, double_bits(offset));
}

static void put_program(uint8_t *entry, uint16_t start_bit,
                        uint16_t length_bits, uint8_t order_flags,
                        uint8_t storage, uint16_t conversion_index,
                        uint16_t slot_index, uint16_t selector_slot,
                        uint32_t expected_value)
{
    put_u16(entry, start_bit);
    put_u16(entry + 2, length_bits);
    entry[4] = order_flags;
    entry[5] = storage;
    put_u16(entry + 6, conversion_index);
    put_u16(entry + 8, slot_index);
    put_u16(entry + 10, selector_slot);
    put_u32(entry + 12, expected_value);
}

static void put_tx_message(uint8_t *entry, uint32_t logical_id,
                           uint32_t encoded_can_id, uint8_t payload_length,
                           uint8_t frame_flags, uint16_t program_count,
                           uint16_t program_index, uint16_t counter_index,
                           uint32_t template_offset)
{
    put_u32(entry, logical_id);
    put_u32(entry + 4, encoded_can_id);
    entry[8] = payload_length;
    entry[9] = frame_flags;
    put_u16(entry + 10, program_count);
    put_u16(entry + 12, program_index);
    put_u16(entry + 14, counter_index);
    put_u32(entry + 16, template_offset);
}

static void put_tx_header(uint8_t *entry, uint16_t message_count,
                          uint16_t program_count, uint16_t counter_count,
                          uint32_t message_offset, uint32_t program_offset,
                          uint32_t counter_offset, uint32_t template_offset,
                          uint32_t template_size)
{
    put_u32(entry, UINT32_C(0x31305854));
    put_u16(entry + 4, message_count);
    put_u16(entry + 6, program_count);
    put_u16(entry + 8, counter_count);
    put_u32(entry + 12, message_offset);
    put_u32(entry + 16, program_offset);
    put_u32(entry + 20, counter_offset);
    put_u32(entry + 24, template_offset);
    put_u32(entry + 28, template_size);
}

static void put_common_header(uint8_t *image, uint32_t total_size,
                              uint16_t conversion_count,
                              uint16_t pool_slot_count,
                              uint32_t conversion_offset,
                              uint32_t conversion_size,
                              uint32_t symbol_offset, uint32_t symbol_size,
                              uint32_t tx_offset, uint32_t tx_size)
{
    memcpy(image, "SCIMG01\0", 8u);
    put_u16(image + 8, 1u);
    put_u16(image + 10, 1u);
    put_u32(image + 12, total_size);
    put_u16(image + 16, 0u);
    put_u16(image + 18, 0u);
    put_u16(image + 20, conversion_count);
    put_u16(image + 22, pool_slot_count);
    put_u32(image + 24, tx_offset);
    put_u32(image + 28, tx_size);

    put_u32(image + 32, 64u);
    put_u32(image + 36, 0u);
    put_u32(image + 40, 64u);
    put_u32(image + 44, 0u);
    put_u32(image + 48, conversion_offset);
    put_u32(image + 52, conversion_size);
    put_u32(image + 56, symbol_offset);
    put_u32(image + 60, symbol_size);
}

static void put_short_names(uint8_t *symbols, uint16_t count)
{
    uint16_t i;
    size_t cursor = 4u;

    put_u16(symbols, count);
    put_u16(symbols + 2, 0u);
    for (i = 0u; i < count; ++i) {
        put_u16(symbols + cursor, 2u);
        symbols[cursor + 2u] = 's';
        symbols[cursor + 3u] = (uint8_t)('0' + i);
        cursor += 4u;
    }
}

static void build_classic_fixture(uint8_t *image)
{
    uint8_t *tx;
    uint8_t *program;
    uint8_t *counter;

    memset(image, 0, CLASSIC_IMAGE_SIZE);
    put_common_header(image, CLASSIC_IMAGE_SIZE, 2u, CLASSIC_POOL_COUNT,
                      64u, 48u, 112u, 28u,
                      CLASSIC_TX_OFFSET, CLASSIC_TX_SIZE);
    put_conversion(image + 64u, 0u, 1.0, 0.0);
    put_conversion(image + 88u, 1u, 0.5, -2.0);
    put_short_names(image + 112u, CLASSIC_POOL_COUNT);

    tx = image + CLASSIC_TX_OFFSET;
    put_tx_header(tx, 1u, 6u, 1u, 32u, 56u, 152u, 176u, 8u);
    put_tx_message(tx + TX_HEADER_SIZE, LOGICAL_CLASSIC,
                   UINT32_C(0x301), 8u, 0u, 6u, 0u, 0u, 176u);

    program = tx + 56u;
    put_program(program, 0u, 2u, 0u, 0u, 0u, 0u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(program + TX_PROGRAM_SIZE, 2u, 12u, 2u, 5u, 0u, 1u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(program + 2u * TX_PROGRAM_SIZE, 16u, 12u, 3u, 5u, 0u, 2u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(program + 3u * TX_PROGRAM_SIZE, 32u, 8u, 0u, 9u, 1u, 3u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(program + 4u * TX_PROGRAM_SIZE, 40u, 8u, 0u, 0u, 0u, 4u,
                0u, 1u);
    put_program(program + 5u * TX_PROGRAM_SIZE, 40u, 8u, 0u, 0u, 0u, 5u,
                0u, 2u);

    counter = tx + 152u;
    put_u16(counter, 48u);
    put_u16(counter + 2, 4u);
    counter[4] = 0u;
    put_u32(counter + 8, 16u);
    put_u32(counter + 12, 1u);
    put_u32(counter + 16, 14u);

    fix_crc(image);
}

static void build_fd_fixture(uint8_t *image)
{
    uint8_t *tx;

    memset(image, 0, FD_IMAGE_SIZE);
    put_common_header(image, FD_IMAGE_SIZE, 1u, FD_POOL_COUNT,
                      64u, 24u, 88u, 8u, FD_TX_OFFSET, FD_TX_SIZE);
    put_conversion(image + 64u, 0u, 1.0, 0.0);
    put_short_names(image + 88u, FD_POOL_COUNT);

    tx = image + FD_TX_OFFSET;
    put_tx_header(tx, 1u, 1u, 0u, 32u, 56u, 72u, 72u, 64u);
    put_tx_message(tx + TX_HEADER_SIZE, LOGICAL_FD,
                   UINT32_C(0x80012345), 64u,
                   (uint8_t)(SC_FRAME_EXTENDED | SC_FRAME_FD),
                   1u, 0u, UINT16_C(0xFFFF), 72u);
    put_program(tx + 56u, 448u, 64u, 0u, 3u, 0u, 0u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));

    fix_crc(image);
}

static int all_zero(const void *value, size_t size)
{
    const uint8_t *bytes = (const uint8_t *)value;
    size_t i;

    for (i = 0u; i < size; ++i) {
        if (bytes[i] != 0u) {
            return 0;
        }
    }
    return 1;
}

static int zero_tail(const sc_frame_t *frame)
{
    size_t i;
    for (i = frame->len; i < sizeof(frame->data); ++i) {
        if (frame->data[i] != 0u) {
            return 0;
        }
    }
    return 1;
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
    static const uint8_t classic_expected[8] = {
        0xEDu, 0x3Fu, 0xFFu, 0x05u, 0x0Au, 0xAAu, 0x0Eu, 0x00u
    };
    uint8_t classic_image[CLASSIC_IMAGE_SIZE];
    uint8_t fd_image[FD_IMAGE_SIZE];
    uint8_t mutation[CLASSIC_IMAGE_SIZE];
    aligned_storage_t schema_storage;
    aligned_storage_t fd_schema_storage;
    aligned_storage_t other_schema_storage;
    aligned_storage_t state_storage;
    sc_schema_t *schema = (sc_schema_t *)(void *)schema_storage.bytes;
    sc_schema_t *fd_schema = (sc_schema_t *)(void *)fd_schema_storage.bytes;
    sc_schema_t *other_schema =
        (sc_schema_t *)(void *)other_schema_storage.bytes;
    sc_runtime_state_t *state = NULL;
    sc_slot_t pool[CLASSIC_POOL_COUNT];
    sc_slot_t fd_pool[FD_POOL_COUNT];
    uint8_t scratch[64];
    sc_frame_t frame;
    sc_frame_t frame_before;
    sc_tx_token_t token;
    sc_tx_token_t token_before;
    sc_tx_token_t stale;
    sc_tx_token_t busy_token;
    size_t state_bytes;
    size_t scratch_bytes;
    int passed;

    build_classic_fixture(classic_image);
    build_fd_fixture(fd_image);
    memset(&schema_storage, 0, sizeof(schema_storage));
    memset(&fd_schema_storage, 0, sizeof(fd_schema_storage));
    memset(&other_schema_storage, 0, sizeof(other_schema_storage));
    state = (void *)state_storage.bytes;

    report("open TX schemas and query counts",
           sc_schema_open(schema, classic_image, CLASSIC_IMAGE_SIZE) == SC_OK &&
           sc_schema_tx_message_count(schema) == 1u &&
           sc_schema_open(fd_schema, fd_image, FD_IMAGE_SIZE) == SC_OK &&
           sc_schema_tx_message_count(fd_schema) == 1u);

    memcpy(mutation, classic_image, sizeof(mutation));
    put_u16(mutation + 10u, 9u);
    fix_crc(mutation);
    report("unknown image feature is rejected",
           sc_schema_open(other_schema, mutation, sizeof(mutation)) ==
               SC_ERR_FEATURE);

    state_bytes = sc_schema_required_state_bytes(schema);
    scratch_bytes = sc_schema_required_scratch_bytes(schema);
    passed = state_bytes > 0u && state_bytes <= sizeof(state_storage.bytes) &&
             scratch_bytes == 8u &&
             sc_runtime_state_init(schema, state, state_bytes - 1u) ==
                 SC_ERR_STATE &&
             sc_runtime_state_init(schema, state, state_bytes) == SC_OK;
    report("caller state sizing and initialization", passed);

    memset(pool, 0, sizeof(pool));
    pool[0].raw = 1u;
    pool[0].flags = SC_SLOT_VALID;
    pool[1].raw = UINT64_C(0xFFFFFFFFFFFFFFFB);
    pool[1].flags = SC_SLOT_VALID;
    pool[2].raw = UINT64_C(0xFFFFFFFFFFFFFFFA);
    pool[2].flags = SC_SLOT_VALID;
    pool[3].raw = double_bits(3.0);
    pool[3].flags = SC_SLOT_VALID;
    pool[4].raw = UINT64_C(0xAA);
    pool[4].flags = SC_SLOT_VALID;
    pool[5].raw = UINT64_C(0x1FF);
    pool[5].flags = 0u;

    memset(&frame, 0xA5, sizeof(frame));
    memset(&token, 0x5A, sizeof(token));
    frame_before = frame;
    token_before = token;
    report("logical ID miss changes nothing",
           sc_encode_prepare(schema, state, UINT32_C(999), pool,
                             CLASSIC_POOL_COUNT, &frame, scratch,
                             scratch_bytes, &token) == SC_OK_NO_MATCH &&
           memcmp(&frame, &frame_before, sizeof(frame)) == 0 &&
           memcmp(&token, &token_before, sizeof(token)) == 0);

    report("too-small pool does not reserve",
           sc_encode_prepare(schema, state, LOGICAL_CLASSIC, pool,
                             CLASSIC_POOL_COUNT - 1u, &frame, scratch,
                             scratch_bytes, &token) == SC_ERR_POOL);
    report("too-small scratch does not reserve",
           sc_encode_prepare(schema, state, LOGICAL_CLASSIC, pool,
                             CLASSIC_POOL_COUNT, &frame, scratch,
                             scratch_bytes - 1u, &token) == SC_ERR_SCRATCH);

    pool[1].flags = 0u;
    report("active invalid slot is a value error",
           sc_encode_prepare(schema, state, LOGICAL_CLASSIC, pool,
                             CLASSIC_POOL_COUNT, &frame, scratch,
                             scratch_bytes, &token) == SC_ERR_VALUE);
    pool[1].flags = SC_SLOT_VALID;

    memset(&frame, 0xA5, sizeof(frame));
    memset(&token, 0, sizeof(token));
    passed = sc_encode_prepare(schema, state, LOGICAL_CLASSIC, pool,
                               CLASSIC_POOL_COUNT, &frame, scratch,
                               scratch_bytes, &token) == SC_OK &&
             frame.id == UINT32_C(0x301) && frame.flags == 0u &&
             frame.len == 8u &&
             memcmp(frame.data, classic_expected, sizeof(classic_expected)) == 0 &&
             zero_tail(&frame);
    report("classic LE and BE signed packing affine inverse and inactive mux", passed);

    memset(&busy_token, 0, sizeof(busy_token));
    report("second prepare while counter is pending is busy",
           sc_encode_prepare(schema, state, LOGICAL_CLASSIC, pool,
                             CLASSIC_POOL_COUNT, &frame, scratch,
                             scratch_bytes, &busy_token) == SC_ERR_BUSY);
    report("cancel clears reservation without advancing",
           sc_encode_commit(&token, 0) == SC_OK &&
           all_zero(&token, sizeof(token)));

    memset(&token, 0, sizeof(token));
    passed = sc_encode_prepare(schema, state, LOGICAL_CLASSIC, pool,
                               CLASSIC_POOL_COUNT, &frame, scratch,
                               scratch_bytes, &token) == SC_OK &&
             frame.data[6] == 14u;
    report("prepare after cancel retries the same counter", passed);

    stale = token;
    passed = sc_encode_commit(&token, 1) == SC_OK &&
             all_zero(&token, sizeof(token)) &&
             sc_encode_commit(&stale, 1) == SC_ERR_TOKEN;
    report("commit advances and copied token becomes stale", passed);

    memset(&token, 0, sizeof(token));
    passed = sc_encode_prepare(schema, state, LOGICAL_CLASSIC, pool,
                               CLASSIC_POOL_COUNT, &frame, scratch,
                               scratch_bytes, &token) == SC_OK &&
             frame.data[6] == 15u &&
             sc_encode_commit(&token, 1) == SC_OK &&
             sc_encode_prepare(schema, state, LOGICAL_CLASSIC, pool,
                               CLASSIC_POOL_COUNT, &frame, scratch,
                               scratch_bytes, &token) == SC_OK &&
             frame.data[6] == 0u && sc_encode_commit(&token, 0) == SC_OK;
    report("counter commit rolls over modulo profile", passed);

    report("counterless schema requires zero state and 64-byte scratch",
           sc_schema_required_state_bytes(fd_schema) == 0u &&
           sc_schema_required_scratch_bytes(fd_schema) == 64u);

    memset(fd_pool, 0, sizeof(fd_pool));
    fd_pool[0].raw = UINT64_C(0x0123456789ABCDEF);
    fd_pool[0].flags = SC_SLOT_VALID;
    memset(&frame, 0xA5, sizeof(frame));
    memset(&token, 0, sizeof(token));
    passed = sc_encode_prepare(fd_schema, NULL, LOGICAL_FD, fd_pool,
                               FD_POOL_COUNT, &frame, scratch,
                               sizeof(scratch), &token) == SC_OK &&
             frame.id == UINT32_C(0x12345) &&
             frame.flags == (uint8_t)(SC_FRAME_EXTENDED | SC_FRAME_FD) &&
             frame.len == 64u &&
             frame.data[56] == 0xEFu && frame.data[57] == 0xCDu &&
             frame.data[58] == 0xABu && frame.data[59] == 0x89u &&
             frame.data[60] == 0x67u && frame.data[61] == 0x45u &&
             frame.data[62] == 0x23u && frame.data[63] == 0x01u &&
             token.counter_index == UINT16_C(0xFFFF) &&
             sc_encode_commit(&token, 1) == SC_OK &&
             all_zero(&token, sizeof(token));
    report("counterless 64-byte FD encode uses NULL state", passed);

    if (failure_count != 0u) {
        printf("FAILED (%u of %u tests)\n", failure_count, test_count);
        return EXIT_FAILURE;
    }

    printf("ALL PASS (%u tests)\n", test_count);
    return EXIT_SUCCESS;
}
