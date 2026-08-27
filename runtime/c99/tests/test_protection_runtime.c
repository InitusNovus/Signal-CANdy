#include "signal_candy_runtime.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define COMBINED_IMAGE_SIZE 416u
#define COMBINED_EXTENSION_OFFSET 136u
#define COMBINED_PROFILE_OFFSET 52u
#define COMBINED_TX_OFFSET 156u
#define COMBINED_POOL_COUNT 3u
#define KAT_IMAGE_SIZE 248u
#define KAT_EXTENSION_OFFSET 136u
#define KAT_PROFILE_OFFSET 40u
#define PR_HEADER_SIZE 48u
#define PR_PLAN_SIZE 16u
#define TX_HEADER_SIZE 32u
#define TX_MESSAGE_SIZE 24u
#define TX_PROGRAM_SIZE 16u
#define TX_COUNTER_SIZE 24u
#define LOGICAL_PROTECTED UINT32_C(33)
#define RX_CAN_ID UINT32_C(0x326)
#define TX_CAN_ID UINT32_C(0x325)

typedef union {
    void *pointer_alignment;
    uint64_t integer_alignment;
    double double_alignment;
    unsigned char bytes[1024];
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

static uint32_t footer_crc32(const uint8_t *bytes, size_t count)
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

static uint8_t crc8_j1850(const uint8_t *bytes, size_t count)
{
    uint8_t crc = UINT8_C(0xFF);
    size_t i;

    for (i = 0u; i < count; ++i) {
        unsigned bit;
        crc ^= bytes[i];
        for (bit = 0u; bit < 8u; ++bit) {
            crc = (uint8_t)((crc & UINT8_C(0x80)) != 0u
                                ? (uint8_t)(crc << 1) ^ UINT8_C(0x1D)
                                : (uint8_t)(crc << 1));
        }
    }
    return crc ^ UINT8_C(0xFF);
}

static uint16_t crc16_ccitt_false(const uint8_t *bytes, size_t count)
{
    uint16_t crc = UINT16_C(0xFFFF);
    size_t i;

    for (i = 0u; i < count; ++i) {
        unsigned bit;
        crc ^= (uint16_t)((uint16_t)bytes[i] << 8);
        for (bit = 0u; bit < 8u; ++bit) {
            crc = (uint16_t)((crc & UINT16_C(0x8000)) != 0u
                                 ? (uint16_t)(crc << 1) ^ UINT16_C(0x1021)
                                 : (uint16_t)(crc << 1));
        }
    }
    return crc;
}

static void fix_footer(uint8_t *image)
{
    uint32_t size = get_u32(image + 12u);
    put_u32(image + size - 4u, footer_crc32(image, size - 4u));
}

static void put_conversion(uint8_t *entry)
{
    entry[0] = 0u;
    put_u64(entry + 8u, UINT64_C(0x3FF0000000000000));
    put_u64(entry + 16u, 0u);
}

static void put_program(uint8_t *entry, uint16_t start_bit,
                        uint16_t length_bits, uint16_t slot)
{
    put_u16(entry, start_bit);
    put_u16(entry + 2u, length_bits);
    entry[4] = 0u;
    entry[5] = 0u;
    put_u16(entry + 6u, 0u);
    put_u16(entry + 8u, slot);
    put_u16(entry + 10u, UINT16_C(0xFFFF));
    put_u32(entry + 12u, UINT32_C(0xFFFFFFFF));
}

static void put_plan(uint8_t *entry, uint8_t flags, uint8_t algorithm,
                     uint8_t crc_width, uint8_t crc_order,
                     uint16_t crc_start, uint16_t span_index,
                     uint8_t span_count, uint8_t data_id_count,
                     uint16_t counter_index, uint16_t data_id)
{
    entry[0] = flags;
    entry[1] = algorithm;
    entry[2] = crc_width;
    entry[3] = crc_order;
    put_u16(entry + 4u, crc_start);
    put_u16(entry + 6u, span_index);
    entry[8] = span_count;
    entry[9] = data_id_count;
    put_u16(entry + 10u, counter_index);
    put_u16(entry + 12u, data_id);
    put_u16(entry + 14u, 0u);
}

static void put_profile_header(uint8_t *entry, uint16_t rx_count,
                               uint16_t tx_count, uint16_t counter_count,
                               uint16_t span_count, uint32_t tx_offset,
                               uint32_t counter_offset,
                               uint32_t span_offset, uint32_t end_offset)
{
    put_u32(entry, UINT32_C(0x31305250));
    put_u16(entry + 4u, rx_count);
    put_u16(entry + 6u, tx_count);
    put_u16(entry + 8u, counter_count);
    put_u16(entry + 10u, span_count);
    put_u32(entry + 12u, PR_HEADER_SIZE);
    put_u32(entry + 16u, tx_offset);
    put_u32(entry + 20u, counter_offset);
    put_u32(entry + 24u, span_offset);
    put_u32(entry + 28u, end_offset);
}

static void put_common_header(uint8_t *image, uint32_t total_size,
                              uint16_t feature_flags, uint16_t messages,
                              uint16_t programs, uint16_t slots,
                              uint32_t message_offset,
                              uint32_t program_offset,
                              uint32_t conversion_offset,
                              uint32_t symbol_offset, uint32_t symbol_size,
                              uint32_t extension_offset,
                              uint32_t extension_size)
{
    memcpy(image, "SCIMG01\0", 8u);
    put_u16(image + 8u, 1u);
    put_u16(image + 10u, feature_flags);
    put_u32(image + 12u, total_size);
    put_u16(image + 16u, messages);
    put_u16(image + 18u, programs);
    put_u16(image + 20u, 1u);
    put_u16(image + 22u, slots);
    put_u32(image + 24u, extension_offset);
    put_u32(image + 28u, extension_size);
    put_u32(image + 32u, message_offset);
    put_u32(image + 36u, (uint32_t)messages * 8u);
    put_u32(image + 40u, program_offset);
    put_u32(image + 44u, (uint32_t)programs * 16u);
    put_u32(image + 48u, conversion_offset);
    put_u32(image + 52u, 24u);
    put_u32(image + 56u, symbol_offset);
    put_u32(image + 60u, symbol_size);
}

static void put_extension_header(uint8_t *entry, uint16_t flags,
                                 uint16_t quality_count,
                                 uint32_t quality_offset,
                                 uint32_t tx_offset, uint32_t tx_size,
                                 uint32_t profile_offset,
                                 uint32_t profile_size)
{
    put_u32(entry, UINT32_C(0x31305845));
    put_u16(entry + 4u, flags);
    entry[6] = 4u;
    entry[7] = 0u;
    put_u16(entry + 8u, 0u);
    put_u16(entry + 10u, quality_count);
    put_u32(entry + 12u, 40u);
    put_u32(entry + 16u, quality_offset);
    put_u32(entry + 20u, tx_offset);
    put_u32(entry + 24u, tx_size);
    put_u32(entry + 28u, profile_offset);
    put_u32(entry + 32u, profile_size);
}

static void put_tx_header(uint8_t *entry)
{
    put_u32(entry, UINT32_C(0x31305854));
    put_u16(entry + 4u, 1u);
    put_u16(entry + 6u, 2u);
    put_u16(entry + 8u, 1u);
    put_u32(entry + 12u, TX_HEADER_SIZE);
    put_u32(entry + 16u, TX_HEADER_SIZE + TX_MESSAGE_SIZE);
    put_u32(entry + 20u,
            TX_HEADER_SIZE + TX_MESSAGE_SIZE + 2u * TX_PROGRAM_SIZE);
    put_u32(entry + 24u,
            TX_HEADER_SIZE + TX_MESSAGE_SIZE + 2u * TX_PROGRAM_SIZE +
                TX_COUNTER_SIZE);
    put_u32(entry + 28u, 8u);
}

static void build_combined_fixture(uint8_t *image)
{
    uint8_t *extension;
    uint8_t *profile;
    uint8_t *tx;
    uint8_t *counter;

    memset(image, 0, COMBINED_IMAGE_SIZE);
    put_common_header(image, COMBINED_IMAGE_SIZE,
                      (uint16_t)(UINT16_C(0x0001) | UINT16_C(0x0002) |
                                 SC_FEATURE_PROTECTION),
                      1u, 1u, COMBINED_POOL_COUNT, 64u, 72u, 88u, 112u,
                      24u, COMBINED_EXTENSION_OFFSET,
                      COMBINED_IMAGE_SIZE - COMBINED_EXTENSION_OFFSET - 4u);

    put_u32(image + 64u, RX_CAN_ID);
    put_u16(image + 68u, 1u);
    put_u16(image + 70u, 0u);
    put_program(image + 72u, 8u, 16u, 0u);
    put_conversion(image + 88u);

    put_u16(image + 112u, COMBINED_POOL_COUNT);
    put_u16(image + 114u, 1u);
    put_u16(image + 116u, 2u);
    memcpy(image + 118u, "rx", 2u);
    put_u16(image + 120u, 2u);
    memcpy(image + 122u, "tx", 2u);
    put_u16(image + 124u, 4u);
    memcpy(image + 126u, "mark", 4u);
    put_u16(image + 130u, 1u);
    image[132] = 'm';

    extension = image + COMBINED_EXTENSION_OFFSET;
    put_extension_header(extension, 14u, COMBINED_POOL_COUNT, 40u,
                         COMBINED_TX_OFFSET, 120u,
                         COMBINED_PROFILE_OFFSET, 104u);
    put_u32(extension + 40u, 100u);
    put_u32(extension + 44u, 0u);
    put_u32(extension + 48u, 0u);

    profile = extension + COMBINED_PROFILE_OFFSET;
    put_profile_header(profile, 1u, 1u, 1u, 2u, 64u, 80u, 96u, 104u);
    put_plan(profile + 48u, 3u, 2u, 2u, 0u, 48u, 0u, 1u, 0u, 0u, 0u);
    put_plan(profile + 64u, 3u, 1u, 1u, 0u, 56u, 1u, 1u, 0u, 0u, 0u);
    put_u16(profile + 80u, 0u);
    put_u16(profile + 82u, 4u);
    profile[84] = 0u;
    put_u32(profile + 88u, 16u);
    put_u32(profile + 92u, 1u);
    profile[96] = 0u;
    profile[97] = 6u;
    profile[100] = 0u;
    profile[101] = 7u;

    tx = extension + COMBINED_TX_OFFSET;
    put_tx_header(tx);
    put_u32(tx + 32u, LOGICAL_PROTECTED);
    put_u32(tx + 36u, TX_CAN_ID);
    tx[40] = 8u;
    tx[41] = 0u;
    put_u16(tx + 42u, 2u);
    put_u16(tx + 44u, 0u);
    put_u16(tx + 46u, 0u);
    put_u32(tx + 48u, 112u);
    put_program(tx + 56u, 8u, 16u, 1u);
    put_program(tx + 72u, 24u, 8u, 2u);

    counter = tx + 88u;
    put_u16(counter, 0u);
    put_u16(counter + 2u, 4u);
    counter[4] = 0u;
    put_u32(counter + 8u, 16u);
    put_u32(counter + 12u, 1u);
    put_u32(counter + 16u, 0u);

    fix_footer(image);
}

static void build_kat_fixture(uint8_t *image, uint8_t algorithm,
                              uint8_t width, uint16_t crc_start,
                              uint8_t span_count, uint16_t data_id)
{
    uint8_t *extension;
    uint8_t *profile;

    memset(image, 0, KAT_IMAGE_SIZE);
    put_common_header(image, KAT_IMAGE_SIZE, SC_FEATURE_PROTECTION,
                      1u, 1u, 1u, 64u, 72u, 88u, 112u, 24u,
                      KAT_EXTENSION_OFFSET,
                      KAT_IMAGE_SIZE - KAT_EXTENSION_OFFSET - 4u);
    put_u32(image + 64u, UINT32_C(0x100));
    put_u16(image + 68u, 1u);
    put_u16(image + 70u, 0u);
    put_program(image + 72u, 0u, 8u, 0u);
    put_conversion(image + 88u);
    put_u16(image + 112u, 1u);
    put_u16(image + 114u, 1u);
    put_u16(image + 116u, 5u);
    memcpy(image + 118u, "value", 5u);
    put_u16(image + 123u, 3u);
    memcpy(image + 125u, "kat", 3u);

    extension = image + KAT_EXTENSION_OFFSET;
    put_extension_header(extension, 8u, 0u, 40u, 108u, 0u,
                         KAT_PROFILE_OFFSET, 68u);
    profile = extension + KAT_PROFILE_OFFSET;
    put_profile_header(profile, 1u, 0u, 0u, 1u, 64u, 64u, 64u, 68u);
    put_plan(profile + 48u, 1u, algorithm, width, 0u, crc_start, 0u, 1u,
             span_count, UINT16_C(0xFFFF), data_id);
    profile[64] = 0u;
    profile[65] = (uint8_t)(crc_start / 8u);
    fix_footer(image);
}

static void set_rx_frame(sc_frame_t *frame, uint8_t counter,
                         uint16_t value, uint8_t marker)
{
    uint16_t crc;
    memset(frame, 0, sizeof(*frame));
    frame->id = RX_CAN_ID;
    frame->len = 8u;
    frame->data[0] = (uint8_t)(counter & 0x0Fu);
    frame->data[1] = (uint8_t)value;
    frame->data[2] = (uint8_t)(value >> 8);
    frame->data[3] = marker;
    crc = crc16_ccitt_false(frame->data, 6u);
    frame->data[6] = (uint8_t)crc;
    frame->data[7] = (uint8_t)(crc >> 8);
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
    static const uint8_t check_text[9] = {
        '1', '2', '3', '4', '5', '6', '7', '8', '9'
    };
    static const uint8_t first_tx[8] = {
        0x00u, 0x34u, 0x12u, 0xA5u, 0x00u, 0x00u, 0x00u, 0xA5u
    };
    static const uint8_t second_tx[8] = {
        0x01u, 0x34u, 0x12u, 0xA5u, 0x00u, 0x00u, 0x00u, 0xF8u
    };
    uint8_t image[COMBINED_IMAGE_SIZE];
    uint8_t kat_image[KAT_IMAGE_SIZE];
    aligned_storage_t schema_storage;
    aligned_storage_t kat_schema_storage;
    aligned_storage_t state_storage;
    sc_schema_t *schema = (sc_schema_t *)(void *)schema_storage.bytes;
    sc_schema_t *kat_schema =
        (sc_schema_t *)(void *)kat_schema_storage.bytes;
    sc_runtime_state_t *state =
        (sc_runtime_state_t *)(void *)state_storage.bytes;
    sc_slot_t pool[COMBINED_POOL_COUNT];
    sc_slot_t pool_before[COMBINED_POOL_COUNT];
    sc_frame_t frame;
    sc_frame_t tx_frame;
    sc_tx_token_t token;
    uint8_t scratch[64];
    uint8_t state_before[1024];
    size_t state_bytes;
    int passed;

    report("public protection feature bit is bit four",
           SC_FEATURE_PROTECTION == UINT16_C(0x0004));
    report("frame protection statuses are distinct",
           SC_ERR_FRAME_CRC != SC_ERR_CRC &&
               SC_ERR_COUNTER != SC_ERR_FRAME_CRC);
    report("CRC algorithm check constants",
           crc8_j1850(check_text, sizeof(check_text)) == UINT8_C(0x4B) &&
               crc16_ccitt_false(check_text, sizeof(check_text)) ==
                   UINT16_C(0x29B1));

    memset(&kat_schema_storage, 0, sizeof(kat_schema_storage));
    build_kat_fixture(kat_image, 1u, 1u, 72u, 0u, 0u);
    passed = sc_schema_open(kat_schema, kat_image, sizeof(kat_image)) == SC_OK;
    memset(pool, 0, sizeof(pool));
    memset(&frame, 0, sizeof(frame));
    frame.id = UINT32_C(0x100);
    frame.flags = SC_FRAME_FD;
    frame.len = 12u;
    memcpy(frame.data, check_text, sizeof(check_text));
    frame.data[9] = UINT8_C(0x4B);
    passed = passed && sc_decode_state(kat_schema, NULL, &frame, pool, 1u) == SC_OK;
    report("runtime J1850 KAT excludes CRC field", passed);

    build_kat_fixture(kat_image, 2u, 2u, 72u, 0u, 0u);
    passed = sc_schema_open(kat_schema, kat_image, sizeof(kat_image)) == SC_OK;
    memset(pool, 0, sizeof(pool));
    memset(&frame, 0, sizeof(frame));
    frame.id = UINT32_C(0x100);
    frame.flags = SC_FRAME_FD;
    frame.len = 12u;
    memcpy(frame.data, check_text, sizeof(check_text));
    frame.data[9] = UINT8_C(0xB1);
    frame.data[10] = UINT8_C(0x29);
    passed = passed && sc_decode_state(kat_schema, NULL, &frame, pool, 1u) == SC_OK;
    report("runtime CCITT-FALSE KAT uses little-endian field", passed);

    build_kat_fixture(kat_image, 1u, 1u, 24u, 2u, UINT16_C(0x1234));
    passed = sc_schema_open(kat_schema, kat_image, sizeof(kat_image)) == SC_OK;
    memset(pool, 0, sizeof(pool));
    memset(&frame, 0, sizeof(frame));
    frame.id = UINT32_C(0x100);
    frame.len = 4u;
    frame.data[0] = 0x01u;
    frame.data[1] = 0x02u;
    frame.data[2] = 0x03u;
    frame.data[3] = 0x3Bu;
    passed = passed && sc_decode_state(kat_schema, NULL, &frame, pool, 1u) == SC_OK;
    report("runtime prepends big-endian data ID", passed);

    build_combined_fixture(image);
    memset(&schema_storage, 0, sizeof(schema_storage));
    passed = sc_schema_open(schema, image, sizeof(image)) == SC_OK &&
             sc_schema_message_count(schema) == 1u &&
             sc_schema_tx_message_count(schema) == 1u;
    report("combined RXQ TX and protection fixture opens", passed);

    state_bytes = sc_schema_required_state_bytes(schema);
    passed = state_bytes > 0u && state_bytes <= sizeof(state_storage.bytes) &&
             sc_runtime_state_init(schema, state, state_bytes) == SC_OK;
    report("caller-owned RX counter state initializes", passed);

    memset(pool, 0, sizeof(pool));
    pool[1].raw = UINT64_C(0x1234);
    pool[1].flags = SC_SLOT_VALID;
    pool[2].raw = UINT64_C(0xA5);
    pool[2].flags = SC_SLOT_VALID;
    memset(&token, 0, sizeof(token));
    passed = sc_encode_prepare(schema, state, LOGICAL_PROTECTED, pool,
                               COMBINED_POOL_COUNT, &tx_frame, scratch,
                               sizeof(scratch), &token) == SC_OK &&
             memcmp(tx_frame.data, first_tx, sizeof(first_tx)) == 0;
    report("TX inserts counter before calculating CRC", passed);

    passed = sc_encode_commit(&token, 0) == SC_OK &&
             all_zero(&token, sizeof(token)) &&
             sc_encode_prepare(schema, state, LOGICAL_PROTECTED, pool,
                               COMBINED_POOL_COUNT, &tx_frame, scratch,
                               sizeof(scratch), &token) == SC_OK &&
             memcmp(tx_frame.data, first_tx, sizeof(first_tx)) == 0;
    report("TX cancel repeats counter and CRC", passed);

    passed = sc_encode_commit(&token, 1) == SC_OK &&
             sc_encode_prepare(schema, state, LOGICAL_PROTECTED, pool,
                               COMBINED_POOL_COUNT, &tx_frame, scratch,
                               sizeof(scratch), &token) == SC_OK &&
             memcmp(tx_frame.data, second_tx, sizeof(second_tx)) == 0 &&
             sc_encode_commit(&token, 0) == SC_OK;
    report("TX transmitted commit advances counter and CRC", passed);

    sc_runtime_reset(schema, state, state_bytes, pool, COMBINED_POOL_COUNT);
    set_rx_frame(&frame, 0u, UINT16_C(0x5678), 0xBCu);
    frame.data[6] = UINT8_C(0x87);
    frame.data[7] = UINT8_C(0xC8);
    passed = sc_decode_state(schema, state, &frame, pool,
                             COMBINED_POOL_COUNT) == SC_OK &&
             pool[0].raw == UINT64_C(0x5678);
    report("RX CCITT known frame seeds first expected counter", passed);

    set_rx_frame(&frame, 1u, UINT16_C(0x5679), 0xBCu);
    passed = sc_decode_state(schema, state, &frame, pool,
                             COMBINED_POOL_COUNT) == SC_OK;
    memcpy(pool_before, pool, sizeof(pool));
    memcpy(state_before, state, state_bytes);
    set_rx_frame(&frame, 1u, UINT16_C(0x9998), 0xBCu);
    passed = passed &&
             sc_decode_state(schema, state, &frame, pool,
                             COMBINED_POOL_COUNT) == SC_ERR_COUNTER &&
             memcmp(pool_before, pool, sizeof(pool)) == 0 &&
             memcmp(state_before, state, state_bytes) == 0;
    set_rx_frame(&frame, 3u, UINT16_C(0x9999), 0xBCu);
    passed = passed &&
             sc_decode_state(schema, state, &frame, pool,
                             COMBINED_POOL_COUNT) == SC_ERR_COUNTER &&
             memcmp(pool_before, pool, sizeof(pool)) == 0 &&
             memcmp(state_before, state, state_bytes) == 0;
    set_rx_frame(&frame, 2u, UINT16_C(0x567A), 0xBCu);
    passed = passed &&
             sc_decode_state(schema, state, &frame, pool,
                             COMBINED_POOL_COUNT) == SC_OK;
    report("RX duplicate and jump reject atomically without consuming expected",
           passed);

    passed = sc_rx_counter_resync(schema, state, RX_CAN_ID, 0u) == SC_OK;
    set_rx_frame(&frame, 14u, UINT16_C(0x6000), 0u);
    passed = passed &&
             sc_decode_state(schema, state, &frame, pool,
                             COMBINED_POOL_COUNT) == SC_OK;
    set_rx_frame(&frame, 15u, UINT16_C(0x6001), 0u);
    passed = passed &&
             sc_decode_state(schema, state, &frame, pool,
                             COMBINED_POOL_COUNT) == SC_OK;
    set_rx_frame(&frame, 0u, UINT16_C(0x6002), 0u);
    passed = passed &&
             sc_decode_state(schema, state, &frame, pool,
                             COMBINED_POOL_COUNT) == SC_OK;
    report("RX explicit resync accepts arbitrary first then rollover", passed);

    sc_runtime_reset(schema, state, state_bytes, pool, COMBINED_POOL_COUNT);
    set_rx_frame(&frame, 0u, UINT16_C(0x7000), 0u);
    passed = sc_decode_at(schema, state, 100u, &frame, pool,
                          COMBINED_POOL_COUNT) == SC_OK;
    set_rx_frame(&frame, 5u, UINT16_C(0xDEAD), 0u);
    frame.data[6] ^= 1u;
    memcpy(pool_before, pool, sizeof(pool));
    memcpy(state_before, state, state_bytes);
    passed = passed &&
             sc_decode_at(schema, state, 101u, &frame, pool,
                          COMBINED_POOL_COUNT) == SC_ERR_FRAME_CRC &&
             memcmp(pool_before, pool, sizeof(pool)) == 0 &&
             memcmp(state_before, state, state_bytes) == 0;
    set_rx_frame(&frame, 1u, UINT16_C(0x7001), 0u);
    passed = passed &&
             sc_decode_at(schema, state, 101u, &frame, pool,
                          COMBINED_POOL_COUNT) == SC_OK;
    report("bad CRC precedes counter and leaves pool time and state atomic", passed);

    memcpy(pool_before, pool, sizeof(pool));
    memcpy(state_before, state, state_bytes);
    set_rx_frame(&frame, 2u, UINT16_C(0x7002), 0u);
    passed = sc_decode(schema, &frame, pool, COMBINED_POOL_COUNT) ==
                 SC_ERR_STATE &&
             memcmp(pool_before, pool, sizeof(pool)) == 0 &&
             memcmp(state_before, state, state_bytes) == 0;
    report("plain decode requires state for RX counter profile", passed);

    passed = sc_rx_counter_resync(schema, state, UINT32_C(0x777), 0u) ==
                 SC_OK_NO_MATCH &&
             sc_rx_counter_resync(schema, state, RX_CAN_ID,
                                  UINT8_C(0x80)) == SC_ERR_VALUE;
    report("RX resync miss and invalid flags are mutation free", passed);

    if (failure_count != 0u) {
        printf("FAILED (%u of %u tests)\n", failure_count, test_count);
        return EXIT_FAILURE;
    }

    printf("ALL PASS (%u tests)\n", test_count);
    return EXIT_SUCCESS;
}
