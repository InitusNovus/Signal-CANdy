#ifndef SIGNAL_CANDY_MALFORMED_IMAGE_REPRESENTATIVES_H
#define SIGNAL_CANDY_MALFORMED_IMAGE_REPRESENTATIVES_H

#include <stddef.h>
#include <stdint.h>
#include <string.h>

typedef enum {
    SC_TEST_BAD_CRC,
    SC_TEST_TRUNCATED,
    SC_TEST_BAD_RANGE,
    SC_TEST_INVALID_UTF8,
    SC_TEST_UNSUPPORTED_FEATURE,
    SC_TEST_PROTECTION_CRC_RANGE,
    SC_TEST_COUNTER_RANGE
} sc_test_malformed_kind_t;

typedef struct {
    const char *id;
    sc_test_malformed_kind_t kind;
    sc_status_t expected;
} sc_test_malformed_representative_t;

static const sc_test_malformed_representative_t
    sc_test_malformed_representatives[] = {
        {"malformed-bad-crc", SC_TEST_BAD_CRC, SC_ERR_CRC},
        {"malformed-truncated", SC_TEST_TRUNCATED, SC_ERR_SIZE},
        {"malformed-table-range", SC_TEST_BAD_RANGE, SC_ERR_BOUNDS},
        {"malformed-invalid-utf8", SC_TEST_INVALID_UTF8, SC_ERR_TABLE},
        {"malformed-unsupported-feature", SC_TEST_UNSUPPORTED_FEATURE,
         SC_ERR_FEATURE},
        {"malformed-protection-crc-range", SC_TEST_PROTECTION_CRC_RANGE,
         SC_ERR_TABLE},
        {"malformed-counter-range", SC_TEST_COUNTER_RANGE, SC_ERR_TABLE}
    };

#define SC_TEST_MALFORMED_REPRESENTATIVE_COUNT \
    (sizeof(sc_test_malformed_representatives) / \
     sizeof(sc_test_malformed_representatives[0]))

static uint16_t sc_test_read_u16(const uint8_t *bytes)
{
    return (uint16_t)((uint16_t)bytes[0] | ((uint16_t)bytes[1] << 8));
}

static uint32_t sc_test_read_u32(const uint8_t *bytes)
{
    return (uint32_t)bytes[0] | ((uint32_t)bytes[1] << 8) |
           ((uint32_t)bytes[2] << 16) | ((uint32_t)bytes[3] << 24);
}

static void sc_test_put_u32(uint8_t *bytes, uint32_t value)
{
    bytes[0] = (uint8_t)value;
    bytes[1] = (uint8_t)(value >> 8);
    bytes[2] = (uint8_t)(value >> 16);
    bytes[3] = (uint8_t)(value >> 24);
}

static uint32_t sc_test_crc32(const uint8_t *bytes, size_t count)
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

static void sc_test_fix_crc(uint8_t *image, size_t image_size)
{
    sc_test_put_u32(image + image_size - 4u,
                    sc_test_crc32(image, image_size - 4u));
}

static int sc_test_make_malformed_representative(
    uint8_t *output, size_t output_capacity, size_t *output_size,
    const uint8_t *valid, size_t valid_size, size_t representative_index)
{
    const sc_test_malformed_representative_t *representative;
    uint32_t names_offset;

    if (output == NULL || output_size == NULL || valid == NULL ||
        representative_index >= SC_TEST_MALFORMED_REPRESENTATIVE_COUNT ||
        valid_size < 68u || output_capacity < valid_size ||
        sc_test_read_u32(valid + 12u) != valid_size) {
        return 0;
    }
    representative =
        &sc_test_malformed_representatives[representative_index];
    memcpy(output, valid, valid_size);
    *output_size = valid_size;

    switch (representative->kind) {
    case SC_TEST_BAD_CRC:
        output[64] ^= UINT8_C(0x01);
        return 1;
    case SC_TEST_TRUNCATED:
        --*output_size;
        return 1;
    case SC_TEST_BAD_RANGE:
        sc_test_put_u32(output + 32u, (uint32_t)valid_size);
        sc_test_fix_crc(output, valid_size);
        return 1;
    case SC_TEST_INVALID_UTF8:
        names_offset = sc_test_read_u32(output + 56u);
        if (names_offset > valid_size - 10u ||
            sc_test_read_u16(output + names_offset + 4u) == 0u) {
            return 0;
        }
        output[names_offset + 6u] = UINT8_C(0xC0);
        sc_test_fix_crc(output, valid_size);
        return 1;
    case SC_TEST_UNSUPPORTED_FEATURE:
        output[11] |= UINT8_C(0x80);
        sc_test_fix_crc(output, valid_size);
        return 1;
    case SC_TEST_PROTECTION_CRC_RANGE: {
        uint32_t extension_offset = sc_test_read_u32(output + 24u);
        uint32_t protection_offset = extension_offset +
            sc_test_read_u32(output + extension_offset + 28u);
        uint8_t *plan = output + protection_offset + 48u;
        if ((plan[0] & 1u) == 0u || plan[2] == 0u || plan[2] > 2u ||
            (uint32_t)sc_test_read_u16(plan + 4u) +
                    (uint32_t)plan[2] * 8u >
                512u) {
            return 0;
        }
        plan[4] = (uint8_t)0xF8u;
        plan[5] = (uint8_t)0xFFu;
        sc_test_fix_crc(output, valid_size);
        return 1;
    }
    case SC_TEST_COUNTER_RANGE: {
        uint32_t extension_offset = sc_test_read_u32(output + 24u);
        uint32_t protection_offset = extension_offset +
            sc_test_read_u32(output + extension_offset + 28u);
        uint32_t counter_offset = protection_offset +
            sc_test_read_u32(output + protection_offset + 20u);
        uint16_t length = sc_test_read_u16(output + counter_offset + 2u);
        if (length == 0u || length > 32u ||
            (uint32_t)sc_test_read_u16(output + counter_offset) + length >
                512u) {
            return 0;
        }
        output[counter_offset] = (uint8_t)0xF8u;
        output[counter_offset + 1u] = (uint8_t)0xFFu;
        sc_test_fix_crc(output, valid_size);
        return 1;
    }
    default:
        return 0;
    }
}

#endif /* SIGNAL_CANDY_MALFORMED_IMAGE_REPRESENTATIVES_H */
