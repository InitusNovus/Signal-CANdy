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
#define SC_EX_MAGIC UINT32_C(0x31305845)
#define SC_EX_HEADER_SIZE UINT32_C(40)
#define SC_NMX_SIZE UINT32_C(36)
#define SC_TX_MAGIC UINT32_C(0x31305854)
#define SC_TX_HEADER_SIZE UINT32_C(32)
#define SC_TX_MESSAGE_SIZE UINT32_C(24)
#define SC_PROGRAM_SIZE UINT32_C(16)
#define SC_COUNTER_SIZE UINT32_C(24)
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
    uint16_t nested_count;
    uint8_t required_scratch;
    uint8_t has_rxq;
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
        cursor += length;
    }
    if (end - cursor > 3u || !sc_bytes_are_zero(bytes, cursor, end)) {
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
    uint32_t extension_offset = 0u;
    uint32_t extension_size = 0u;
    uint32_t nested_offset = 0u;
    uint32_t quality_offset = 0u;
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
    if ((feature_flags & (uint16_t)~(SC_FEATURE_TX | SC_FEATURE_RXQ)) != 0u) {
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

    if ((feature_flags & SC_FEATURE_RXQ) != 0u) {
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

    if ((feature_flags & SC_FEATURE_RXQ) != 0u) {
        const uint8_t *extension = bytes + extension_offset;
        uint16_t extension_flags;
        uint16_t quality_count;
        uint32_t relative_nested;
        uint32_t relative_quality;
        uint32_t relative_tx;
        uint16_t expected_flags;

        if (extension_size < SC_EX_HEADER_SIZE ||
            sc_read_u32(extension) != SC_EX_MAGIC || extension[6] != 4u ||
            extension[7] != 0u ||
            !sc_bytes_are_zero(extension, 28u, 40u)) {
            return SC_ERR_TABLE;
        }
        extension_flags = sc_read_u16(extension + 4u);
        nested_count = sc_read_u16(extension + 8u);
        quality_count = sc_read_u16(extension + 10u);
        relative_nested = sc_read_u32(extension + 12u);
        relative_quality = sc_read_u32(extension + 16u);
        relative_tx = sc_read_u32(extension + 20u);
        tx_size = sc_read_u32(extension + 24u);
        expected_flags = (uint16_t)(2u | (nested_count != 0u ? 1u : 0u) |
            ((feature_flags & SC_FEATURE_TX) != 0u ? 4u : 0u));

        if (extension_flags != expected_flags ||
            (extension_flags & (uint16_t)~7u) != 0u ||
            nested_count > SC_SIGNAL_LIMIT || quality_count != pool_slot_count ||
            relative_nested != SC_EX_HEADER_SIZE ||
            relative_quality != SC_EX_HEADER_SIZE +
                (uint32_t)nested_count * SC_NMX_SIZE ||
            relative_tx != relative_quality + (uint32_t)quality_count * 4u ||
            relative_tx > extension_size || tx_size != extension_size - relative_tx ||
            (((feature_flags & SC_FEATURE_TX) != 0u) != (tx_size != 0u))) {
            return SC_ERR_TABLE;
        }
        nested_offset = extension_offset + relative_nested;
        quality_offset = extension_offset + relative_quality;
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
    parsed.nested_count = nested_count;
    parsed.required_scratch = required_scratch;
    parsed.has_rxq = (feature_flags & SC_FEATURE_RXQ) != 0u;
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

size_t sc_schema_required_state_bytes(const sc_schema_t *schema)
{
    size_t counter_end;
    if (!sc_schema_is_open(schema)) {
        return 0u;
    }
    counter_end = sc_counter_state_end(schema);
    if (schema->has_rxq != 0u) {
        return counter_end + 8u + (size_t)schema->pool_slot_count * 8u;
    }
    return schema->counter_count == 0u ? 0u : counter_end;
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

SC_LOCAL sc_status_t sc_decode_impl(const sc_schema_t *schema,
                                    sc_runtime_state_t *state,
                                    uint32_t now_ms, int timestamped,
                                    const sc_frame_t *frame,
                                    sc_slot_t *pool, size_t pool_count)
{
    const uint8_t *message = NULL;
    uint32_t wanted_id;
    uint16_t i;
    uint16_t program_count;
    uint16_t program_index;

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
    if (timestamped != 0) {
        sc_status_t status;
        if (schema->has_rxq == 0u) {
            return SC_ERR_FEATURE;
        }
        status = sc_accept_time(schema, state, now_ms);
        if (status != SC_OK) {
            return status;
        }
    }

    wanted_id = frame->id;
    if ((frame->flags & SC_FRAME_EXTENDED) != 0u) {
        wanted_id |= UINT32_C(0x80000000);
    }
    for (i = 0u; i < schema->message_count; ++i) {
        const uint8_t *candidate = schema->image + schema->msg_offset +
                                   (size_t)i * 8u;
        uint32_t candidate_id = sc_read_u32(candidate);
        if (candidate_id == wanted_id) {
            message = candidate;
            break;
        }
        if (candidate_id > wanted_id) {
            break;
        }
    }
    if (message == NULL) {
        return SC_OK_NO_MATCH;
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

sc_status_t sc_decode_at(const sc_schema_t *schema,
                         sc_runtime_state_t *state, uint32_t now_ms,
                         const sc_frame_t *frame, sc_slot_t *pool,
                         size_t pool_count)
{
    return sc_decode_impl(schema, state, now_ms, 1, frame, pool, pool_count);
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

    for (i = 0u; i < schema->signal_count; ++i) {
        const uint8_t *program = schema->image + schema->prg_offset +
                                 (size_t)i * 16u;
        uint16_t slot = sc_read_u16(program + 8u);
        pool[slot].flags &= ~(SC_SLOT_VALID | SC_SLOT_UPDATED |
                              SC_SLOT_CHANGED | SC_SLOT_STALE);
    }
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
