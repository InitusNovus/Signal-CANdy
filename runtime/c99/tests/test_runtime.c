#include "signal_candy_runtime.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define IMAGE_CAPACITY 512u
#define IMAGE_TOTAL_SIZE 316u
#define MSG_OFFSET 64u
#define PRG_OFFSET 88u
#define CNV_OFFSET 216u
#define SYM_OFFSET 264u
#define CRC_OFFSET 312u
#define SLOT_COUNT 8u

typedef union {
    void *pointer_alignment;
    uint64_t integer_alignment;
    double double_alignment;
    unsigned char bytes[128];
} schema_storage_t;

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

static uint32_t float_bits(float value)
{
    uint32_t bits;
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

static void put_message(uint8_t *entry, uint32_t can_id,
                        uint16_t program_count, uint16_t program_index)
{
    put_u32(entry, can_id);
    put_u16(entry + 4, program_count);
    put_u16(entry + 6, program_index);
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
    const char *signal_names[SLOT_COUNT] = {
        "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7"
    };
    const char *message_names[3] = {"m0", "m1", "m2"};
    size_t cursor;
    unsigned i;

    memset(image, 0, IMAGE_CAPACITY);
    memcpy(image, "SCIMG01\0", 8u);
    put_u16(image + 8, 1u);
    put_u32(image + 12, IMAGE_TOTAL_SIZE);
    put_u16(image + 16, 3u);
    put_u16(image + 18, SLOT_COUNT);
    put_u16(image + 20, 2u);

    put_u32(image + 32, MSG_OFFSET);
    put_u32(image + 36, 24u);
    put_u32(image + 40, PRG_OFFSET);
    put_u32(image + 44, 128u);
    put_u32(image + 48, CNV_OFFSET);
    put_u32(image + 52, 48u);
    put_u32(image + 56, SYM_OFFSET);
    put_u32(image + 60, 48u);

    put_message(image + MSG_OFFSET, UINT32_C(0x100), 4u, 0u);
    put_message(image + MSG_OFFSET + 8u, UINT32_C(0x200), 3u, 4u);
    put_message(image + MSG_OFFSET + 16u, UINT32_C(0x9CF00000), 1u, 7u);

    put_program(image + PRG_OFFSET, 0u, 16u, 0u, 1u, 0u, 0u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(image + PRG_OFFSET + 16u, 16u, 16u, 0u, 9u, 1u, 1u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(image + PRG_OFFSET + 32u, 32u, 8u, 0u, 9u, 0u, 6u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(image + PRG_OFFSET + 48u, 40u, 8u, 0u, 8u, 1u, 7u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(image + PRG_OFFSET + 64u, 0u, 8u, 0u, 0u, 0u, 2u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));
    put_program(image + PRG_OFFSET + 80u, 8u, 8u, 0u, 0u, 0u, 3u,
                2u, 1u);
    put_program(image + PRG_OFFSET + 96u, 8u, 8u, 0u, 0u, 0u, 4u,
                2u, 2u);
    put_program(image + PRG_OFFSET + 112u, 4u, 12u, 3u, 5u, 0u, 5u,
                UINT16_C(0xFFFF), UINT32_C(0xFFFFFFFF));

    image[CNV_OFFSET] = 0u;
    put_u64(image + CNV_OFFSET + 8u, double_bits(1.0));
    put_u64(image + CNV_OFFSET + 16u, double_bits(0.0));
    image[CNV_OFFSET + 24u] = 1u;
    put_u64(image + CNV_OFFSET + 32u, double_bits(0.5));
    put_u64(image + CNV_OFFSET + 40u, double_bits(-2.0));

    put_u16(image + SYM_OFFSET, SLOT_COUNT);
    put_u16(image + SYM_OFFSET + 2u, 3u);
    cursor = SYM_OFFSET + 4u;
    for (i = 0u; i < SLOT_COUNT; ++i) {
        append_name(image, &cursor, signal_names[i]);
    }
    for (i = 0u; i < 3u; ++i) {
        append_name(image, &cursor, message_names[i]);
    }

    fix_crc(image);
}

static void set_big_bits(uint8_t *data, uint16_t start_bit,
                         uint16_t length_bits, uint64_t value)
{
    uint16_t i;
    for (i = 0u; i < length_bits; ++i) {
        uint16_t bit_index = (uint16_t)(start_bit + i);
        uint8_t bit = (uint8_t)((value >> (length_bits - 1u - i)) & 1u);
        if (bit != 0u) {
            data[bit_index / 8u] |= (uint8_t)(1u << (bit_index % 8u));
        }
    }
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
    uint8_t image[IMAGE_CAPACITY];
    uint8_t mutation[IMAGE_CAPACITY];
    schema_storage_t schema_storage;
    schema_storage_t other_storage;
    sc_schema_t *schema = (sc_schema_t *)(void *)schema_storage.bytes;
    sc_schema_t *other = (sc_schema_t *)(void *)other_storage.bytes;
    sc_slot_t pool[SLOT_COUNT];
    sc_frame_t frame;
    sc_slot_t before0;
    sc_slot_t before1;
    sc_slot_t pool_snapshot[SLOT_COUNT];
    int passed;

    build_fixture(image);
    memset(&schema_storage, 0, sizeof(schema_storage));
    report("schema size fits caller storage",
           sc_schema_size() <= sizeof(schema_storage.bytes));
    report("open valid image",
           sc_schema_open(schema, image, IMAGE_TOTAL_SIZE) == SC_OK &&
           sc_schema_message_count(schema) == 3u &&
           sc_schema_signal_count(schema) == SLOT_COUNT &&
           sc_schema_required_state_bytes(schema) == 0u &&
           sc_schema_required_scratch_bytes(schema) == 0u);

    memset(pool, 0, sizeof(pool));
    memset(&frame, 0, sizeof(frame));
    frame.id = UINT32_C(0x100);
    frame.len = 6u;
    frame.data[0] = 0x34u;
    frame.data[1] = 0x12u;
    frame.data[2] = 10u;
    frame.data[4] = 100u;
    frame.data[5] = 8u;
    report("standard little identity and affine initial",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
           pool[0].raw == UINT64_C(0x1234) && pool[0].flags == 3u &&
           pool[1].raw == double_bits(3.0) && pool[1].flags == 3u);
    report("identity conversion with f64 storage",
           pool[6].raw == double_bits(100.0) && pool[6].flags == 3u);
    report("affine conversion with f32 storage",
           pool[7].raw == (uint64_t)float_bits(2.0f) && pool[7].flags == 3u);

    report("same standard values clear changed",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
           pool[0].raw == UINT64_C(0x1234) && pool[0].flags == 3u &&
           pool[1].raw == double_bits(3.0) && pool[1].flags == 3u);

    frame.data[0] = 0x35u;
    frame.data[2] = 12u;
    report("changed standard values set changed",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
           pool[0].raw == UINT64_C(0x1235) && pool[0].flags == 7u &&
           pool[1].raw == double_bits(4.0) && pool[1].flags == 7u);

    before0 = pool[0];
    before1 = pool[1];
    frame.len = 1u;
    report("short frame skips programs without flag changes",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
           pool[0].raw == before0.raw && pool[0].flags == before0.flags &&
           pool[1].raw == before1.raw && pool[1].flags == before1.flags);

    memset(&frame, 0, sizeof(frame));
    frame.id = UINT32_C(0x1CF00000);
    frame.flags = 1u;
    frame.len = 2u;
    set_big_bits(frame.data, 4u, 12u, UINT64_C(0xFFB));
    report("extended big signed initial",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
           pool[5].raw == UINT64_C(0xFFFFFFFFFFFFFFFB) &&
           pool[5].flags == 3u);

    report("same big signed value clears changed",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
           pool[5].raw == UINT64_C(0xFFFFFFFFFFFFFFFB) &&
           pool[5].flags == 3u);

    memset(frame.data, 0, sizeof(frame.data));
    set_big_bits(frame.data, 4u, 12u, UINT64_C(0xFFA));
    report("changed big signed value sets changed",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
           pool[5].raw == UINT64_C(0xFFFFFFFFFFFFFFFA) &&
           pool[5].flags == 7u);

    memset(&frame, 0, sizeof(frame));
    frame.id = UINT32_C(0x200);
    frame.len = 2u;
    frame.data[0] = 1u;
    frame.data[1] = 0xAAu;
    report("mux selector one decodes first branch",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
           pool[2].raw == 1u && pool[2].flags == 3u &&
           pool[3].raw == 0xAAu && pool[3].flags == 3u &&
           pool[4].flags == 0u);

    report("same mux branch clears changed",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
           pool[2].flags == 3u && pool[3].flags == 3u &&
           pool[4].flags == 0u);

    frame.data[0] = 2u;
    frame.data[1] = 0x55u;
    before0 = pool[3];
    report("mux selector two decodes second branch",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK &&
           pool[2].raw == 2u && pool[2].flags == 7u &&
           pool[3].raw == before0.raw && pool[3].flags == before0.flags &&
           pool[4].raw == 0x55u && pool[4].flags == 3u);

    memcpy(pool_snapshot, pool, sizeof(pool));
    memset(&frame, 0, sizeof(frame));
    frame.id = UINT32_C(0x321);
    frame.len = 1u;
    report("unmatched frame leaves pool unchanged",
           sc_decode(schema, &frame, pool, SLOT_COUNT) == SC_OK_NO_MATCH &&
           memcmp(pool, pool_snapshot, sizeof(pool)) == 0);

    memset(&frame, 0, sizeof(frame));
    frame.id = UINT32_C(0x100);
    frame.len = 6u;
    report("pool too small",
           sc_decode(schema, &frame, pool, SLOT_COUNT - 1u) == SC_ERR_POOL);

    memcpy(mutation, image, IMAGE_TOTAL_SIZE);
    mutation[0] ^= 1u;
    memset(&other_storage, 0, sizeof(other_storage));
    report("bad magic",
           sc_schema_open(other, mutation, IMAGE_TOTAL_SIZE) == SC_ERR_MAGIC);

    memcpy(mutation, image, IMAGE_TOTAL_SIZE);
    put_u16(mutation + 8, 2u);
    report("bad version",
           sc_schema_open(other, mutation, IMAGE_TOTAL_SIZE) == SC_ERR_VERSION);

    report("truncated image",
           sc_schema_open(other, image, IMAGE_TOTAL_SIZE - 1u) == SC_ERR_SIZE);

    memcpy(mutation, image, IMAGE_TOTAL_SIZE);
    mutation[SYM_OFFSET + 4u] ^= 1u;
    report("corrupted crc",
           sc_schema_open(other, mutation, IMAGE_TOTAL_SIZE) == SC_ERR_CRC);

    memcpy(mutation, image, IMAGE_TOTAL_SIZE);
    put_u32(mutation + 36, 23u);
    fix_crc(mutation);
    report("section size mismatch",
           sc_schema_open(other, mutation, IMAGE_TOTAL_SIZE) == SC_ERR_TABLE);

    memcpy(mutation, image, IMAGE_TOTAL_SIZE);
    put_u16(mutation + PRG_OFFSET, 500u);
    fix_crc(mutation);
    report("program out of range",
           sc_schema_open(other, mutation, IMAGE_TOTAL_SIZE) == SC_ERR_BOUNDS);

    memcpy(mutation, image, IMAGE_TOTAL_SIZE);
    put_u16(mutation + PRG_OFFSET + 6u, 1u);
    fix_crc(mutation);
    report("affine conversion with integer storage rejected",
           sc_schema_open(other, mutation, IMAGE_TOTAL_SIZE) == SC_ERR_TABLE);

    memcpy(mutation, image, IMAGE_TOTAL_SIZE);
    mutation[PRG_OFFSET + 4u] = 4u;
    fix_crc(mutation);
    report("invalid order flags rejected",
           sc_schema_open(other, mutation, IMAGE_TOTAL_SIZE) == SC_ERR_TABLE);

    passed = sc_schema_open(NULL, image, IMAGE_TOTAL_SIZE) == SC_ERR_NULL &&
             sc_schema_open(other, NULL, IMAGE_TOTAL_SIZE) == SC_ERR_NULL &&
             sc_decode(NULL, &frame, pool, SLOT_COUNT) == SC_ERR_NULL &&
             sc_decode(schema, NULL, pool, SLOT_COUNT) == SC_ERR_NULL &&
             sc_decode(schema, &frame, NULL, SLOT_COUNT) == SC_ERR_NULL;
    report("null arguments", passed);

    if (failure_count != 0u) {
        printf("FAILED (%u of %u tests)\n", failure_count, test_count);
        return EXIT_FAILURE;
    }

    printf("ALL PASS (%u tests)\n", test_count);
    return EXIT_SUCCESS;
}
