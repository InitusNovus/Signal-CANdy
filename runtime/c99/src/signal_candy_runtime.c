#include "signal_candy_runtime.h"

#include <math.h>
#include <string.h>

#define SC_IMAGE_LIMIT ((size_t)1048576u)
#define SC_MESSAGE_LIMIT ((uint16_t)4096u)
#define SC_SIGNAL_LIMIT ((uint16_t)8192u)
#define SC_CONVERSION_LIMIT ((uint16_t)1024u)
#define SC_SCHEMA_TAG UINT32_C(0x53435231)
#define SC_FEATURE_TX UINT16_C(0x0001)
#define SC_FEATURE_RXQ UINT16_C(0x0002)
#define SC_FEATURE_PROTECTION UINT16_C(0x0004)
#define SC_EX_MAGIC UINT32_C(0x31305845)
#define SC_EX_HEADER_SIZE UINT32_C(40)
#define SC_NMX_SIZE UINT32_C(36)
#define SC_TX_MAGIC UINT32_C(0x31305854)
#define SC_TX_HEADER_SIZE UINT32_C(32)
#define SC_TX_MESSAGE_SIZE UINT32_C(24)
#define SC_PROGRAM_SIZE UINT32_C(16)
#define SC_COUNTER_SIZE UINT32_C(24)
#define SC_PR_MAGIC UINT32_C(0x31305250)
#define SC_PR_HEADER_SIZE UINT32_C(48)
#define SC_PR_PLAN_SIZE UINT32_C(16)
#define SC_RX_COUNTER_SIZE UINT32_C(16)
#define SC_SPAN_SIZE UINT32_C(4)
#define SC_SPAN_LIMIT UINT16_C(16384)
#define SC_LOCAL static

struct sc_schema {
    const uint8_t *image;
    size_t image_size;
    uint32_t msg_offset;
    uint32_t prg_offset;
    uint32_t cnv_offset;
    uint32_t extension_offset;
    uint32_t nested_offset;
    uint32_t quality_offset;
    uint32_t protection_offset;
    uint32_t rx_counter_offset;
    uint32_t span_offset;
    uint32_t tx_offset;
    uint32_t tx_message_offset;
    uint32_t tx_program_offset;
    uint32_t tx_counter_offset;
    uint32_t tx_template_offset;
    uint16_t message_count;
    uint16_t signal_count;
    uint16_t pool_slot_count;
    uint16_t conversion_count;
    uint16_t tx_message_count;
    uint16_t tx_program_count;
    uint16_t counter_count;
    uint16_t rx_counter_count;
    uint16_t span_count;
    uint16_t nested_count;
    uint8_t required_scratch;
    uint8_t has_rxq;
    uint8_t has_protection;
    uint32_t tag;
};

SC_LOCAL uint16_t sc_read_u16(const uint8_t *p)
{
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

SC_LOCAL uint32_t sc_read_u32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

SC_LOCAL uint64_t sc_read_u64(const uint8_t *p)
{
    return (uint64_t)sc_read_u32(p) |
           ((uint64_t)sc_read_u32(p + 4) << 32);
}

SC_LOCAL uint32_t sc_crc32(const uint8_t *bytes, size_t count)
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

SC_LOCAL int sc_bytes_are_zero(const uint8_t *bytes, size_t begin,
                               size_t end)
{
    size_t i;
    for (i = begin; i < end; ++i) {
        if (bytes[i] != 0u) {
            return 0;
        }
    }
    return 1;
}

SC_LOCAL int sc_schema_is_open(const sc_schema_t *schema)
{
    return schema != NULL && schema->tag == SC_SCHEMA_TAG &&
           schema->image != NULL;
}

SC_LOCAL int sc_valid_can_id(uint32_t encoded)
{
    if ((encoded & UINT32_C(0x80000000)) != 0u) {
        return (encoded & UINT32_C(0x60000000)) == 0u;
    }
    return encoded <= UINT32_C(0x7FF);
}

SC_LOCAL int sc_valid_payload_length(uint8_t length)
{
    return length <= 8u || length == 12u || length == 16u ||
           length == 20u || length == 24u || length == 32u ||
           length == 48u || length == 64u;
}

SC_LOCAL int sc_ranges_overlap(uint16_t start_a, uint16_t length_a,
                               uint16_t start_b, uint16_t length_b)
{
    return (uint32_t)start_a < (uint32_t)start_b + length_b &&
           (uint32_t)start_b < (uint32_t)start_a + length_a;
}

SC_LOCAL sc_status_t sc_validate_program(const uint8_t *entry,
                                         uint16_t slot_count,
                                         uint16_t conversion_count,
                                         uint32_t payload_bits)
{
    uint16_t start_bit = sc_read_u16(entry);
    uint16_t length_bits = sc_read_u16(entry + 2);
    uint8_t order_flags = entry[4];
    uint8_t storage = entry[5];
    uint16_t conversion_index = sc_read_u16(entry + 6);
    uint16_t slot_index = sc_read_u16(entry + 8);
    uint16_t selector_slot = sc_read_u16(entry + 10);
    uint32_t expected = sc_read_u32(entry + 12);
    int unconditional = selector_slot == UINT16_C(0xFFFF);

    if (length_bits == 0u || length_bits > 64u ||
        (uint32_t)start_bit + length_bits > payload_bits) {
        return SC_ERR_BOUNDS;
    }
    if (order_flags > 3u || storage > 9u) {
        return SC_ERR_TABLE;
    }
    if (conversion_index >= conversion_count || slot_index >= slot_count) {
        return SC_ERR_BOUNDS;
    }
    if (storage <= 7u && conversion_index != 0u) {
        return SC_ERR_TABLE;
    }
    if (unconditional != (expected == UINT32_C(0xFFFFFFFF))) {
        return SC_ERR_TABLE;
    }
    if (!unconditional &&
        (selector_slot >= slot_count || selector_slot == slot_index)) {
        return SC_ERR_BOUNDS;
    }
    return SC_OK;
}

SC_LOCAL sc_status_t sc_validate_selector(const uint8_t *programs,
                                          uint16_t count)
{
    uint16_t selector_slot = UINT16_C(0xFFFF);
    uint16_t i;

    for (i = 0u; i < count; ++i) {
        const uint8_t *program = programs + (size_t)i * 16u;
        uint16_t candidate = sc_read_u16(program + 10);
        if (candidate != UINT16_C(0xFFFF)) {
            if (selector_slot != UINT16_C(0xFFFF) &&
                selector_slot != candidate) {
                return SC_ERR_TABLE;
            }
            selector_slot = candidate;
        }
    }
    if (selector_slot != UINT16_C(0xFFFF)) {
        if (count == 0u || sc_read_u16(programs + 8) != selector_slot ||
            sc_read_u16(programs + 10) != UINT16_C(0xFFFF) ||
            sc_read_u32(programs + 12) != UINT32_C(0xFFFFFFFF)) {
            return SC_ERR_TABLE;
        }
    }
    return SC_OK;
}

SC_LOCAL int sc_is_valid_utf8(const uint8_t *bytes, uint16_t length)
{
    uint16_t cursor = 0u;
    while (cursor < length) {
        uint8_t first = bytes[cursor++];
        uint8_t continuation;
        if (first <= 0x7Fu) {
            if (first == 0u) return 0;
            continue;
        }
        if (first >= 0xC2u && first <= 0xDFu) {
            if (cursor >= length) return 0;
            continuation = bytes[cursor++];
            if (continuation < 0x80u || continuation > 0xBFu) return 0;
            continue;
        }
        if (first >= 0xE0u && first <= 0xEFu) {
            uint8_t second;
            if ((uint16_t)(length - cursor) < 2u) return 0;
            second = bytes[cursor++];
            continuation = bytes[cursor++];
            if (continuation < 0x80u || continuation > 0xBFu ||
                second < 0x80u || second > 0xBFu ||
                (first == 0xE0u && second < 0xA0u) ||
                (first == 0xEDu && second > 0x9Fu)) return 0;
            continue;
        }
        if (first >= 0xF0u && first <= 0xF4u) {
            uint8_t second;
            uint8_t third;
            if ((uint16_t)(length - cursor) < 3u) return 0;
            second = bytes[cursor++];
            third = bytes[cursor++];
            continuation = bytes[cursor++];
            if (second < 0x80u || second > 0xBFu ||
                third < 0x80u || third > 0xBFu ||
                continuation < 0x80u || continuation > 0xBFu ||
                (first == 0xF0u && second < 0x90u) ||
                (first == 0xF4u && second > 0x8Fu)) return 0;
            continue;
        }
        return 0;
    }
    return 1;
}

SC_LOCAL sc_status_t sc_validate_symbols(const uint8_t *bytes,
                                         uint32_t offset, uint32_t size,
                                         uint16_t signal_count,
                                         uint16_t message_count)
{
    uint32_t cursor;
    uint32_t end;
    uint32_t count;
    uint32_t i;

    if (size < 4u || sc_read_u16(bytes + offset) != signal_count ||
        sc_read_u16(bytes + offset + 2u) != message_count) {
        return SC_ERR_TABLE;
    }
    cursor = offset + 4u;
    end = offset + size;
    count = (uint32_t)signal_count + message_count;
    for (i = 0u; i < count; ++i) {
        uint16_t length;
        uint16_t j;
        if (cursor > end || end - cursor < 2u) {
            return SC_ERR_BOUNDS;
        }
        length = sc_read_u16(bytes + cursor);
        cursor += 2u;
        if (length == 0u || length > 255u || cursor > end ||
            length > end - cursor) {
            return SC_ERR_TABLE;
        }
        for (j = 0u; j < length; ++j) {
            if (bytes[cursor + j] == 0u) {
                return SC_ERR_TABLE;
            }
        }
        if (!sc_is_valid_utf8(bytes + cursor, length)) {
            return SC_ERR_TABLE;
        }
        cursor += length;
    }
    if (!sc_bytes_are_zero(bytes, cursor, end)) {
        return SC_ERR_TABLE;
    }
    return SC_OK;
}

size_t sc_schema_size(void)
{
    return sizeof(sc_schema_t);
}

sc_status_t sc_schema_open(sc_schema_t *schema, const void *image,
                           size_t image_size)
{
    static const uint8_t expected_magic[8] = {
        0x53u, 0x43u, 0x49u, 0x4Du, 0x47u, 0x30u, 0x31u, 0x00u
    };
    const uint8_t *bytes;
    uint32_t offsets[4];
    uint32_t sizes[4];
    uint32_t total_size;
    uint32_t crc_offset;
    uint32_t directory_end;
    uint32_t previous_end;
    uint32_t expected_program;
    uint16_t feature_flags;
    uint16_t message_count;
    uint16_t signal_count;
    uint16_t pool_slot_count;
    uint16_t conversion_count;
    uint16_t tx_message_count = 0u;
    uint16_t tx_program_count = 0u;
    uint16_t counter_count = 0u;
    uint16_t nested_count = 0u;
    uint16_t rx_counter_count = 0u;
    uint16_t span_count = 0u;
    uint32_t extension_offset = 0u;
    uint32_t extension_size = 0u;
    uint32_t nested_offset = 0u;
    uint32_t quality_offset = 0u;
    uint32_t protection_offset = 0u;
    uint32_t rx_counter_offset = 0u;
    uint32_t span_offset = 0u;
    uint32_t tx_offset = 0u;
    uint32_t tx_size = 0u;
    uint32_t tx_message_offset = 0u;
    uint32_t tx_program_offset = 0u;
    uint32_t tx_counter_offset = 0u;
    uint32_t tx_template_offset = 0u;
    uint32_t tx_template_size = 0u;
    uint8_t required_scratch = 0u;
    unsigned section;
    uint16_t i;
    struct sc_schema parsed;

    if (schema == NULL || image == NULL) {
        return SC_ERR_NULL;
    }
    if (((uintptr_t)(void *)schema % sizeof(void *)) != 0u) {
        return SC_ERR_ALIGN;
    }
    if (image_size < 68u) {
        return SC_ERR_SIZE;
    }
    if (image_size > SC_IMAGE_LIMIT) {
        return SC_ERR_LIMIT;
    }

    bytes = (const uint8_t *)image;
    if (memcmp(bytes, expected_magic, sizeof(expected_magic)) != 0) {
        return SC_ERR_MAGIC;
    }
    if (sc_read_u16(bytes + 8) != 1u) {
        return SC_ERR_VERSION;
    }
    feature_flags = sc_read_u16(bytes + 10);
    if ((feature_flags & (uint16_t)~(SC_FEATURE_TX | SC_FEATURE_RXQ |
                                    SC_FEATURE_PROTECTION)) != 0u) {
        return SC_ERR_FEATURE;
    }

    total_size = sc_read_u32(bytes + 12);
    if ((size_t)total_size != image_size || total_size < 68u) {
        return SC_ERR_SIZE;
    }
    crc_offset = total_size - 4u;
    message_count = sc_read_u16(bytes + 16);
    signal_count = sc_read_u16(bytes + 18);
    conversion_count = sc_read_u16(bytes + 20);
    if (message_count > SC_MESSAGE_LIMIT || signal_count > SC_SIGNAL_LIMIT ||
        conversion_count == 0u || conversion_count > SC_CONVERSION_LIMIT) {
        return conversion_count == 0u ? SC_ERR_TABLE : SC_ERR_LIMIT;
    }

    if ((feature_flags & (SC_FEATURE_RXQ | SC_FEATURE_PROTECTION)) != 0u) {
        pool_slot_count = sc_read_u16(bytes + 22);
        extension_offset = sc_read_u32(bytes + 24);
        extension_size = sc_read_u32(bytes + 28);
        if (pool_slot_count == 0u || pool_slot_count > SC_SIGNAL_LIMIT) {
            return SC_ERR_TABLE;
        }
        if ((extension_offset & 3u) != 0u) {
            return SC_ERR_ALIGN;
        }
        if (extension_offset < 64u || extension_offset > crc_offset ||
            extension_size != crc_offset - extension_offset) {
            return SC_ERR_BOUNDS;
        }
        directory_end = extension_offset;
    } else if ((feature_flags & SC_FEATURE_TX) == 0u) {
        if (sc_read_u16(bytes + 22) != 0u ||
            !sc_bytes_are_zero(bytes, 24u, 32u)) {
            return SC_ERR_TABLE;
        }
        pool_slot_count = signal_count;
        directory_end = crc_offset;
    } else {
        pool_slot_count = sc_read_u16(bytes + 22);
        tx_offset = sc_read_u32(bytes + 24);
        tx_size = sc_read_u32(bytes + 28);
        if (pool_slot_count == 0u || pool_slot_count > SC_SIGNAL_LIMIT) {
            return SC_ERR_TABLE;
        }
        if ((tx_offset & 3u) != 0u) {
            return SC_ERR_ALIGN;
        }
        if (tx_offset < 64u || tx_offset > crc_offset ||
            tx_size > crc_offset - tx_offset) {
            return SC_ERR_BOUNDS;
        }
        if (crc_offset - (tx_offset + tx_size) > 3u ||
            !sc_bytes_are_zero(bytes, tx_offset + tx_size, crc_offset)) {
            return SC_ERR_TABLE;
        }
        directory_end = tx_offset;
    }

    for (section = 0u; section < 4u; ++section) {
        const uint8_t *entry = bytes + 32u + (size_t)section * 8u;
        offsets[section] = sc_read_u32(entry);
        sizes[section] = sc_read_u32(entry + 4);
        if (offsets[section] < 64u || offsets[section] > directory_end ||
            sizes[section] > directory_end - offsets[section]) {
            return SC_ERR_BOUNDS;
        }
        if ((offsets[section] & 3u) != 0u) {
            return SC_ERR_ALIGN;
        }
    }
    if (sizes[0] != (uint32_t)message_count * 8u ||
        sizes[1] != (uint32_t)signal_count * 16u ||
        sizes[2] != (uint32_t)conversion_count * 24u) {
        return SC_ERR_TABLE;
    }
    if ((sizes[3] & 3u) != 0u) {
        return SC_ERR_ALIGN;
    }

    previous_end = 64u;
    for (section = 0u; section < 4u; ++section) {
        if (offsets[section] < previous_end) {
            return SC_ERR_BOUNDS;
        }
        if (!sc_bytes_are_zero(bytes, previous_end, offsets[section])) {
            return SC_ERR_TABLE;
        }
        previous_end = offsets[section] + sizes[section];
    }
    if (!sc_bytes_are_zero(bytes, previous_end, directory_end)) {
        return SC_ERR_TABLE;
    }
    if (sc_crc32(bytes, crc_offset) != sc_read_u32(bytes + crc_offset)) {
        return SC_ERR_CRC;
    }

    if ((feature_flags & (SC_FEATURE_RXQ | SC_FEATURE_PROTECTION)) != 0u) {
        const uint8_t *extension = bytes + extension_offset;
        uint16_t extension_flags;
        uint16_t quality_count;
        uint32_t relative_nested;
        uint32_t relative_quality;
        uint32_t relative_tx;
        uint32_t relative_protection;
        uint32_t protection_size;
        uint32_t expected_protection;
        uint16_t expected_flags;

        if (extension_size < SC_EX_HEADER_SIZE ||
            sc_read_u32(extension) != SC_EX_MAGIC || extension[6] != 4u ||
            extension[7] != 0u ||
            !sc_bytes_are_zero(extension, 36u, 40u)) {
            return SC_ERR_TABLE;
        }
        extension_flags = sc_read_u16(extension + 4u);
        nested_count = sc_read_u16(extension + 8u);
        quality_count = sc_read_u16(extension + 10u);
        relative_nested = sc_read_u32(extension + 12u);
        relative_quality = sc_read_u32(extension + 16u);
        relative_tx = sc_read_u32(extension + 20u);
        tx_size = sc_read_u32(extension + 24u);
        relative_protection = sc_read_u32(extension + 28u);
        protection_size = sc_read_u32(extension + 32u);
        expected_flags = (uint16_t)(((feature_flags & SC_FEATURE_RXQ) != 0u ? 2u : 0u) |
            (nested_count != 0u ? 1u : 0u) |
            ((feature_flags & SC_FEATURE_TX) != 0u ? 4u : 0u) |
            ((feature_flags & SC_FEATURE_PROTECTION) != 0u ? 8u : 0u));
        expected_protection = relative_quality + (uint32_t)quality_count * 4u;

        if (extension_flags != expected_flags ||
            (extension_flags & (uint16_t)~15u) != 0u ||
            nested_count > SC_SIGNAL_LIMIT ||
            quality_count != ((feature_flags & SC_FEATURE_RXQ) != 0u ? pool_slot_count : 0u) ||
            relative_nested != SC_EX_HEADER_SIZE ||
            relative_quality != SC_EX_HEADER_SIZE +
                (uint32_t)nested_count * SC_NMX_SIZE ||
            relative_protection != ((feature_flags & SC_FEATURE_PROTECTION) != 0u ? expected_protection : 0u) ||
            relative_tx < expected_protection ||
            protection_size != ((feature_flags & SC_FEATURE_PROTECTION) != 0u ? relative_tx - expected_protection : 0u) ||
            relative_tx != expected_protection + protection_size ||
            relative_tx > extension_size || tx_size != extension_size - relative_tx ||
            (((feature_flags & SC_FEATURE_TX) != 0u) != (tx_size != 0u))) {
            return SC_ERR_TABLE;
        }
        nested_offset = extension_offset + relative_nested;
        quality_offset = extension_offset + relative_quality;
        protection_offset = extension_offset + relative_protection;
        tx_offset = extension_offset + relative_tx;

        for (i = 0u; i < nested_count; ++i) {
            const uint8_t *record = bytes + nested_offset + (size_t)i * 36u;
            uint16_t target = sc_read_u16(record);
            uint8_t depth = record[2];
            uint16_t target_message_start = 0u;
            uint16_t target_message_count = 0u;
            uint8_t predicate;
            uint16_t message_index;

            for (message_index = 0u; message_index < message_count; ++message_index) {
                const uint8_t *message = bytes + offsets[0] +
                    (size_t)message_index * 8u;
                uint16_t start = sc_read_u16(message + 6u);
                uint16_t count = sc_read_u16(message + 4u);
                if (target >= start && target < (uint16_t)(start + count)) {
                    target_message_start = start;
                    target_message_count = count;
                    break;
                }
            }

            if (target >= signal_count || target_message_count == 0u ||
                depth < 2u || depth > 4u ||
                record[3] != 0u ||
                (i != 0u && target <= sc_read_u16(record - 36u))) {
                return SC_ERR_TABLE;
            }
            for (predicate = 0u; predicate < 4u; ++predicate) {
                const uint8_t *item = record + 4u + (size_t)predicate * 8u;
                uint16_t selector_program = sc_read_u16(item);
                uint16_t selector_slot = sc_read_u16(item + 2u);
                uint32_t expected = sc_read_u32(item + 4u);

                if (predicate < depth) {
                    const uint8_t *selector;
                    if (selector_program >= signal_count ||
                        selector_slot >= pool_slot_count ||
                        selector_program == target ||
                        selector_program < target_message_start ||
                        selector_program >= (uint16_t)(target_message_start + target_message_count)) {
                        return SC_ERR_BOUNDS;
                    }
                    selector = bytes + offsets[1] +
                        (size_t)selector_program * 16u;
                    if (sc_read_u16(selector + 8u) != selector_slot ||
                        sc_read_u16(selector + 2u) > 32u ||
                        (selector[4] & 2u) != 0u || selector[5] > 7u ||
                        sc_read_u16(selector + 6u) != 0u) {
                        return SC_ERR_TABLE;
                    }
                    if (predicate == 0u) {
                        const uint8_t *target_program = bytes + offsets[1] +
                            (size_t)target * 16u;
                        if (sc_read_u16(selector + 10u) != UINT16_C(0xFFFF) ||
                            sc_read_u32(selector + 12u) != UINT32_C(0xFFFFFFFF) ||
                            sc_read_u16(target_program + 10u) != selector_slot ||
                            sc_read_u32(target_program + 12u) != expected) {
                            return SC_ERR_TABLE;
                        }
                    } else if (predicate == 1u) {
                        const uint8_t *outer = record + 4u;
                        if (sc_read_u16(selector + 10u) != sc_read_u16(outer + 2u) ||
                            sc_read_u32(selector + 12u) != sc_read_u32(outer + 4u)) {
                            return SC_ERR_TABLE;
                        }
                    } else {
                        uint16_t prior;
                        const uint8_t *selector_record = NULL;
                        for (prior = 0u; prior < i; ++prior) {
                            const uint8_t *candidate = bytes + nested_offset +
                                (size_t)prior * 36u;
                            if (sc_read_u16(candidate) == selector_program) {
                                selector_record = candidate;
                                break;
                            }
                        }
                        if (selector_record == NULL ||
                            selector_record[2] != predicate ||
                            memcmp(selector_record + 4u, record + 4u,
                                   (size_t)predicate * 8u) != 0) {
                            return SC_ERR_TABLE;
                        }
                    }
                } else if (selector_program != UINT16_C(0xFFFF) ||
                           selector_slot != UINT16_C(0xFFFF) ||
                           expected != UINT32_C(0xFFFFFFFF)) {
                    return SC_ERR_TABLE;
                }
            }
        }
        for (i = 0u; i < quality_count; ++i) {
            if (sc_read_u32(bytes + quality_offset + (size_t)i * 4u) >
                UINT32_C(0x7FFFFFFF)) {
                return SC_ERR_TABLE;
            }
        }

        if ((feature_flags & SC_FEATURE_PROTECTION) != 0u) {
            const uint8_t *profile = bytes + protection_offset;
            uint16_t rx_plan_count;
            uint16_t tx_plan_count;
            uint32_t rx_plan_offset;
            uint32_t tx_plan_offset;
            uint32_t counter_relative;
            uint32_t span_relative;
            uint32_t end_relative;
            uint32_t expected_span = 0u;
            uint16_t expected_rx_counter = 0u;
            uint32_t plan_count;
            uint32_t p;

            if (protection_size < SC_PR_HEADER_SIZE ||
                sc_read_u32(profile) != SC_PR_MAGIC ||
                !sc_bytes_are_zero(profile, 32u, 48u)) {
                return SC_ERR_TABLE;
            }
            rx_plan_count = sc_read_u16(profile + 4u);
            tx_plan_count = sc_read_u16(profile + 6u);
            rx_counter_count = sc_read_u16(profile + 8u);
            span_count = sc_read_u16(profile + 10u);
            rx_plan_offset = sc_read_u32(profile + 12u);
            tx_plan_offset = sc_read_u32(profile + 16u);
            counter_relative = sc_read_u32(profile + 20u);
            span_relative = sc_read_u32(profile + 24u);
            end_relative = sc_read_u32(profile + 28u);
            if (rx_plan_count != message_count ||
                rx_counter_count > SC_MESSAGE_LIMIT ||
                span_count > SC_SPAN_LIMIT ||
                rx_plan_offset != SC_PR_HEADER_SIZE ||
                tx_plan_offset != rx_plan_offset +
                    (uint32_t)rx_plan_count * SC_PR_PLAN_SIZE ||
                counter_relative != tx_plan_offset +
                    (uint32_t)tx_plan_count * SC_PR_PLAN_SIZE ||
                span_relative != counter_relative +
                    (uint32_t)rx_counter_count * SC_RX_COUNTER_SIZE ||
                end_relative != span_relative +
                    (uint32_t)span_count * SC_SPAN_SIZE ||
                end_relative != protection_size) {
                return SC_ERR_TABLE;
            }
            rx_counter_offset = protection_offset + counter_relative;
            span_offset = protection_offset + span_relative;
            plan_count = (uint32_t)rx_plan_count + tx_plan_count;
            for (p = 0u; p < plan_count; ++p) {
                const uint8_t *plan = profile + SC_PR_HEADER_SIZE +
                    p * SC_PR_PLAN_SIZE;
                uint8_t plan_flags = plan[0];
                int has_crc = (plan_flags & 1u) != 0u;
                int has_counter = (plan_flags & 2u) != 0u;
                if (plan_flags > 3u || plan[3] > 1u ||
                    (plan[9] != 0u && plan[9] != 2u) ||
                    (plan[9] == 0u && sc_read_u16(plan + 12u) != 0u) ||
                    sc_read_u16(plan + 14u) != 0u) {
                    return SC_ERR_TABLE;
                }
                if (has_crc) {
                    uint8_t expected_width = plan[1] == 1u ? 1u :
                        (plan[1] == 2u ? 2u : 0u);
                    uint16_t first_span = sc_read_u16(plan + 6u);
                    uint8_t count = plan[8];
                    uint8_t s;
                    uint16_t previous_end = 0u;
                    uint16_t crc_byte = (uint16_t)(sc_read_u16(plan + 4u) / 8u);
                    if (expected_width == 0u || plan[2] != expected_width ||
                        sc_read_u16(plan + 4u) == UINT16_C(0xFFFF) ||
                        (sc_read_u16(plan + 4u) & 7u) != 0u || count == 0u ||
                        (uint32_t)sc_read_u16(plan + 4u) +
                                (uint32_t)expected_width * 8u >
                            512u ||
                        count > 2u || first_span != expected_span ||
                        (uint32_t)first_span + count > span_count) {
                        return SC_ERR_TABLE;
                    }
                    for (s = 0u; s < count; ++s) {
                        const uint8_t *span = bytes + span_offset +
                            (size_t)(first_span + s) * SC_SPAN_SIZE;
                        uint16_t begin = span[0];
                        uint16_t end = (uint16_t)(begin + span[1]);
                        if (span[1] == 0u || !sc_bytes_are_zero(span, 2u, 4u) ||
                            (s != 0u && begin < previous_end) || end > 64u ||
                            (begin < (uint16_t)(crc_byte + expected_width) &&
                             end > crc_byte)) {
                            return SC_ERR_TABLE;
                        }
                        previous_end = end;
                    }
                    expected_span += count;
                } else if (plan[1] != 0u || plan[2] != 0u || plan[3] != 0u ||
                           sc_read_u16(plan + 4u) != UINT16_C(0xFFFF) ||
                           sc_read_u16(plan + 6u) != UINT16_C(0xFFFF) ||
                           plan[8] != 0u || plan[9] != 0u ||
                           sc_read_u16(plan + 12u) != 0u) {
                    return SC_ERR_TABLE;
                }
                if (p < rx_plan_count) {
                    if (has_counter) {
                        if (sc_read_u16(plan + 10u) != expected_rx_counter++) {
                            return SC_ERR_TABLE;
                        }
                    } else if (sc_read_u16(plan + 10u) != UINT16_C(0xFFFF)) {
                        return SC_ERR_TABLE;
                    }
                }
            }
            if (expected_span != span_count ||
                expected_rx_counter != rx_counter_count) {
                return SC_ERR_TABLE;
            }
            for (i = 0u; i < rx_counter_count; ++i) {
                const uint8_t *counter = bytes + rx_counter_offset +
                    (size_t)i * SC_RX_COUNTER_SIZE;
                uint16_t length = sc_read_u16(counter + 2u);
                uint32_t modulus = sc_read_u32(counter + 8u);
                uint32_t increment = sc_read_u32(counter + 12u);
                if (length == 0u || length > 32u || counter[4] > 1u ||
                    !sc_bytes_are_zero(counter, 5u, 8u) || increment == 0u ||
                    (uint32_t)sc_read_u16(counter) + length > 512u ||
                    modulus == 1u || (modulus == 0u && length != 32u) ||
                    (modulus != 0u && increment >= modulus) ||
                    (length < 32u && modulus != 0u &&
                     (uint64_t)modulus > (UINT64_C(1) << length))) {
                    return SC_ERR_TABLE;
                }
            }
        }
    }

    expected_program = 0u;
    for (i = 0u; i < message_count; ++i) {
        const uint8_t *entry = bytes + offsets[0] + (size_t)i * 8u;
        uint32_t can_id = sc_read_u32(entry);
        uint16_t count = sc_read_u16(entry + 4);
        uint16_t index = sc_read_u16(entry + 6);
        uint16_t j;

        if (!sc_valid_can_id(can_id) || count == 0u || index != expected_program ||
            (uint32_t)index + count > signal_count) {
            return SC_ERR_TABLE;
        }
        if (i != 0u && can_id <= sc_read_u32(entry - 8)) {
            return SC_ERR_TABLE;
        }
        for (j = 0u; j < count; ++j) {
            const uint8_t *program = bytes + offsets[1] +
                                     (size_t)(index + j) * 16u;
            sc_status_t status = sc_validate_program(program, pool_slot_count,
                                                     conversion_count, 512u);
            if (status != SC_OK) {
                return status;
            }
        }
        if (sc_validate_selector(bytes + offsets[1] + (size_t)index * 16u,
                                 count) != SC_OK) {
            return SC_ERR_TABLE;
        }
        expected_program += count;
    }
    if (expected_program != signal_count) {
        return SC_ERR_TABLE;
    }

    for (i = 0u; i < conversion_count; ++i) {
        const uint8_t *entry = bytes + offsets[2] + (size_t)i * 24u;
        uint8_t kind = entry[0];
        uint64_t factor_bits = sc_read_u64(entry + 8);
        uint64_t offset_bits = sc_read_u64(entry + 16);
        double factor;
        double offset;

        if (kind > 1u || !sc_bytes_are_zero(entry, 1u, 8u)) {
            return SC_ERR_TABLE;
        }
        memcpy(&factor, &factor_bits, sizeof(factor));
        memcpy(&offset, &offset_bits, sizeof(offset));
        if (!isfinite(factor) || !isfinite(offset)) {
            return SC_ERR_TABLE;
        }
        if (kind == 0u) {
            if (factor != 1.0 || offset != 0.0) {
                return SC_ERR_TABLE;
            }
        } else if (factor == 0.0) {
            return SC_ERR_TABLE;
        }
    }
    if (bytes[offsets[2]] != 0u) {
        return SC_ERR_TABLE;
    }
    {
        sc_status_t status = sc_validate_symbols(
            bytes, offsets[3], sizes[3], pool_slot_count, message_count);
        if (status != SC_OK) {
            return status;
        }
    }

    if ((feature_flags & SC_FEATURE_TX) != 0u) {
        uint32_t expected_message_offset;
        uint32_t expected_program_offset;
        uint32_t expected_counter_offset;
        uint32_t expected_template_offset;
        uint32_t template_end;
        uint32_t expected_template;
        uint16_t referenced_counters;

        if (tx_size < SC_TX_HEADER_SIZE ||
            sc_read_u32(bytes + tx_offset) != SC_TX_MAGIC ||
            !sc_bytes_are_zero(bytes, tx_offset + 10u, tx_offset + 12u)) {
            return SC_ERR_TABLE;
        }
        tx_message_count = sc_read_u16(bytes + tx_offset + 4u);
        tx_program_count = sc_read_u16(bytes + tx_offset + 6u);
        counter_count = sc_read_u16(bytes + tx_offset + 8u);
        if (tx_message_count == 0u || tx_message_count > SC_MESSAGE_LIMIT ||
            tx_program_count > SC_SIGNAL_LIMIT ||
            counter_count > SC_MESSAGE_LIMIT) {
            return SC_ERR_LIMIT;
        }
        if ((feature_flags & SC_FEATURE_PROTECTION) != 0u &&
            sc_read_u16(bytes + protection_offset + 6u) != tx_message_count) {
            return SC_ERR_TABLE;
        }

        tx_message_offset = sc_read_u32(bytes + tx_offset + 12u);
        tx_program_offset = sc_read_u32(bytes + tx_offset + 16u);
        tx_counter_offset = sc_read_u32(bytes + tx_offset + 20u);
        tx_template_offset = sc_read_u32(bytes + tx_offset + 24u);
        tx_template_size = sc_read_u32(bytes + tx_offset + 28u);
        expected_message_offset = SC_TX_HEADER_SIZE;
        expected_program_offset = expected_message_offset +
                                  (uint32_t)tx_message_count * SC_TX_MESSAGE_SIZE;
        expected_counter_offset = expected_program_offset +
                                  (uint32_t)tx_program_count * SC_PROGRAM_SIZE;
        expected_template_offset = expected_counter_offset +
                                  (uint32_t)counter_count * SC_COUNTER_SIZE;
        if (tx_message_offset != expected_message_offset ||
            tx_program_offset != expected_program_offset ||
            tx_counter_offset != expected_counter_offset ||
            tx_template_offset != expected_template_offset ||
            tx_template_offset > tx_size ||
            tx_template_size > tx_size - tx_template_offset) {
            return SC_ERR_TABLE;
        }
        template_end = tx_template_offset + tx_template_size;
        if (tx_size - template_end > 3u ||
            !sc_bytes_are_zero(bytes, tx_offset + template_end,
                               tx_offset + tx_size)) {
            return SC_ERR_TABLE;
        }

        expected_program = 0u;
        expected_template = tx_template_offset;
        referenced_counters = 0u;
        for (i = 0u; i < tx_message_count; ++i) {
            const uint8_t *message = bytes + tx_offset + tx_message_offset +
                                     (size_t)i * 24u;
            uint32_t logical_id = sc_read_u32(message);
            uint32_t can_id = sc_read_u32(message + 4);
            uint8_t payload_length = message[8];
            uint8_t frame_flags = message[9];
            uint16_t count = sc_read_u16(message + 10);
            uint16_t index = sc_read_u16(message + 12);
            uint16_t counter_index = sc_read_u16(message + 14);
            uint32_t template_offset = sc_read_u32(message + 16);
            uint8_t expected_flags;
            uint16_t j;

            if (!sc_bytes_are_zero(message, 20u, 24u) ||
                !sc_valid_can_id(can_id) ||
                !sc_valid_payload_length(payload_length)) {
                return SC_ERR_TABLE;
            }
            expected_flags = (can_id & UINT32_C(0x80000000)) != 0u
                                 ? SC_FRAME_EXTENDED
                                 : 0u;
            if (payload_length > 8u) {
                expected_flags |= SC_FRAME_FD;
            }
            if (frame_flags != expected_flags || index != expected_program ||
                (uint32_t)index + count > tx_program_count ||
                template_offset != expected_template ||
                template_offset > template_end ||
                payload_length > template_end - template_offset) {
                return SC_ERR_TABLE;
            }
            if (i != 0u && logical_id <= sc_read_u32(message - 24)) {
                return SC_ERR_TABLE;
            }
            if (count == 0u && counter_index == UINT16_C(0xFFFF)) {
                return SC_ERR_TABLE;
            }
            if ((feature_flags & SC_FEATURE_PROTECTION) != 0u) {
                const uint8_t *plan = bytes + protection_offset + SC_PR_HEADER_SIZE +
                    (size_t)message_count * SC_PR_PLAN_SIZE +
                    (size_t)i * SC_PR_PLAN_SIZE;
                int plan_counter = (plan[0] & 2u) != 0u;
                if (plan_counter != (counter_index != UINT16_C(0xFFFF)) ||
                    (plan_counter && sc_read_u16(plan + 10u) != counter_index)) {
                    return SC_ERR_TABLE;
                }
                if ((plan[0] & 1u) != 0u) {
                    uint16_t crc_start = sc_read_u16(plan + 4u);
                    uint16_t first_span = sc_read_u16(plan + 6u);
                    uint8_t span_items = plan[8];
                    uint8_t s;
                    if ((uint32_t)crc_start + (uint32_t)plan[2] * 8u >
                        (uint32_t)payload_length * 8u) {
                        return SC_ERR_BOUNDS;
                    }
                    for (s = 0u; s < span_items; ++s) {
                        const uint8_t *span = bytes + span_offset +
                            (size_t)(first_span + s) * SC_SPAN_SIZE;
                        if ((uint16_t)span[0] + span[1] > payload_length) {
                            return SC_ERR_BOUNDS;
                        }
                    }
                }
            }
            if (counter_index != UINT16_C(0xFFFF)) {
                uint16_t owner_count = 0u;
                uint16_t owner;
                if (counter_index >= counter_count) {
                    return SC_ERR_BOUNDS;
                }
                for (owner = 0u; owner <= i; ++owner) {
                    const uint8_t *prior = bytes + tx_offset +
                        tx_message_offset + (size_t)owner * 24u;
                    if (sc_read_u16(prior + 14) == counter_index) {
                        ++owner_count;
                    }
                }
                if (owner_count != 1u) {
                    return SC_ERR_TABLE;
                }
                ++referenced_counters;
            }
            if (payload_length > required_scratch) {
                required_scratch = payload_length;
            }

            for (j = 0u; j < count; ++j) {
                const uint8_t *program = bytes + tx_offset +
                    tx_program_offset + (size_t)(index + j) * 16u;
                sc_status_t status = sc_validate_program(
                    program, pool_slot_count, conversion_count,
                    (uint32_t)payload_length * 8u);
                if (status != SC_OK) {
                    return status;
                }
            }
            if (sc_validate_selector(bytes + tx_offset + tx_program_offset +
                                     (size_t)index * 16u, count) != SC_OK) {
                return SC_ERR_TABLE;
            }
            for (j = 0u; j < count; ++j) {
                const uint8_t *left = bytes + tx_offset + tx_program_offset +
                    (size_t)(index + j) * 16u;
                uint16_t k;
                for (k = (uint16_t)(j + 1u); k < count; ++k) {
                    const uint8_t *right = bytes + tx_offset +
                        tx_program_offset + (size_t)(index + k) * 16u;
                    int legal_branch_overlap =
                        sc_read_u16(left + 10) != UINT16_C(0xFFFF) &&
                        sc_read_u16(left + 10) == sc_read_u16(right + 10) &&
                        sc_read_u32(left + 12) != sc_read_u32(right + 12);
                    if (sc_ranges_overlap(sc_read_u16(left),
                                          sc_read_u16(left + 2),
                                          sc_read_u16(right),
                                          sc_read_u16(right + 2)) &&
                        !legal_branch_overlap) {
                        return SC_ERR_TABLE;
                    }
                }
            }
            if (counter_index != UINT16_C(0xFFFF)) {
                const uint8_t *counter = bytes + tx_offset +
                    tx_counter_offset + (size_t)counter_index * 24u;
                for (j = 0u; j < count; ++j) {
                    const uint8_t *program = bytes + tx_offset +
                        tx_program_offset + (size_t)(index + j) * 16u;
                    if (sc_ranges_overlap(sc_read_u16(counter),
                                          sc_read_u16(counter + 2),
                                          sc_read_u16(program),
                                          sc_read_u16(program + 2))) {
                        return SC_ERR_TABLE;
                    }
                }
                if ((uint32_t)sc_read_u16(counter) +
                        sc_read_u16(counter + 2) >
                    (uint32_t)payload_length * 8u) {
                    return SC_ERR_BOUNDS;
                }
            }
            expected_program += count;
            expected_template += payload_length;
        }
        if (expected_program != tx_program_count ||
            expected_template != template_end ||
            referenced_counters != counter_count) {
            return SC_ERR_TABLE;
        }

        for (i = 0u; i < counter_count; ++i) {
            const uint8_t *counter = bytes + tx_offset + tx_counter_offset +
                                     (size_t)i * 24u;
            uint16_t length = sc_read_u16(counter + 2);
            uint32_t modulus = sc_read_u32(counter + 8);
            uint32_t increment = sc_read_u32(counter + 12);
            uint32_t initial = sc_read_u32(counter + 16);
            if (length == 0u || length > 32u || counter[4] > 1u ||
                !sc_bytes_are_zero(counter, 5u, 8u) ||
                !sc_bytes_are_zero(counter, 20u, 24u) || increment == 0u ||
                modulus == 1u || (modulus == 0u && length != 32u) ||
                (modulus != 0u &&
                 (increment >= modulus || initial >= modulus)) ||
                (length < 32u && modulus != 0u &&
                 (uint64_t)modulus > (UINT64_C(1) << length))) {
                return SC_ERR_TABLE;
            }
        }
    }

    memset(&parsed, 0, sizeof(parsed));
    parsed.image = bytes;
    parsed.image_size = image_size;
    parsed.msg_offset = offsets[0];
    parsed.prg_offset = offsets[1];
    parsed.cnv_offset = offsets[2];
    parsed.extension_offset = extension_offset;
    parsed.nested_offset = nested_offset;
    parsed.quality_offset = quality_offset;
    parsed.protection_offset = protection_offset;
    parsed.rx_counter_offset = rx_counter_offset;
    parsed.span_offset = span_offset;
    parsed.tx_offset = tx_offset;
    parsed.tx_message_offset = tx_message_offset;
    parsed.tx_program_offset = tx_program_offset;
    parsed.tx_counter_offset = tx_counter_offset;
    parsed.tx_template_offset = tx_template_offset;
    parsed.message_count = message_count;
    parsed.signal_count = signal_count;
    parsed.pool_slot_count = pool_slot_count;
    parsed.conversion_count = conversion_count;
    parsed.tx_message_count = tx_message_count;
    parsed.tx_program_count = tx_program_count;
    parsed.counter_count = counter_count;
    parsed.rx_counter_count = rx_counter_count;
    parsed.span_count = span_count;
    parsed.nested_count = nested_count;
    parsed.required_scratch = required_scratch;
    parsed.has_rxq = (feature_flags & SC_FEATURE_RXQ) != 0u;
    parsed.has_protection = (feature_flags & SC_FEATURE_PROTECTION) != 0u;
    parsed.tag = SC_SCHEMA_TAG;
    *schema = parsed;
    return SC_OK;
}

uint16_t sc_schema_message_count(const sc_schema_t *schema)
{
    return sc_schema_is_open(schema) ? schema->message_count : 0u;
}

uint16_t sc_schema_signal_count(const sc_schema_t *schema)
{
    return sc_schema_is_open(schema) ? schema->pool_slot_count : 0u;
}

uint16_t sc_schema_tx_message_count(const sc_schema_t *schema)
{
    return sc_schema_is_open(schema) ? schema->tx_message_count : 0u;
}

SC_LOCAL size_t sc_counter_state_end(const sc_schema_t *schema)
{
    return offsetof(sc_runtime_state_t, counters) +
           (size_t)schema->counter_count * sizeof(sc_tx_counter_state_t);
}

SC_LOCAL size_t sc_rx_state_offset(const sc_schema_t *schema)
{
    size_t offset = sc_counter_state_end(schema);
    if (schema->has_rxq != 0u) {
        offset += 8u + (size_t)schema->pool_slot_count * 8u;
    }
    return offset;
}

size_t sc_schema_required_state_bytes(const sc_schema_t *schema)
{
    size_t end;
    if (!sc_schema_is_open(schema)) {
        return 0u;
    }
    end = sc_rx_state_offset(schema);
    if (schema->rx_counter_count != 0u) {
        return end + (size_t)schema->rx_counter_count * 8u;
    }
    if (schema->has_rxq != 0u || schema->counter_count != 0u) {
        return end;
    }
    return 0u;
}

size_t sc_schema_required_scratch_bytes(const sc_schema_t *schema)
{
    return sc_schema_is_open(schema) ? schema->required_scratch : 0u;
}

sc_status_t sc_runtime_state_init(const sc_schema_t *schema,
                                  sc_runtime_state_t *state,
                                  size_t state_size)
{
    size_t required;
    uint16_t i;

    if (schema == NULL) {
        return SC_ERR_NULL;
    }
    if (!sc_schema_is_open(schema)) {
        return SC_ERR_STATE;
    }
    required = sc_schema_required_state_bytes(schema);
    if (required == 0u) {
        return state_size == 0u ? SC_OK : SC_ERR_STATE;
    }
    if (state == NULL) {
        return SC_ERR_NULL;
    }
    if (((uintptr_t)(void *)state % sizeof(void *)) != 0u) {
        return SC_ERR_ALIGN;
    }
    if (state_size < required) {
        return SC_ERR_STATE;
    }

    memset(state, 0, required);
    state->schema = schema;
    state->counter_count = schema->counter_count;
    for (i = 0u; i < schema->counter_count; ++i) {
        const uint8_t *counter = schema->image + schema->tx_offset +
            schema->tx_counter_offset + (size_t)i * 24u;
        state->counters[i].current = sc_read_u32(counter + 16);
        state->counters[i].next_generation = 1u;
    }
    return SC_OK;
}

SC_LOCAL uint64_t sc_extract_bits(const uint8_t *data, uint16_t start_bit,
                                  uint16_t length_bits, uint8_t byte_order)
{
    uint64_t value = 0u;
    uint16_t i;

    if (byte_order == 0u) {
        for (i = 0u; i < length_bits; ++i) {
            uint16_t bit_index = (uint16_t)(start_bit + i);
            uint64_t bit = (uint64_t)((data[bit_index / 8u] >>
                                      (bit_index % 8u)) & 1u);
            value |= bit << i;
        }
    } else {
        for (i = 0u; i < length_bits; ++i) {
            uint16_t bit_index = (uint16_t)(start_bit + i);
            uint64_t bit = (uint64_t)((data[bit_index / 8u] >>
                                      (bit_index % 8u)) & 1u);
            value = (value << 1) | bit;
        }
    }
    return value;
}

SC_LOCAL uint64_t sc_sign_extend(uint64_t value, uint16_t length_bits)
{
    if (length_bits < 64u) {
        uint64_t sign_bit = UINT64_C(1) << (length_bits - 1u);
        if ((value & sign_bit) != 0u) {
            value |= ~((UINT64_C(1) << length_bits) - 1u);
        }
    }
    return value;
}

SC_LOCAL uint8_t *sc_quality_state(const sc_schema_t *schema,
                                   sc_runtime_state_t *state)
{
    return (uint8_t *)(void *)state + sc_counter_state_end(schema);
}

SC_LOCAL sc_status_t sc_accept_time(const sc_schema_t *schema,
                                    sc_runtime_state_t *state,
                                    uint32_t now_ms)
{
    uint8_t *quality;
    uint32_t previous;
    uint32_t flags;

    if (state == NULL || state->schema != schema ||
        state->counter_count != schema->counter_count) {
        return SC_ERR_STATE;
    }
    quality = sc_quality_state(schema, state);
    memcpy(&previous, quality, sizeof(previous));
    memcpy(&flags, quality + 4u, sizeof(flags));
    if ((flags & 1u) != 0u && now_ms - previous >= UINT32_C(0x80000000)) {
        return SC_ERR_TIME;
    }
    memcpy(quality, &now_ms, sizeof(now_ms));
    flags |= 1u;
    memcpy(quality + 4u, &flags, sizeof(flags));
    return SC_OK;
}

SC_LOCAL const uint8_t *sc_nested_record(const sc_schema_t *schema,
                                         uint16_t target)
{
    uint16_t i;
    for (i = 0u; i < schema->nested_count; ++i) {
        const uint8_t *record = schema->image + schema->nested_offset +
                                (size_t)i * 36u;
        uint16_t candidate = sc_read_u16(record);
        if (candidate == target) {
            return record;
        }
        if (candidate > target) {
            break;
        }
    }
    return NULL;
}

SC_LOCAL int sc_selector_matches(const sc_schema_t *schema,
                                 const sc_frame_t *frame,
                                 uint16_t selector_program,
                                 uint32_t expected)
{
    const uint8_t *selector = schema->image + schema->prg_offset +
                              (size_t)selector_program * 16u;
    uint16_t start = sc_read_u16(selector);
    uint16_t length = sc_read_u16(selector + 2u);
    uint64_t raw;

    if ((uint32_t)start + length > (uint32_t)frame->len * 8u) {
        return 0;
    }
    raw = sc_extract_bits(frame->data, start, length,
                          (uint8_t)(selector[4] & 1u));
    return (uint32_t)(raw & UINT64_C(0xFFFFFFFF)) == expected;
}

SC_LOCAL int sc_program_active(const sc_schema_t *schema,
                               const sc_frame_t *frame,
                               uint16_t message_program_index,
                               uint16_t message_program_count,
                               uint16_t target_index,
                               const uint8_t *program)
{
    const uint8_t *record = sc_nested_record(schema, target_index);
    if (record != NULL) {
        uint8_t depth = record[2];
        uint8_t i;
        for (i = 0u; i < depth; ++i) {
            const uint8_t *predicate = record + 4u + (size_t)i * 8u;
            if (!sc_selector_matches(schema, frame,
                                     sc_read_u16(predicate),
                                     sc_read_u32(predicate + 4u))) {
                return 0;
            }
        }
        return 1;
    }
    if (sc_read_u16(program + 10u) != UINT16_C(0xFFFF)) {
        uint16_t selector_slot = sc_read_u16(program + 10u);
        uint16_t i;
        for (i = 0u; i < message_program_count; ++i) {
            uint16_t candidate_index = (uint16_t)(message_program_index + i);
            const uint8_t *candidate = schema->image + schema->prg_offset +
                (size_t)candidate_index * 16u;
            if (sc_read_u16(candidate + 8u) == selector_slot) {
                return sc_selector_matches(schema, frame, candidate_index,
                                           sc_read_u32(program + 12u));
            }
        }
        return 0;
    }
    return 1;
}

SC_LOCAL uint8_t sc_crc8_step(uint8_t crc, uint8_t value)
{
    unsigned bit;
    crc ^= value;
    for (bit = 0u; bit < 8u; ++bit) {
        crc = (uint8_t)((crc & UINT8_C(0x80)) != 0u
            ? (uint8_t)(crc << 1) ^ UINT8_C(0x1D)
            : (uint8_t)(crc << 1));
    }
    return crc;
}

SC_LOCAL uint16_t sc_crc16_step(uint16_t crc, uint8_t value)
{
    unsigned bit;
    crc ^= (uint16_t)((uint16_t)value << 8);
    for (bit = 0u; bit < 8u; ++bit) {
        crc = (uint16_t)((crc & UINT16_C(0x8000)) != 0u
            ? (uint16_t)(crc << 1) ^ UINT16_C(0x1021)
            : (uint16_t)(crc << 1));
    }
    return crc;
}

SC_LOCAL sc_status_t sc_check_frame_crc(const sc_schema_t *schema,
                                        const uint8_t *plan,
                                        const sc_frame_t *frame)
{
    uint16_t first_span;
    uint8_t span_items;
    uint8_t algorithm;
    uint16_t data_id;
    uint64_t received;
    uint8_t s;
    uint8_t crc8 = UINT8_C(0xFF);
    uint16_t crc16 = UINT16_C(0xFFFF);

    if ((plan[0] & 1u) == 0u) {
        return SC_OK;
    }
    if ((uint32_t)sc_read_u16(plan + 4u) + (uint32_t)plan[2] * 8u >
        (uint32_t)frame->len * 8u) {
        return SC_ERR_FRAME_CRC;
    }
    algorithm = plan[1];
    data_id = sc_read_u16(plan + 12u);
    if (plan[9] == 2u) {
        crc8 = sc_crc8_step(crc8, (uint8_t)(data_id >> 8));
        crc8 = sc_crc8_step(crc8, (uint8_t)data_id);
        crc16 = sc_crc16_step(crc16, (uint8_t)(data_id >> 8));
        crc16 = sc_crc16_step(crc16, (uint8_t)data_id);
    }
    first_span = sc_read_u16(plan + 6u);
    span_items = plan[8];
    for (s = 0u; s < span_items; ++s) {
        const uint8_t *span = schema->image + schema->span_offset +
            (size_t)(first_span + s) * SC_SPAN_SIZE;
        uint16_t end = (uint16_t)span[0] + span[1];
        uint16_t b;
        if (end > frame->len) {
            return SC_ERR_FRAME_CRC;
        }
        for (b = span[0]; b < end; ++b) {
            crc8 = sc_crc8_step(crc8, frame->data[b]);
            crc16 = sc_crc16_step(crc16, frame->data[b]);
        }
    }
    received = sc_extract_bits(frame->data, sc_read_u16(plan + 4u),
                               (uint16_t)plan[2] * 8u, plan[3]);
    if (algorithm == 1u) {
        return (uint8_t)received == (uint8_t)(crc8 ^ UINT8_C(0xFF))
            ? SC_OK : SC_ERR_FRAME_CRC;
    }
    return (uint16_t)received == crc16 ? SC_OK : SC_ERR_FRAME_CRC;
}

SC_LOCAL uint8_t *sc_rx_counter_state(const sc_schema_t *schema,
                                      sc_runtime_state_t *state,
                                      uint16_t index)
{
    return (uint8_t *)(void *)state + sc_rx_state_offset(schema) +
           (size_t)index * 8u;
}

SC_LOCAL sc_status_t sc_decode_impl(const sc_schema_t *schema,
                                    sc_runtime_state_t *state,
                                    uint32_t now_ms, int timestamped,
                                    const sc_frame_t *frame,
                                    sc_slot_t *pool, size_t pool_count)
{
    const uint8_t *message = NULL;
    uint32_t wanted_id;
    uint16_t i;
    uint16_t message_index = 0u;
    uint16_t program_count;
    uint16_t program_index;
    uint16_t rx_counter_index = UINT16_C(0xFFFF);
    uint32_t next_counter = 0u;
    int update_counter = 0;

    if (schema == NULL || frame == NULL || pool == NULL) {
        return SC_ERR_NULL;
    }
    if (!sc_schema_is_open(schema)) {
        return SC_ERR_TABLE;
    }
    if (pool_count < schema->pool_slot_count) {
        return SC_ERR_POOL;
    }
    if (frame->len > 64u) {
        return SC_ERR_BOUNDS;
    }
    if (timestamped != 0 && schema->has_rxq == 0u) {
        return SC_ERR_FEATURE;
    }

    wanted_id = frame->id;
    if ((frame->flags & SC_FRAME_EXTENDED) != 0u) {
        if (wanted_id > UINT32_C(0x1FFFFFFF)) {
            return SC_ERR_BOUNDS;
        }
        wanted_id |= UINT32_C(0x80000000);
    } else if (wanted_id > UINT32_C(0x7FF)) {
        return SC_ERR_BOUNDS;
    }
    for (i = 0u; i < schema->message_count; ++i) {
        const uint8_t *candidate = schema->image + schema->msg_offset +
                                   (size_t)i * 8u;
        uint32_t candidate_id = sc_read_u32(candidate);
        if (candidate_id == wanted_id) {
            message = candidate;
            message_index = i;
            break;
        }
        if (candidate_id > wanted_id) {
            break;
        }
    }
    if (message == NULL) {
        return SC_OK_NO_MATCH;
    }

    if (schema->has_protection != 0u) {
        const uint8_t *plan = schema->image + schema->protection_offset +
            SC_PR_HEADER_SIZE + (size_t)message_index * SC_PR_PLAN_SIZE;
        sc_status_t crc_status = sc_check_frame_crc(schema, plan, frame);
        if (crc_status != SC_OK) {
            return crc_status;
        }
        if ((plan[0] & 2u) != 0u) {
            const uint8_t *counter;
            uint8_t *counter_state;
            uint32_t expected;
            uint32_t initialized;
            uint32_t actual;
            uint32_t modulus;
            uint32_t increment;
            if (state == NULL || state->schema != schema ||
                state->counter_count != schema->counter_count) {
                return SC_ERR_STATE;
            }
            rx_counter_index = sc_read_u16(plan + 10u);
            counter = schema->image + schema->rx_counter_offset +
                (size_t)rx_counter_index * SC_RX_COUNTER_SIZE;
            counter_state = sc_rx_counter_state(schema, state,
                                                rx_counter_index);
            memcpy(&expected, counter_state, sizeof(expected));
            memcpy(&initialized, counter_state + 4u, sizeof(initialized));
            if ((uint32_t)sc_read_u16(counter) + sc_read_u16(counter + 2u) >
                (uint32_t)frame->len * 8u) {
                return SC_ERR_COUNTER;
            }
            actual = (uint32_t)sc_extract_bits(frame->data,
                sc_read_u16(counter), sc_read_u16(counter + 2u), counter[4]);
            if (initialized != 0u && actual != expected) {
                return SC_ERR_COUNTER;
            }
            modulus = sc_read_u32(counter + 8u);
            increment = sc_read_u32(counter + 12u);
            next_counter = modulus == 0u ? actual + increment :
                (uint32_t)(((uint64_t)actual + increment) % modulus);
            update_counter = 1;
        }
    }
    if (timestamped != 0) {
        sc_status_t status = sc_accept_time(schema, state, now_ms);
        if (status != SC_OK) {
            return status;
        }
    }
    if (update_counter != 0) {
        uint8_t *counter_state = sc_rx_counter_state(schema, state,
                                                     rx_counter_index);
        uint32_t initialized = 1u;
        memcpy(counter_state, &next_counter, sizeof(next_counter));
        memcpy(counter_state + 4u, &initialized, sizeof(initialized));
    }

    program_count = sc_read_u16(message + 4);
    program_index = sc_read_u16(message + 6);
    for (i = 0u; i < program_count; ++i) {
        const uint8_t *program = schema->image + schema->prg_offset +
                                 (size_t)(program_index + i) * 16u;
        uint16_t start_bit = sc_read_u16(program);
        uint16_t length_bits = sc_read_u16(program + 2);
        uint8_t order_flags = program[4];
        uint8_t storage = program[5];
        uint16_t conversion_index = sc_read_u16(program + 6);
        uint16_t slot_index = sc_read_u16(program + 8);
        uint64_t value;
        uint32_t old_flags;
        uint32_t changed;

        if ((uint32_t)start_bit + length_bits > (uint32_t)frame->len * 8u) {
            continue;
        }
        if (!sc_program_active(schema, frame, program_index, program_count,
                               (uint16_t)(program_index + i), program)) {
            continue;
        }

        value = sc_extract_bits(frame->data, start_bit, length_bits,
                                (uint8_t)(order_flags & 1u));
        if ((order_flags & 2u) != 0u) {
            value = sc_sign_extend(value, length_bits);
        }

        if (storage >= 8u) {
            const uint8_t *conversion = schema->image + schema->cnv_offset +
                                        (size_t)conversion_index * 24u;
            double numeric = (order_flags & 2u) != 0u
                                 ? (double)(int64_t)value
                                 : (double)value;
            double physical = numeric;
            if (conversion[0] != 0u) {
                uint64_t factor_bits = sc_read_u64(conversion + 8);
                uint64_t offset_bits = sc_read_u64(conversion + 16);
                double factor;
                double offset;
                memcpy(&factor, &factor_bits, sizeof(factor));
                memcpy(&offset, &offset_bits, sizeof(offset));
                physical = numeric * factor + offset;
            }
            if (storage == 8u) {
                float narrowed = (float)physical;
                uint32_t narrowed_bits;
                memcpy(&narrowed_bits, &narrowed, sizeof(narrowed_bits));
                value = narrowed_bits;
            } else {
                memcpy(&value, &physical, sizeof(value));
            }
        }

        old_flags = pool[slot_index].flags;
        changed = ((old_flags & SC_SLOT_VALID) != 0u &&
                   pool[slot_index].raw != value)
                      ? SC_SLOT_CHANGED
                      : 0u;
        pool[slot_index].raw = value;
        pool[slot_index].flags =
            (old_flags & ~(SC_SLOT_VALID | SC_SLOT_UPDATED |
                           SC_SLOT_CHANGED |
                           (timestamped != 0 ? SC_SLOT_STALE : 0u))) |
            SC_SLOT_VALID | SC_SLOT_UPDATED | changed;

        if (timestamped != 0) {
            uint8_t *slot_time = sc_quality_state(schema, state) + 8u +
                                 (size_t)slot_index * 8u;
            uint32_t initialized = 1u;
            memcpy(slot_time, &now_ms, sizeof(now_ms));
            memcpy(slot_time + 4u, &initialized, sizeof(initialized));
        }
    }
    return SC_OK;
}

sc_status_t sc_decode(const sc_schema_t *schema, const sc_frame_t *frame,
                      sc_slot_t *pool, size_t pool_count)
{
    return sc_decode_impl(schema, NULL, 0u, 0, frame, pool, pool_count);
}

sc_status_t sc_decode_state(const sc_schema_t *schema,
                            sc_runtime_state_t *state,
                            const sc_frame_t *frame, sc_slot_t *pool,
                            size_t pool_count)
{
    return sc_decode_impl(schema, state, 0u, 0, frame, pool, pool_count);
}

sc_status_t sc_decode_at(const sc_schema_t *schema,
                         sc_runtime_state_t *state, uint32_t now_ms,
                         const sc_frame_t *frame, sc_slot_t *pool,
                         size_t pool_count)
{
    return sc_decode_impl(schema, state, now_ms, 1, frame, pool, pool_count);
}

sc_status_t sc_rx_counter_resync(const sc_schema_t *schema,
                                 sc_runtime_state_t *state,
                                 uint32_t encoded_can_id, uint8_t flags)
{
    uint32_t wanted;
    uint16_t i;
    if (schema == NULL) {
        return SC_ERR_NULL;
    }
    if (!sc_schema_is_open(schema) || state == NULL ||
        state->schema != schema || state->counter_count != schema->counter_count) {
        return SC_ERR_STATE;
    }
    if ((flags & (uint8_t)~SC_FRAME_EXTENDED) != 0u) {
        return SC_ERR_VALUE;
    }
    wanted = encoded_can_id | ((flags & SC_FRAME_EXTENDED) != 0u
        ? UINT32_C(0x80000000) : 0u);
    for (i = 0u; i < schema->message_count; ++i) {
        const uint8_t *message = schema->image + schema->msg_offset +
            (size_t)i * 8u;
        uint32_t candidate = sc_read_u32(message);
        if (candidate == wanted) {
            const uint8_t *plan;
            uint16_t counter_index;
            uint8_t *counter_state;
            uint32_t zero = 0u;
            if (schema->has_protection == 0u) {
                return SC_OK_NO_MATCH;
            }
            plan = schema->image + schema->protection_offset +
                SC_PR_HEADER_SIZE + (size_t)i * SC_PR_PLAN_SIZE;
            if ((plan[0] & 2u) == 0u) {
                return SC_OK_NO_MATCH;
            }
            counter_index = sc_read_u16(plan + 10u);
            counter_state = sc_rx_counter_state(schema, state, counter_index);
            memcpy(counter_state, &zero, sizeof(zero));
            memcpy(counter_state + 4u, &zero, sizeof(zero));
            return SC_OK;
        }
        if (candidate > wanted) {
            break;
        }
    }
    return SC_OK_NO_MATCH;
}

sc_status_t sc_expire(const sc_schema_t *schema,
                      sc_runtime_state_t *state, uint32_t now_ms,
                      sc_slot_t *pool, size_t pool_count)
{
    uint16_t slot;
    sc_status_t status;

    if (schema == NULL || pool == NULL) {
        return SC_ERR_NULL;
    }
    if (!sc_schema_is_open(schema)) {
        return SC_ERR_TABLE;
    }
    if (schema->has_rxq == 0u) {
        return SC_ERR_FEATURE;
    }
    if (pool_count < schema->pool_slot_count) {
        return SC_ERR_POOL;
    }
    status = sc_accept_time(schema, state, now_ms);
    if (status != SC_OK) {
        return status;
    }

    for (slot = 0u; slot < schema->pool_slot_count; ++slot) {
        uint32_t threshold = sc_read_u32(schema->image +
            schema->quality_offset + (size_t)slot * 4u);
        uint8_t *slot_time = sc_quality_state(schema, state) + 8u +
                             (size_t)slot * 8u;
        uint32_t updated;
        uint32_t flags;
        memcpy(&updated, slot_time, sizeof(updated));
        memcpy(&flags, slot_time + 4u, sizeof(flags));

        if (threshold != 0u && (flags & 1u) != 0u &&
            (pool[slot].flags & SC_SLOT_VALID) != 0u &&
            now_ms - updated >= threshold) {
            pool[slot].flags |= SC_SLOT_STALE;
        }
    }
    return SC_OK;
}

SC_LOCAL void sc_reset_pool_flags(const sc_schema_t *schema,
                                  sc_slot_t *pool)
{
    uint16_t i;

    for (i = 0u; i < schema->pool_slot_count; ++i) {
        pool[i].flags &= ~(SC_SLOT_UPDATED | SC_SLOT_CHANGED | SC_SLOT_STALE);
    }
    for (i = 0u; i < schema->signal_count; ++i) {
        const uint8_t *program = schema->image + schema->prg_offset +
                                 (size_t)i * SC_PROGRAM_SIZE;
        uint16_t slot = sc_read_u16(program + 8u);
        pool[slot].flags &= ~SC_SLOT_VALID;
    }
}

sc_status_t sc_runtime_reset(const sc_schema_t *schema,
                             sc_runtime_state_t *state, size_t state_size,
                             sc_slot_t *pool, size_t pool_count)
{
    size_t required;
    uint16_t i;

    if (schema == NULL || pool == NULL) {
        return SC_ERR_NULL;
    }
    if (!sc_schema_is_open(schema)) {
        return SC_ERR_STATE;
    }
    required = sc_schema_required_state_bytes(schema);
    if (pool_count < schema->pool_slot_count) {
        return SC_ERR_POOL;
    }
    if (required != 0u && state == NULL) {
        return SC_ERR_NULL;
    }
    if (state_size < required) {
        return SC_ERR_STATE;
    }
    if (required != 0u &&
        ((uintptr_t)(void *)state % sizeof(void *)) != 0u) {
        return SC_ERR_ALIGN;
    }

    if (required != 0u) {
        sc_status_t init = sc_runtime_state_init(schema, state, state_size);
        if (init != SC_OK) {
            return init;
        }
    } else if (state_size != 0u) {
        return SC_ERR_STATE;
    }

    (void)i;
    sc_reset_pool_flags(schema, pool);
    return SC_OK;
}

SC_LOCAL void sc_insert_bits(uint8_t *data, uint16_t start_bit,
                             uint16_t length_bits, uint8_t byte_order,
                             uint64_t value)
{
    uint16_t i;
    for (i = 0u; i < length_bits; ++i) {
        uint16_t bit_index = (uint16_t)(start_bit + i);
        uint16_t source = byte_order == 0u ? i :
                          (uint16_t)(length_bits - 1u - i);
        uint8_t mask = (uint8_t)(1u << (bit_index % 8u));
        if (((value >> source) & 1u) != 0u) {
            data[bit_index / 8u] |= mask;
        } else {
            data[bit_index / 8u] &= (uint8_t)~mask;
        }
    }
}

SC_LOCAL sc_status_t sc_insert_frame_crc(const sc_schema_t *schema,
                                         const uint8_t *plan,
                                         uint8_t *payload,
                                         uint8_t payload_length)
{
    uint16_t first_span;
    uint8_t span_items;
    uint16_t data_id = sc_read_u16(plan + 12u);
    uint8_t crc8 = UINT8_C(0xFF);
    uint16_t crc16 = UINT16_C(0xFFFF);
    uint8_t s;
    if ((plan[0] & 1u) == 0u) {
        return SC_OK;
    }
    if (plan[9] == 2u) {
        crc8 = sc_crc8_step(crc8, (uint8_t)(data_id >> 8));
        crc8 = sc_crc8_step(crc8, (uint8_t)data_id);
        crc16 = sc_crc16_step(crc16, (uint8_t)(data_id >> 8));
        crc16 = sc_crc16_step(crc16, (uint8_t)data_id);
    }
    first_span = sc_read_u16(plan + 6u);
    span_items = plan[8];
    for (s = 0u; s < span_items; ++s) {
        const uint8_t *span = schema->image + schema->span_offset +
            (size_t)(first_span + s) * SC_SPAN_SIZE;
        uint16_t end = (uint16_t)span[0] + span[1];
        uint16_t b;
        if (end > payload_length) {
            return SC_ERR_BOUNDS;
        }
        for (b = span[0]; b < end; ++b) {
            crc8 = sc_crc8_step(crc8, payload[b]);
            crc16 = sc_crc16_step(crc16, payload[b]);
        }
    }
    sc_insert_bits(payload, sc_read_u16(plan + 4u),
                   (uint16_t)plan[2] * 8u, plan[3],
                   plan[1] == 1u ? (uint8_t)(crc8 ^ UINT8_C(0xFF)) : crc16);
    return SC_OK;
}

SC_LOCAL sc_status_t sc_encode_program_raw(const sc_schema_t *schema,
                                           const uint8_t *program,
                                           const sc_slot_t *slot,
                                           uint64_t *raw)
{
    uint16_t length = sc_read_u16(program + 2);
    uint8_t order_flags = program[4];
    uint8_t storage = program[5];
    uint16_t conversion_index = sc_read_u16(program + 6);
    int wire_signed = (order_flags & 2u) != 0u;

    if ((slot->flags & SC_SLOT_VALID) == 0u) {
        return SC_ERR_VALUE;
    }

    if (storage <= 7u) {
        if (storage >= 4u) {
            int64_t value = (int64_t)slot->raw;
            if (wire_signed) {
                if (length < 64u) {
                    int64_t minimum = -(INT64_C(1) << (length - 1u));
                    int64_t maximum = (INT64_C(1) << (length - 1u)) - 1;
                    if (value < minimum || value > maximum) {
                        return SC_ERR_VALUE;
                    }
                }
            } else {
                if (value < 0 ||
                    (length < 64u &&
                     (uint64_t)value >= (UINT64_C(1) << length))) {
                    return SC_ERR_VALUE;
                }
            }
            *raw = (uint64_t)value;
        } else {
            uint64_t value = slot->raw;
            if (wire_signed) {
                uint64_t maximum = length == 64u
                    ? (uint64_t)INT64_MAX
                    : (UINT64_C(1) << (length - 1u)) - 1u;
                if (value > maximum) {
                    return SC_ERR_VALUE;
                }
            } else if (length < 64u &&
                       value >= (UINT64_C(1) << length)) {
                return SC_ERR_VALUE;
            }
            *raw = value;
        }
    } else {
        double physical;
        double numeric;
        double rounded;
        const uint8_t *conversion = schema->image + schema->cnv_offset +
                                    (size_t)conversion_index * 24u;

        if (storage == 8u) {
            uint32_t bits;
            float value;
            if ((slot->raw >> 32) != 0u) {
                return SC_ERR_VALUE;
            }
            bits = (uint32_t)slot->raw;
            memcpy(&value, &bits, sizeof(value));
            physical = value;
        } else {
            memcpy(&physical, &slot->raw, sizeof(physical));
        }
        if (!isfinite(physical)) {
            return SC_ERR_VALUE;
        }
        numeric = physical;
        if (conversion[0] != 0u) {
            uint64_t factor_bits = sc_read_u64(conversion + 8);
            uint64_t offset_bits = sc_read_u64(conversion + 16);
            double factor;
            double offset;
            memcpy(&factor, &factor_bits, sizeof(factor));
            memcpy(&offset, &offset_bits, sizeof(offset));
            numeric = (physical - offset) / factor;
        }
        if (!isfinite(numeric)) {
            return SC_ERR_VALUE;
        }
        rounded = numeric >= 0.0 ? floor(numeric + 0.5) :
                                  ceil(numeric - 0.5);
        if (wire_signed) {
            double limit = ldexp(1.0, (int)length - 1);
            if (rounded < -limit || rounded >= limit) {
                return SC_ERR_VALUE;
            }
            *raw = (uint64_t)(int64_t)rounded;
        } else {
            double limit = ldexp(1.0, length);
            if (rounded < 0.0 || rounded >= limit) {
                return SC_ERR_VALUE;
            }
            *raw = (uint64_t)rounded;
        }
    }
    return SC_OK;
}

sc_status_t sc_encode_prepare(const sc_schema_t *schema,
                              sc_runtime_state_t *state,
                              uint32_t logical_message_id,
                              const sc_slot_t *pool, size_t pool_count,
                              sc_frame_t *frame, void *scratch,
                              size_t scratch_size, sc_tx_token_t *token)
{
    const uint8_t *message = NULL;
    const uint8_t *counter = NULL;
    uint16_t counter_index;
    uint16_t program_count;
    uint16_t program_index;
    uint16_t selector_slot = UINT16_C(0xFFFF);
    uint64_t selector_raw = 0u;
    uint32_t counter_value = 0u;
    uint32_t generation = 0u;
    uint8_t payload_length;
    uint8_t *work;
    sc_frame_t prepared;
    uint16_t i;
    uint16_t tx_message_index = 0u;

    if (schema == NULL || pool == NULL || frame == NULL || token == NULL) {
        return SC_ERR_NULL;
    }
    if (!sc_schema_is_open(schema)) {
        return SC_ERR_TABLE;
    }
    for (i = 0u; i < schema->tx_message_count; ++i) {
        const uint8_t *candidate = schema->image + schema->tx_offset +
            schema->tx_message_offset + (size_t)i * 24u;
        uint32_t id = sc_read_u32(candidate);
        if (id == logical_message_id) {
            message = candidate;
            tx_message_index = i;
            break;
        }
        if (id > logical_message_id) {
            break;
        }
    }
    if (message == NULL) {
        return SC_OK_NO_MATCH;
    }
    if (pool_count < schema->pool_slot_count) {
        return SC_ERR_POOL;
    }
    if (scratch == NULL) {
        return SC_ERR_NULL;
    }
    if (scratch_size < schema->required_scratch) {
        return SC_ERR_SCRATCH;
    }

    counter_index = sc_read_u16(message + 14);
    if (counter_index != UINT16_C(0xFFFF)) {
        if (state == NULL || state->schema != schema ||
            state->counter_count != schema->counter_count) {
            return SC_ERR_STATE;
        }
        if (state->counters[counter_index].pending_generation != 0u) {
            return SC_ERR_BUSY;
        }
        counter = schema->image + schema->tx_offset +
            schema->tx_counter_offset + (size_t)counter_index * 24u;
        counter_value = state->counters[counter_index].current;
    }

    payload_length = message[8];
    work = (uint8_t *)scratch;
    memcpy(work, schema->image + schema->tx_offset +
           sc_read_u32(message + 16), payload_length);

    program_count = sc_read_u16(message + 10);
    program_index = sc_read_u16(message + 12);
    if (program_count > 0u) {
        const uint8_t *first = schema->image + schema->tx_offset +
            schema->tx_program_offset + (size_t)program_index * 16u;
        uint16_t j;
        for (j = 0u; j < program_count; ++j) {
            const uint8_t *program = first + (size_t)j * 16u;
            uint16_t candidate = sc_read_u16(program + 10);
            if (candidate != UINT16_C(0xFFFF)) {
                selector_slot = candidate;
                break;
            }
        }

        for (i = 0u; i < program_count; ++i) {
            const uint8_t *program = first + (size_t)i * 16u;
            uint16_t slot_index = sc_read_u16(program + 8);
            uint16_t mux_slot = sc_read_u16(program + 10);
            uint64_t raw;
            sc_status_t status;

            if (mux_slot != UINT16_C(0xFFFF) &&
                (uint32_t)(selector_raw & UINT64_C(0xFFFFFFFF)) !=
                    sc_read_u32(program + 12)) {
                continue;
            }
            status = sc_encode_program_raw(schema, program,
                                           &pool[slot_index], &raw);
            if (status != SC_OK) {
                return status;
            }
            sc_insert_bits(work, sc_read_u16(program),
                           sc_read_u16(program + 2),
                           (uint8_t)(program[4] & 1u), raw);
            if (slot_index == selector_slot &&
                mux_slot == UINT16_C(0xFFFF)) {
                selector_raw = raw;
            }
        }
    }
    if (counter != NULL) {
        sc_insert_bits(work, sc_read_u16(counter), sc_read_u16(counter + 2),
                       counter[4], counter_value);
    }
    if (schema->has_protection != 0u) {
        const uint8_t *plan = schema->image + schema->protection_offset +
            SC_PR_HEADER_SIZE + (size_t)schema->message_count * SC_PR_PLAN_SIZE +
            (size_t)tx_message_index * SC_PR_PLAN_SIZE;
        sc_status_t status = sc_insert_frame_crc(schema, plan, work,
                                                 payload_length);
        if (status != SC_OK) {
            return status;
        }
    }

    memset(&prepared, 0, sizeof(prepared));
    prepared.id = sc_read_u32(message + 4) & UINT32_C(0x7FFFFFFF);
    prepared.flags = message[9];
    prepared.len = payload_length;
    memcpy(prepared.data, work, payload_length);

    if (counter_index != UINT16_C(0xFFFF)) {
        sc_tx_counter_state_t *counter_state =
            &state->counters[counter_index];
        generation = counter_state->next_generation;
        if (generation == 0u) {
            generation = 1u;
        }
        counter_state->pending_generation = generation;
        counter_state->next_generation = generation + 1u;
        if (counter_state->next_generation == 0u) {
            counter_state->next_generation = 1u;
        }
    }

    *frame = prepared;
    token->schema = schema;
    token->state = counter_index == UINT16_C(0xFFFF) ? NULL : state;
    token->counter_index = counter_index;
    token->reserved = 0u;
    token->generation = generation;
    token->counter_value = counter_value;
    return SC_OK;
}

sc_status_t sc_encode_commit(sc_tx_token_t *token, int transmitted)
{
    const sc_schema_t *schema;
    sc_runtime_state_t *state;
    uint16_t index;
    sc_tx_counter_state_t *counter_state;
    const uint8_t *counter;

    if (token == NULL) {
        return SC_ERR_NULL;
    }
    schema = token->schema;
    if (!sc_schema_is_open(schema)) {
        return SC_ERR_TOKEN;
    }
    if (token->counter_index == UINT16_C(0xFFFF)) {
        memset(token, 0, sizeof(*token));
        return SC_OK;
    }

    state = token->state;
    index = token->counter_index;
    if (state == NULL || state->schema != schema ||
        state->counter_count != schema->counter_count ||
        index >= schema->counter_count || token->generation == 0u) {
        return SC_ERR_TOKEN;
    }
    counter_state = &state->counters[index];
    if (counter_state->pending_generation != token->generation ||
        counter_state->current != token->counter_value) {
        return SC_ERR_TOKEN;
    }

    if (transmitted != 0) {
        uint32_t modulus;
        uint32_t increment;
        counter = schema->image + schema->tx_offset +
            schema->tx_counter_offset + (size_t)index * 24u;
        modulus = sc_read_u32(counter + 8);
        increment = sc_read_u32(counter + 12);
        if (modulus == 0u) {
            counter_state->current += increment;
        } else {
            counter_state->current =
                (uint32_t)(((uint64_t)counter_state->current + increment) %
                           modulus);
        }
    }
    counter_state->pending_generation = 0u;
    memset(token, 0, sizeof(*token));
    return SC_OK;
}

#define SC_ACTIVATION_TAG UINT32_C(0x53434131)

typedef char sc_activation_descriptor_host_size[
    sizeof(void *) != 8u || sizeof(sc_activation_descriptor_t) == 128u ? 1 : -1];
typedef char sc_activation_controller_host_size[
    sizeof(void *) != 8u || sizeof(sc_activation_controller_t) == 184u ? 1 : -1];

typedef struct {
    uint32_t state[8];
    uint64_t bit_count;
    uint8_t block[64];
    size_t used;
} sc_sha256_t;

SC_LOCAL uint32_t sc_rotate_right(uint32_t value, unsigned count)
{
    return (value >> count) | (value << (32u - count));
}

SC_LOCAL void sc_sha256_transform(sc_sha256_t *hash,
                                  const uint8_t block[64])
{
    static const uint32_t constants[64] = {
        UINT32_C(0x428A2F98), UINT32_C(0x71374491), UINT32_C(0xB5C0FBCF),
        UINT32_C(0xE9B5DBA5), UINT32_C(0x3956C25B), UINT32_C(0x59F111F1),
        UINT32_C(0x923F82A4), UINT32_C(0xAB1C5ED5), UINT32_C(0xD807AA98),
        UINT32_C(0x12835B01), UINT32_C(0x243185BE), UINT32_C(0x550C7DC3),
        UINT32_C(0x72BE5D74), UINT32_C(0x80DEB1FE), UINT32_C(0x9BDC06A7),
        UINT32_C(0xC19BF174), UINT32_C(0xE49B69C1), UINT32_C(0xEFBE4786),
        UINT32_C(0x0FC19DC6), UINT32_C(0x240CA1CC), UINT32_C(0x2DE92C6F),
        UINT32_C(0x4A7484AA), UINT32_C(0x5CB0A9DC), UINT32_C(0x76F988DA),
        UINT32_C(0x983E5152), UINT32_C(0xA831C66D), UINT32_C(0xB00327C8),
        UINT32_C(0xBF597FC7), UINT32_C(0xC6E00BF3), UINT32_C(0xD5A79147),
        UINT32_C(0x06CA6351), UINT32_C(0x14292967), UINT32_C(0x27B70A85),
        UINT32_C(0x2E1B2138), UINT32_C(0x4D2C6DFC), UINT32_C(0x53380D13),
        UINT32_C(0x650A7354), UINT32_C(0x766A0ABB), UINT32_C(0x81C2C92E),
        UINT32_C(0x92722C85), UINT32_C(0xA2BFE8A1), UINT32_C(0xA81A664B),
        UINT32_C(0xC24B8B70), UINT32_C(0xC76C51A3), UINT32_C(0xD192E819),
        UINT32_C(0xD6990624), UINT32_C(0xF40E3585), UINT32_C(0x106AA070),
        UINT32_C(0x19A4C116), UINT32_C(0x1E376C08), UINT32_C(0x2748774C),
        UINT32_C(0x34B0BCB5), UINT32_C(0x391C0CB3), UINT32_C(0x4ED8AA4A),
        UINT32_C(0x5B9CCA4F), UINT32_C(0x682E6FF3), UINT32_C(0x748F82EE),
        UINT32_C(0x78A5636F), UINT32_C(0x84C87814), UINT32_C(0x8CC70208),
        UINT32_C(0x90BEFFFA), UINT32_C(0xA4506CEB), UINT32_C(0xBEF9A3F7),
        UINT32_C(0xC67178F2)
    };
    uint32_t words[64];
    uint32_t a;
    uint32_t b;
    uint32_t c;
    uint32_t d;
    uint32_t e;
    uint32_t f;
    uint32_t g;
    uint32_t h;
    unsigned i;

    for (i = 0u; i < 16u; ++i) {
        const uint8_t *p = block + 4u * i;
        words[i] = ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) |
                   ((uint32_t)p[2] << 8) | p[3];
    }
    for (i = 16u; i < 64u; ++i) {
        uint32_t s0 = sc_rotate_right(words[i - 15u], 7u) ^
                      sc_rotate_right(words[i - 15u], 18u) ^
                      (words[i - 15u] >> 3);
        uint32_t s1 = sc_rotate_right(words[i - 2u], 17u) ^
                      sc_rotate_right(words[i - 2u], 19u) ^
                      (words[i - 2u] >> 10);
        words[i] = words[i - 16u] + s0 + words[i - 7u] + s1;
    }

    a = hash->state[0]; b = hash->state[1]; c = hash->state[2];
    d = hash->state[3]; e = hash->state[4]; f = hash->state[5];
    g = hash->state[6]; h = hash->state[7];
    for (i = 0u; i < 64u; ++i) {
        uint32_t sum1 = sc_rotate_right(e, 6u) ^ sc_rotate_right(e, 11u) ^
                        sc_rotate_right(e, 25u);
        uint32_t choose = (e & f) ^ ((~e) & g);
        uint32_t temporary1 = h + sum1 + choose + constants[i] + words[i];
        uint32_t sum0 = sc_rotate_right(a, 2u) ^ sc_rotate_right(a, 13u) ^
                        sc_rotate_right(a, 22u);
        uint32_t majority = (a & b) ^ (a & c) ^ (b & c);
        uint32_t temporary2 = sum0 + majority;
        h = g; g = f; f = e; e = d + temporary1;
        d = c; c = b; b = a; a = temporary1 + temporary2;
    }
    hash->state[0] += a; hash->state[1] += b;
    hash->state[2] += c; hash->state[3] += d;
    hash->state[4] += e; hash->state[5] += f;
    hash->state[6] += g; hash->state[7] += h;
}

SC_LOCAL void sc_sha256(const uint8_t *bytes, size_t count,
                        uint8_t digest[32])
{
    sc_sha256_t hash;
    size_t offset = 0u;
    unsigned i;

    memset(&hash, 0, sizeof(hash));
    hash.state[0] = UINT32_C(0x6A09E667);
    hash.state[1] = UINT32_C(0xBB67AE85);
    hash.state[2] = UINT32_C(0x3C6EF372);
    hash.state[3] = UINT32_C(0xA54FF53A);
    hash.state[4] = UINT32_C(0x510E527F);
    hash.state[5] = UINT32_C(0x9B05688C);
    hash.state[6] = UINT32_C(0x1F83D9AB);
    hash.state[7] = UINT32_C(0x5BE0CD19);
    hash.bit_count = (uint64_t)count * UINT64_C(8);
    while (count - offset >= 64u) {
        sc_sha256_transform(&hash, bytes + offset);
        offset += 64u;
    }
    hash.used = count - offset;
    memcpy(hash.block, bytes + offset, hash.used);
    hash.block[hash.used++] = UINT8_C(0x80);
    if (hash.used > 56u) {
        memset(hash.block + hash.used, 0, 64u - hash.used);
        sc_sha256_transform(&hash, hash.block);
        hash.used = 0u;
    }
    memset(hash.block + hash.used, 0, 56u - hash.used);
    for (i = 0u; i < 8u; ++i) {
        hash.block[63u - i] = (uint8_t)(hash.bit_count >> (8u * i));
    }
    sc_sha256_transform(&hash, hash.block);
    for (i = 0u; i < 8u; ++i) {
        digest[i * 4u] = (uint8_t)(hash.state[i] >> 24);
        digest[i * 4u + 1u] = (uint8_t)(hash.state[i] >> 16);
        digest[i * 4u + 2u] = (uint8_t)(hash.state[i] >> 8);
        digest[i * 4u + 3u] = (uint8_t)hash.state[i];
    }
}

SC_LOCAL int sc_memory_is_zero(const void *memory, size_t count)
{
    const uint8_t *bytes = (const uint8_t *)memory;
    size_t i;
    for (i = 0u; i < count; ++i) {
        if (bytes[i] != 0u) {
            return 0;
        }
    }
    return 1;
}

SC_LOCAL int sc_memory_overlaps(const void *left, size_t left_size,
                                const void *right, size_t right_size)
{
    uintptr_t a;
    uintptr_t b;
    if (left == NULL || right == NULL || left_size == 0u || right_size == 0u) {
        return 0;
    }
    a = (uintptr_t)left;
    b = (uintptr_t)right;
    return a <= b ? b - a < left_size : a - b < right_size;
}

SC_LOCAL uint32_t sc_required_features(const sc_schema_t *schema)
{
    uint32_t features = 0u;
    uint16_t i;

    if (schema->message_count != 0u) features |= SC_RUNTIME_FEATURE_RX;
    if (schema->tx_message_count != 0u) features |= SC_RUNTIME_FEATURE_TX;
    if (schema->nested_count != 0u) features |= SC_RUNTIME_FEATURE_NESTED_MUX;
    if (schema->has_rxq != 0u) features |= SC_RUNTIME_FEATURE_RX_QUALITY;
    if (schema->rx_counter_count != 0u) features |= SC_RUNTIME_FEATURE_RX_COUNTER;
    if (schema->counter_count != 0u) features |= SC_RUNTIME_FEATURE_TX_COUNTER;

    for (i = 0u; i < schema->message_count; ++i) {
        const uint8_t *message = schema->image + schema->msg_offset +
                                 (size_t)i * 8u;
        if ((sc_read_u32(message) & UINT32_C(0x80000000)) != 0u) {
            features |= SC_RUNTIME_FEATURE_EXTENDED_CAN;
        }
    }
    for (i = 0u; i < schema->signal_count; ++i) {
        const uint8_t *program = schema->image + schema->prg_offset +
                                 (size_t)i * SC_PROGRAM_SIZE;
        if (sc_read_u16(program + 10u) != UINT16_C(0xFFFF)) {
            features |= SC_RUNTIME_FEATURE_MULTIPLEXING;
        }
        if ((program[4] & 1u) != 0u) features |= SC_RUNTIME_FEATURE_MOTOROLA;
        if ((uint32_t)sc_read_u16(program) + sc_read_u16(program + 2u) > 64u) {
            features |= SC_RUNTIME_FEATURE_CAN_FD;
        }
    }
    for (i = 0u; i < schema->conversion_count; ++i) {
        if (schema->image[schema->cnv_offset + (size_t)i * 24u] != 0u) {
            features |= SC_RUNTIME_FEATURE_AFFINE;
        }
    }
    for (i = 0u; i < schema->tx_message_count; ++i) {
        const uint8_t *message = schema->image + schema->tx_offset +
            schema->tx_message_offset + (size_t)i * SC_TX_MESSAGE_SIZE;
        uint16_t first = sc_read_u16(message + 12u);
        uint16_t count = sc_read_u16(message + 10u);
        uint16_t j;
        if (message[8] > 8u) features |= SC_RUNTIME_FEATURE_CAN_FD;
        if ((sc_read_u32(message + 4u) & UINT32_C(0x80000000)) != 0u) {
            features |= SC_RUNTIME_FEATURE_EXTENDED_CAN;
        }
        for (j = 0u; j < count; ++j) {
            const uint8_t *program = schema->image + schema->tx_offset +
                schema->tx_program_offset + (size_t)(first + j) * SC_PROGRAM_SIZE;
            if (sc_read_u16(program + 10u) != UINT16_C(0xFFFF)) {
                features |= SC_RUNTIME_FEATURE_MULTIPLEXING;
            }
            if ((program[4] & 1u) != 0u) features |= SC_RUNTIME_FEATURE_MOTOROLA;
        }
    }
    if (schema->has_protection != 0u) {
        uint32_t plans = (uint32_t)schema->message_count + schema->tx_message_count;
        uint32_t p;
        for (p = 0u; p < plans; ++p) {
            const uint8_t *plan = schema->image + schema->protection_offset +
                                  SC_PR_HEADER_SIZE + p * SC_PR_PLAN_SIZE;
            if (plan[1] == 1u) features |= SC_RUNTIME_FEATURE_CRC8_SAE_J1850;
            if (plan[1] == 2u) features |= SC_RUNTIME_FEATURE_CRC16_CCITT_FALSE;
            if (plan[9] == 2u) features |= SC_RUNTIME_FEATURE_CRC_DATA_ID;
            if (plan[3] != 0u) features |= SC_RUNTIME_FEATURE_MOTOROLA;
        }
        for (i = 0u; i < schema->rx_counter_count; ++i) {
            if (schema->image[schema->rx_counter_offset + (size_t)i *
                              SC_RX_COUNTER_SIZE + 4u] != 0u) {
                features |= SC_RUNTIME_FEATURE_MOTOROLA;
            }
        }
    }
    for (i = 0u; i < schema->counter_count; ++i) {
        if (schema->image[schema->tx_offset + schema->tx_counter_offset +
                          (size_t)i * SC_COUNTER_SIZE + 4u] != 0u) {
            features |= SC_RUNTIME_FEATURE_MOTOROLA;
        }
    }
    return features;
}

SC_LOCAL uint32_t sc_ilp32_state_bytes(const sc_schema_t *schema)
{
    uint64_t bytes;
    if (schema->counter_count == 0u && schema->has_rxq == 0u &&
        schema->rx_counter_count == 0u) {
        return 0u;
    }
    bytes = 8u + (uint64_t)schema->counter_count * 12u +
            (schema->has_rxq != 0u
                 ? 8u + (uint64_t)schema->pool_slot_count * 8u
                 : 0u) +
            (uint64_t)schema->rx_counter_count * 8u;
    return (uint32_t)bytes;
}

SC_LOCAL sc_status_t sc_validate_storage(const sc_schema_t *parsed,
                                         const sc_activation_storage_t *storage)
{
    size_t required = sc_schema_required_state_bytes(parsed);
    if (storage == NULL || storage->schema == NULL) return SC_ERR_NULL;
    if (storage->schema_capacity < sizeof(sc_schema_t)) return SC_ERR_SIZE;
    if (sc_memory_overlaps(storage->schema, storage->schema_capacity,
                           storage->state, storage->state_capacity)) {
        return SC_ERR_VALUE;
    }
    if (((uintptr_t)(void *)storage->schema % sizeof(void *)) != 0u) {
        return SC_ERR_ALIGN;
    }
    if (required == 0u) {
        return storage->state == NULL && storage->state_capacity == 0u
                   ? SC_OK : SC_ERR_STATE;
    }
    if (storage->state == NULL) return SC_ERR_NULL;
    if (((uintptr_t)(void *)storage->state % sizeof(void *)) != 0u) {
        return SC_ERR_ALIGN;
    }
    return storage->state_capacity >= required ? SC_OK : SC_ERR_STATE;
}

SC_LOCAL sc_status_t sc_preflight_descriptor(
    const sc_activation_descriptor_t *descriptor, uint16_t runtime_abi,
    uint16_t image_major, uint16_t image_minor, uint32_t supported_features,
    const uint8_t pool_hash[32], size_t scratch_capacity,
    size_t pool_count, const sc_activation_storage_t *storage,
    struct sc_schema *parsed)
{
    uint8_t digest[32];
    uint32_t features;
    sc_status_t status;
    unsigned i;

    if (descriptor == NULL || storage == NULL) return SC_ERR_NULL;
    if (descriptor->struct_size != sizeof(*descriptor)) return SC_ERR_SIZE;
    if (descriptor->descriptor_major != SC_ACTIVATION_DESCRIPTOR_VERSION_MAJOR ||
        descriptor->descriptor_minor > SC_ACTIVATION_DESCRIPTOR_VERSION_MINOR) {
        return SC_ERR_VERSION;
    }
    for (i = 0u; i < 4u; ++i) {
        if (descriptor->reserved[i] != 0u) return SC_ERR_VALUE;
    }
    if (descriptor->runtime_abi != runtime_abi ||
        descriptor->runtime_image_major != image_major ||
        descriptor->runtime_image_minor > image_minor) {
        return SC_ERR_VERSION;
    }
    if (descriptor->image == NULL) return SC_ERR_NULL;
    if (descriptor->image_size < 68u || descriptor->image_size > SC_IMAGE_LIMIT ||
        sc_read_u32(descriptor->image + 12u) != descriptor->image_size) {
        return SC_ERR_SIZE;
    }
    sc_sha256(descriptor->image, descriptor->image_size, digest);
    if (memcmp(digest, descriptor->image_sha256, sizeof(digest)) != 0) {
        return SC_ERR_CRC;
    }
    if (memcmp(descriptor->pool_abi_sha256, pool_hash, 32u) != 0) {
        return SC_ERR_POOL;
    }
    status = sc_schema_open(parsed, descriptor->image, descriptor->image_size);
    if (status != SC_OK) return status;
    if (descriptor->runtime_image_major != sc_read_u16(descriptor->image + 8u) ||
        descriptor->runtime_image_minor != 0u) {
        return SC_ERR_VERSION;
    }
    if (descriptor->image_feature_flags != sc_read_u16(descriptor->image + 10u)) {
        return SC_ERR_FEATURE;
    }
    features = sc_required_features(parsed);
    if (descriptor->required_features != features ||
        (features & ~supported_features) != 0u) {
        return SC_ERR_FEATURE;
    }
    if (descriptor->runtime_state_bytes != sc_ilp32_state_bytes(parsed)) {
        return SC_ERR_STATE;
    }
    if (descriptor->runtime_scratch_bytes != parsed->required_scratch) {
        return SC_ERR_SCRATCH;
    }
    if (descriptor->pool_slots != parsed->pool_slot_count) return SC_ERR_POOL;
    if (descriptor->runtime_scratch_bytes > scratch_capacity) return SC_ERR_SCRATCH;
    if (descriptor->pool_slots > pool_count) return SC_ERR_POOL;
    if (pool_count > (size_t)-1 / sizeof(sc_slot_t)) return SC_ERR_POOL;
    return sc_validate_storage(parsed, storage);
}

SC_LOCAL int sc_storage_overlaps(const sc_activation_storage_t *left,
                                 const sc_activation_storage_t *right)
{
    return sc_memory_overlaps(left->schema, left->schema_capacity,
                              right->schema, right->schema_capacity) ||
           sc_memory_overlaps(left->schema, left->schema_capacity,
                              right->state, right->state_capacity) ||
           sc_memory_overlaps(left->state, left->state_capacity,
                              right->schema, right->schema_capacity) ||
           sc_memory_overlaps(left->state, left->state_capacity,
                              right->state, right->state_capacity);
}

SC_LOCAL int sc_controller_is_valid(const sc_activation_controller_t *controller)
{
    return controller != NULL && controller->tag == SC_ACTIVATION_TAG &&
           controller->generation != 0u && controller->pool != NULL &&
           controller->active.descriptor != NULL &&
           sc_schema_is_open(controller->active.storage.schema);
}

sc_status_t sc_activation_init(
    sc_activation_controller_t *controller,
    const sc_activation_target_t *target,
    const sc_activation_descriptor_t *initial,
    const sc_activation_storage_t *active_storage)
{
    struct sc_schema parsed;
    sc_activation_controller_t initialized;
    sc_status_t status;

    if (controller == NULL || target == NULL || initial == NULL ||
        active_storage == NULL) return SC_ERR_NULL;
    if (!sc_memory_is_zero(controller, sizeof(*controller))) return SC_ERR_STATE;
    if (target->struct_size != sizeof(*target)) return SC_ERR_SIZE;
    if (target->reserved != 0u) return SC_ERR_VALUE;
    if (target->runtime_abi != SC_RUNTIME_ABI_ILP32 ||
        target->runtime_image_major != 1u) return SC_ERR_VERSION;
    if (target->pool == NULL) return SC_ERR_NULL;
    status = sc_preflight_descriptor(initial, target->runtime_abi,
        target->runtime_image_major, target->runtime_image_minor,
        target->supported_features, target->pool_abi_sha256,
        target->scratch_capacity, target->pool_count, active_storage, &parsed);
    if (status != SC_OK) return status;
    if (sc_memory_overlaps(controller, sizeof(*controller), target->pool,
                           target->pool_count * sizeof(sc_slot_t)) ||
        sc_memory_overlaps(controller, sizeof(*controller),
                           active_storage->schema, active_storage->schema_capacity) ||
        sc_memory_overlaps(controller, sizeof(*controller),
                           active_storage->state, active_storage->state_capacity) ||
        sc_memory_overlaps(target->pool, target->pool_count * sizeof(sc_slot_t),
                           active_storage->schema, active_storage->schema_capacity) ||
        sc_memory_overlaps(target->pool, target->pool_count * sizeof(sc_slot_t),
                           active_storage->state, active_storage->state_capacity) ||
        sc_memory_overlaps(initial->image, initial->image_size,
                           active_storage->schema, active_storage->schema_capacity) ||
        sc_memory_overlaps(initial->image, initial->image_size,
                           active_storage->state, active_storage->state_capacity) ||
        sc_memory_overlaps(initial->image, initial->image_size, target->pool,
                           target->pool_count * sizeof(sc_slot_t))) {
        return SC_ERR_VALUE;
    }

    memset(&initialized, 0, sizeof(initialized));
    initialized.tag = SC_ACTIVATION_TAG;
    initialized.generation = 1u;
    initialized.next_serial = 1u;
    initialized.pool = target->pool;
    initialized.pool_count = target->pool_count;
    initialized.scratch_capacity = target->scratch_capacity;
    initialized.supported_features = target->supported_features;
    initialized.runtime_abi = target->runtime_abi;
    initialized.runtime_image_major = target->runtime_image_major;
    initialized.runtime_image_minor = target->runtime_image_minor;
    memcpy(initialized.pool_abi_sha256, target->pool_abi_sha256, 32u);
    initialized.active.descriptor = initial;
    initialized.active.storage = *active_storage;

    *active_storage->schema = parsed;
    if (sc_schema_required_state_bytes(&parsed) != 0u) {
        (void)sc_runtime_state_init(active_storage->schema,
                                    active_storage->state,
                                    active_storage->state_capacity);
    }
    sc_reset_pool_flags(active_storage->schema, target->pool);
    *controller = initialized;
    return SC_OK;
}

sc_status_t sc_activation_prepare(
    sc_activation_controller_t *controller,
    const sc_activation_descriptor_t *candidate,
    const sc_activation_storage_t *staging_storage,
    sc_activation_token_t *token)
{
    struct sc_schema parsed;
    uint64_t serial;
    sc_status_t status;

    if (controller == NULL || candidate == NULL || staging_storage == NULL ||
        token == NULL) return SC_ERR_NULL;
    if (!sc_controller_is_valid(controller)) return SC_ERR_STATE;
    if (controller->pending_token != NULL) return SC_ERR_BUSY;
    status = sc_preflight_descriptor(candidate, controller->runtime_abi,
        controller->runtime_image_major, controller->runtime_image_minor,
        controller->supported_features, controller->pool_abi_sha256,
        controller->scratch_capacity, controller->pool_count,
        staging_storage, &parsed);
    if (status != SC_OK) return status;
    if (sc_storage_overlaps(&controller->active.storage, staging_storage) ||
        sc_memory_overlaps(candidate->image, candidate->image_size,
            controller->active.descriptor->image,
            controller->active.descriptor->image_size) ||
        sc_memory_overlaps(controller, sizeof(*controller),
            staging_storage->schema, staging_storage->schema_capacity) ||
        sc_memory_overlaps(controller, sizeof(*controller),
            staging_storage->state, staging_storage->state_capacity) ||
        sc_memory_overlaps(token, sizeof(*token), staging_storage->schema,
            staging_storage->schema_capacity) ||
        sc_memory_overlaps(token, sizeof(*token), staging_storage->state,
            staging_storage->state_capacity) ||
        sc_memory_overlaps(controller->pool,
            controller->pool_count * sizeof(sc_slot_t), staging_storage->schema,
            staging_storage->schema_capacity) ||
        sc_memory_overlaps(controller->pool,
            controller->pool_count * sizeof(sc_slot_t), staging_storage->state,
            staging_storage->state_capacity) ||
        sc_memory_overlaps(candidate->image, candidate->image_size,
            staging_storage->schema, staging_storage->schema_capacity) ||
        sc_memory_overlaps(candidate->image, candidate->image_size,
            staging_storage->state, staging_storage->state_capacity) ||
        sc_memory_overlaps(candidate->image, candidate->image_size,
            controller->pool, controller->pool_count * sizeof(sc_slot_t)) ||
        sc_memory_overlaps(token, sizeof(*token), controller,
            sizeof(*controller)) ||
        sc_memory_overlaps(token, sizeof(*token), controller->pool,
            controller->pool_count * sizeof(sc_slot_t)) ||
        sc_memory_overlaps(token, sizeof(*token),
            controller->active.storage.schema,
            controller->active.storage.schema_capacity) ||
        sc_memory_overlaps(token, sizeof(*token),
            controller->active.storage.state,
            controller->active.storage.state_capacity) ||
        sc_memory_overlaps(token, sizeof(*token), candidate->image,
            candidate->image_size)) {
        return SC_ERR_VALUE;
    }

    *staging_storage->schema = parsed;
    if (sc_schema_required_state_bytes(&parsed) != 0u) {
        (void)sc_runtime_state_init(staging_storage->schema,
                                    staging_storage->state,
                                    staging_storage->state_capacity);
    }
    serial = controller->next_serial;
    if (serial == 0u) serial = 1u;
    controller->next_serial = serial + 1u;
    if (controller->next_serial == 0u) controller->next_serial = 1u;
    controller->pending.descriptor = candidate;
    controller->pending.storage = *staging_storage;
    controller->pending_token = token;
    controller->pending_serial = serial;
    token->controller = controller;
    token->serial = serial;
    token->prepared_generation = controller->generation;
    token->reserved = 0u;
    return SC_OK;
}

SC_LOCAL sc_status_t sc_validate_activation_token(
    sc_activation_controller_t *controller, sc_activation_token_t *token)
{
    if (!sc_controller_is_valid(controller)) return SC_ERR_STATE;
    if (token == NULL || controller->pending_token != token ||
        token->controller != controller || token->serial == 0u ||
        token->serial != controller->pending_serial ||
        token->prepared_generation != controller->generation ||
        token->reserved != 0u || controller->pending.descriptor == NULL ||
        !sc_schema_is_open(controller->pending.storage.schema)) {
        return SC_ERR_TOKEN;
    }
    return SC_OK;
}

sc_status_t sc_activation_abort(sc_activation_controller_t *controller,
                                sc_activation_token_t *token,
                                sc_activation_slot_t *released)
{
    sc_status_t status;
    if (controller == NULL || token == NULL || released == NULL) return SC_ERR_NULL;
    if (sc_memory_overlaps(released, sizeof(*released), controller,
                           sizeof(*controller)) ||
        sc_memory_overlaps(released, sizeof(*released), token,
                           sizeof(*token))) return SC_ERR_VALUE;
    status = sc_validate_activation_token(controller, token);
    if (status != SC_OK) return status;
    *released = controller->pending;
    memset(&controller->pending, 0, sizeof(controller->pending));
    controller->pending_token = NULL;
    controller->pending_serial = 0u;
    memset(token, 0, sizeof(*token));
    return SC_OK;
}

SC_LOCAL int sc_activation_has_pending_tx(
    const sc_activation_controller_t *controller)
{
    const sc_runtime_state_t *state = controller->active.storage.state;
    const sc_schema_t *schema = controller->active.storage.schema;
    uint16_t i;

    if (state == NULL) return 0;
    if (state->schema != schema ||
        state->counter_count != schema->counter_count) {
        return -1;
    }
    for (i = 0u; i < state->counter_count; ++i) {
        if (state->counters[i].pending_generation != 0u) return 1;
    }
    return 0;
}

sc_status_t sc_activation_commit(sc_activation_controller_t *controller,
                                 sc_activation_token_t *token,
                                 sc_activation_slot_t *previous)
{
    sc_activation_slot_t old;
    sc_status_t status;
    if (controller == NULL || token == NULL || previous == NULL) return SC_ERR_NULL;
    if (sc_memory_overlaps(previous, sizeof(*previous), controller,
                           sizeof(*controller)) ||
        sc_memory_overlaps(previous, sizeof(*previous), token,
                           sizeof(*token))) return SC_ERR_VALUE;
    status = sc_validate_activation_token(controller, token);
    if (status != SC_OK) return status;
    if (sc_activation_has_pending_tx(controller) < 0) return SC_ERR_STATE;
    if (sc_activation_has_pending_tx(controller) > 0) return SC_ERR_BUSY;

    old = controller->active;
    sc_reset_pool_flags(controller->pending.storage.schema, controller->pool);
    controller->active = controller->pending;
    ++controller->generation;
    if (controller->generation == 0u) controller->generation = 1u;
    memset(&controller->pending, 0, sizeof(controller->pending));
    controller->pending_token = NULL;
    controller->pending_serial = 0u;
    *previous = old;
    memset(token, 0, sizeof(*token));
    return SC_OK;
}

sc_status_t sc_activation_view(const sc_activation_controller_t *controller,
                               sc_activation_view_t *view)
{
    if (controller == NULL || view == NULL) return SC_ERR_NULL;
    if (sc_memory_overlaps(view, sizeof(*view), controller,
                           sizeof(*controller))) return SC_ERR_VALUE;
    if (!sc_controller_is_valid(controller)) return SC_ERR_STATE;
    view->descriptor = controller->active.descriptor;
    view->schema = controller->active.storage.schema;
    view->state = controller->active.storage.state;
    view->state_capacity = controller->active.storage.state_capacity;
    view->generation = controller->generation;
    view->reserved = 0u;
    return SC_OK;
}
