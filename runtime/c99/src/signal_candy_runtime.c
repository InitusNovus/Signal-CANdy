#include "signal_candy_runtime.h"

#include <string.h>

#define SC_IMAGE_LIMIT ((size_t)1048576u)
#define SC_MESSAGE_LIMIT ((uint16_t)4096u)
#define SC_SIGNAL_LIMIT ((uint16_t)8192u)
#define SC_CONVERSION_LIMIT ((uint16_t)1024u)
#define SC_SCHEMA_TAG UINT32_C(0x53435231)
#define SC_LOCAL static

struct sc_schema {
    const uint8_t *image;
    size_t image_size;
    uint32_t msg_offset;
    uint32_t prg_offset;
    uint32_t cnv_offset;
    uint16_t message_count;
    uint16_t signal_count;
    uint16_t conversion_count;
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
    uint64_t low = (uint64_t)sc_read_u32(p);
    uint64_t high = (uint64_t)sc_read_u32(p + 4);
    return low | (high << 32);
}

SC_LOCAL uint32_t sc_crc32(const uint8_t *bytes, size_t count)
{
    uint32_t crc = UINT32_C(0xFFFFFFFF);
    size_t i;

    for (i = 0u; i < count; ++i) {
        unsigned bit;
        crc ^= (uint32_t)bytes[i];
        for (bit = 0u; bit < 8u; ++bit) {
            uint32_t mask = (uint32_t)(0u - (crc & 1u));
            crc = (crc >> 1) ^ (UINT32_C(0xEDB88320) & mask);
        }
    }
    return crc ^ UINT32_C(0xFFFFFFFF);
}

SC_LOCAL int sc_bytes_are_zero(const uint8_t *bytes, size_t begin, size_t end)
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

size_t sc_schema_size(void)
{
    return sizeof(sc_schema_t);
}

sc_status_t sc_schema_open(sc_schema_t *schema, const void *image,
                           size_t image_size)
{
    const uint8_t expected_magic[8] = {
        0x53u, 0x43u, 0x49u, 0x4Du, 0x47u, 0x30u, 0x31u, 0x00u
    };
    const uint8_t *bytes;
    uint32_t offsets[4];
    uint32_t sizes[4];
    uint32_t total_size;
    uint32_t crc_offset;
    uint16_t message_count;
    uint16_t signal_count;
    uint16_t conversion_count;
    uint32_t previous_end;
    uint32_t previous_program_end;
    unsigned section;
    uint16_t i;
    struct sc_schema parsed;

    if (schema == NULL || image == NULL) {
        return SC_ERR_NULL;
    }
    if (((uintptr_t)(const void *)schema % sizeof(void *)) != 0u) {
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

    total_size = sc_read_u32(bytes + 12);
    if ((size_t)total_size != image_size || total_size < 68u) {
        return SC_ERR_SIZE;
    }
    if ((size_t)total_size > SC_IMAGE_LIMIT) {
        return SC_ERR_LIMIT;
    }
    if (sc_read_u16(bytes + 10) != 0u ||
        sc_read_u16(bytes + 22) != 0u ||
        !sc_bytes_are_zero(bytes, 24u, 32u)) {
        return SC_ERR_TABLE;
    }

    message_count = sc_read_u16(bytes + 16);
    signal_count = sc_read_u16(bytes + 18);
    conversion_count = sc_read_u16(bytes + 20);
    if (message_count > SC_MESSAGE_LIMIT || signal_count > SC_SIGNAL_LIMIT ||
        conversion_count > SC_CONVERSION_LIMIT) {
        return SC_ERR_LIMIT;
    }
    if (conversion_count == 0u) {
        return SC_ERR_TABLE;
    }

    crc_offset = total_size - 4u;
    for (section = 0u; section < 4u; ++section) {
        const uint8_t *entry = bytes + 32u + (size_t)section * 8u;
        offsets[section] = sc_read_u32(entry);
        sizes[section] = sc_read_u32(entry + 4);
        if (offsets[section] < 64u) {
            return SC_ERR_BOUNDS;
        }
        if ((offsets[section] & 3u) != 0u) {
            return SC_ERR_ALIGN;
        }
        if (offsets[section] > crc_offset ||
            sizes[section] > crc_offset - offsets[section]) {
            return SC_ERR_BOUNDS;
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
    if (!sc_bytes_are_zero(bytes, previous_end, crc_offset)) {
        return SC_ERR_TABLE;
    }

    if (sc_crc32(bytes, (size_t)crc_offset) !=
        sc_read_u32(bytes + crc_offset)) {
        return SC_ERR_CRC;
    }

    previous_program_end = 0u;
    for (i = 0u; i < message_count; ++i) {
        const uint8_t *entry = bytes + offsets[0] + (size_t)i * 8u;
        uint32_t can_id = sc_read_u32(entry);
        uint16_t program_count = sc_read_u16(entry + 4);
        uint16_t program_index = sc_read_u16(entry + 6);
        uint32_t program_end = (uint32_t)program_index + program_count;

        if ((can_id & UINT32_C(0x80000000)) != 0u) {
            if ((can_id & UINT32_C(0x60000000)) != 0u) {
                return SC_ERR_TABLE;
            }
        } else if (can_id > UINT32_C(0x7FF)) {
            return SC_ERR_TABLE;
        }
        if (i != 0u) {
            uint32_t prior_id = sc_read_u32(entry - 8);
            if (can_id <= prior_id) {
                return SC_ERR_TABLE;
            }
        }
        if (program_count == 0u) {
            return SC_ERR_TABLE;
        }
        if (program_end > signal_count) {
            return SC_ERR_BOUNDS;
        }
        if ((uint32_t)program_index < previous_program_end) {
            return SC_ERR_TABLE;
        }
        previous_program_end = program_end;
    }

    for (i = 0u; i < signal_count; ++i) {
        const uint8_t *entry = bytes + offsets[1] + (size_t)i * 16u;
        uint16_t start_bit = sc_read_u16(entry);
        uint16_t length_bits = sc_read_u16(entry + 2);
        uint8_t byte_order = entry[4];
        uint8_t is_signed = entry[5];
        uint16_t conversion_index = sc_read_u16(entry + 6);
        uint16_t slot_index = sc_read_u16(entry + 8);
        uint16_t selector_slot = sc_read_u16(entry + 10);
        uint32_t expected_value = sc_read_u32(entry + 12);
        int unconditional = selector_slot == UINT16_C(0xFFFF);

        if (length_bits == 0u || length_bits > 64u ||
            (uint32_t)start_bit + length_bits > 512u) {
            return SC_ERR_BOUNDS;
        }
        if (byte_order > 1u || is_signed > 1u) {
            return SC_ERR_TABLE;
        }
        if (conversion_index >= conversion_count ||
            slot_index >= signal_count) {
            return SC_ERR_BOUNDS;
        }
        if (unconditional !=
            (expected_value == UINT32_C(0xFFFFFFFF))) {
            return SC_ERR_TABLE;
        }
        if (!unconditional &&
            (selector_slot >= signal_count || selector_slot == slot_index)) {
            return SC_ERR_BOUNDS;
        }
    }

    for (i = 0u; i < conversion_count; ++i) {
        const uint8_t *entry = bytes + offsets[2] + (size_t)i * 24u;
        uint8_t kind = entry[0];
        uint64_t factor_bits = sc_read_u64(entry + 8);
        uint64_t offset_bits = sc_read_u64(entry + 16);

        if (!sc_bytes_are_zero(entry, 1u, 8u)) {
            return SC_ERR_TABLE;
        }
        if (kind > 1u) {
            return SC_ERR_TABLE;
        }
        if (kind == 0u) {
            if (factor_bits != UINT64_C(0x3FF0000000000000) ||
                offset_bits != UINT64_C(0)) {
                return SC_ERR_TABLE;
            }
        } else if ((factor_bits & UINT64_C(0x7FFFFFFFFFFFFFFF)) == 0u) {
            return SC_ERR_TABLE;
        }
    }

    if (bytes[offsets[2]] != 0u) {
        return SC_ERR_TABLE;
    }

    parsed.image = bytes;
    parsed.image_size = image_size;
    parsed.msg_offset = offsets[0];
    parsed.prg_offset = offsets[1];
    parsed.cnv_offset = offsets[2];
    parsed.message_count = message_count;
    parsed.signal_count = signal_count;
    parsed.conversion_count = conversion_count;
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
    return sc_schema_is_open(schema) ? schema->signal_count : 0u;
}

size_t sc_schema_required_state_bytes(const sc_schema_t *schema)
{
    (void)schema;
    return 0u;
}

size_t sc_schema_required_scratch_bytes(const sc_schema_t *schema)
{
    (void)schema;
    return 0u;
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
            uint64_t mask = (UINT64_C(1) << length_bits) - 1u;
            value |= ~mask;
        }
    }
    return value;
}

sc_status_t sc_decode(const sc_schema_t *schema, const sc_frame_t *frame,
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
    if (pool_count < schema->signal_count) {
        return SC_ERR_POOL;
    }
    if (frame->len > 64u) {
        return SC_ERR_BOUNDS;
    }

    wanted_id = frame->id;
    if ((frame->flags & 1u) != 0u) {
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
        uint16_t conversion_index = sc_read_u16(program + 6);
        uint16_t slot_index = sc_read_u16(program + 8);
        uint16_t selector_slot = sc_read_u16(program + 10);
        uint32_t expected_value = sc_read_u32(program + 12);
        uint64_t value;
        uint32_t old_flags;
        uint32_t changed;

        if ((uint32_t)start_bit + length_bits > (uint32_t)frame->len * 8u) {
            continue;
        }
        if (selector_slot != UINT16_C(0xFFFF) &&
            (uint32_t)(pool[selector_slot].raw & UINT64_C(0xFFFFFFFF)) !=
                expected_value) {
            continue;
        }

        value = sc_extract_bits(frame->data, start_bit, length_bits, program[4]);
        if (program[5] != 0u) {
            value = sc_sign_extend(value, length_bits);
        }

        {
            const uint8_t *conversion = schema->image + schema->cnv_offset +
                                        (size_t)conversion_index * 24u;
            if (conversion[0] != 0u) {
                uint64_t factor_bits = sc_read_u64(conversion + 8);
                uint64_t offset_bits = sc_read_u64(conversion + 16);
                double factor;
                double offset;
                double numeric;
                double physical;

                memcpy(&factor, &factor_bits, sizeof(factor));
                memcpy(&offset, &offset_bits, sizeof(offset));
                if (program[5] != 0u) {
                    numeric = (double)(int64_t)value;
                } else {
                    numeric = (double)value;
                }
                physical = numeric * factor + offset;
                memcpy(&value, &physical, sizeof(value));
            }
        }

        old_flags = pool[slot_index].flags;
        changed = ((old_flags & 1u) != 0u && pool[slot_index].raw != value)
                      ? 4u
                      : 0u;
        pool[slot_index].raw = value;
        pool[slot_index].flags = (old_flags & ~UINT32_C(7)) | 3u | changed;
    }

    return SC_OK;
}
