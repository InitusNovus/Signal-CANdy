#include "signal_candy_runtime.h"

#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "malformed_image_representatives.h"

#define ACT_IMAGE_SIZE 428u
#define ACT_EXTENSION_OFFSET 144u
#define ACT_PROFILE_OFFSET 56u
#define ACT_TX_OFFSET 160u
#define ACT_POOL_COUNT 4u
#define ACT_STATE_ILP32_BYTES 68u
#define ACT_SCHEMA_A_RX_ID UINT32_C(0x326)
#define ACT_SCHEMA_A_TX_ID UINT32_C(0x325)
#define ACT_SCHEMA_B_RX_ID UINT32_C(0x336)
#define ACT_SCHEMA_B_TX_ID UINT32_C(0x335)
#define ACT_LOGICAL_TX_ID UINT32_C(33)
#define ACT_PRIVATE_FLAG UINT32_C(0x100)

#define PR_HEADER_SIZE 48u
#define TX_HEADER_SIZE 32u
#define TX_MESSAGE_SIZE 24u
#define TX_PROGRAM_SIZE 16u
#define TX_COUNTER_SIZE 24u

typedef union {
    void *pointer_alignment;
    uint64_t integer_alignment;
    double double_alignment;
    unsigned char bytes[1024];
} aligned_storage_t;

typedef struct {
    uint32_t state[8];
    uint64_t bits;
    uint8_t block[64];
    size_t used;
} fixture_sha256_t;

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
    put_u32(p + 4u, (uint32_t)(value >> 32));
}

static uint32_t get_u32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint32_t rotate_right(uint32_t value, unsigned count)
{
    return (value >> count) | (value << (32u - count));
}

static void fixture_sha256_transform(fixture_sha256_t *hash,
                                     const uint8_t block[64])
{
    const uint32_t constants[64] = {
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
                   ((uint32_t)p[2] << 8) | (uint32_t)p[3];
    }
    for (i = 16u; i < 64u; ++i) {
        uint32_t s0 = rotate_right(words[i - 15u], 7u) ^
                      rotate_right(words[i - 15u], 18u) ^
                      (words[i - 15u] >> 3);
        uint32_t s1 = rotate_right(words[i - 2u], 17u) ^
                      rotate_right(words[i - 2u], 19u) ^
                      (words[i - 2u] >> 10);
        words[i] = words[i - 16u] + s0 + words[i - 7u] + s1;
    }

    a = hash->state[0];
    b = hash->state[1];
    c = hash->state[2];
    d = hash->state[3];
    e = hash->state[4];
    f = hash->state[5];
    g = hash->state[6];
    h = hash->state[7];

    for (i = 0u; i < 64u; ++i) {
        uint32_t sum1 = rotate_right(e, 6u) ^ rotate_right(e, 11u) ^
                        rotate_right(e, 25u);
        uint32_t choose = (e & f) ^ ((~e) & g);
        uint32_t temporary1 = h + sum1 + choose + constants[i] + words[i];
        uint32_t sum0 = rotate_right(a, 2u) ^ rotate_right(a, 13u) ^
                        rotate_right(a, 22u);
        uint32_t majority = (a & b) ^ (a & c) ^ (b & c);
        uint32_t temporary2 = sum0 + majority;
        h = g;
        g = f;
        f = e;
        e = d + temporary1;
        d = c;
        c = b;
        b = a;
        a = temporary1 + temporary2;
    }

    hash->state[0] += a;
    hash->state[1] += b;
    hash->state[2] += c;
    hash->state[3] += d;
    hash->state[4] += e;
    hash->state[5] += f;
    hash->state[6] += g;
    hash->state[7] += h;
}

static void fixture_sha256(const uint8_t *bytes, size_t count,
                           uint8_t digest[32])
{
    fixture_sha256_t hash;
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
    hash.bits = (uint64_t)count * UINT64_C(8);

    while (count - offset >= 64u) {
        fixture_sha256_transform(&hash, bytes + offset);
        offset += 64u;
    }
    hash.used = count - offset;
    memcpy(hash.block, bytes + offset, hash.used);
    hash.block[hash.used++] = UINT8_C(0x80);
    if (hash.used > 56u) {
        memset(hash.block + hash.used, 0, 64u - hash.used);
        fixture_sha256_transform(&hash, hash.block);
        hash.used = 0u;
    }
    memset(hash.block + hash.used, 0, 56u - hash.used);
    for (i = 0u; i < 8u; ++i) {
        hash.block[63u - i] = (uint8_t)(hash.bits >> (8u * i));
    }
    fixture_sha256_transform(&hash, hash.block);

    for (i = 0u; i < 8u; ++i) {
        digest[4u * i] = (uint8_t)(hash.state[i] >> 24);
        digest[4u * i + 1u] = (uint8_t)(hash.state[i] >> 16);
        digest[4u * i + 2u] = (uint8_t)(hash.state[i] >> 8);
        digest[4u * i + 3u] = (uint8_t)hash.state[i];
    }
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

static void fix_footer(uint8_t *image)
{
    uint32_t size = get_u32(image + 12u);
    put_u32(image + size - 4u, fixture_crc32(image, size - 4u));
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
                     uint8_t crc_width, uint16_t crc_start,
                     uint16_t span_index, uint16_t counter_index)
{
    entry[0] = flags;
    entry[1] = algorithm;
    entry[2] = crc_width;
    entry[3] = 0u;
    put_u16(entry + 4u, crc_start);
    put_u16(entry + 6u, span_index);
    entry[8] = 1u;
    entry[9] = 0u;
    put_u16(entry + 10u, counter_index);
    put_u16(entry + 12u, 0u);
    put_u16(entry + 14u, 0u);
}

static void build_activation_fixture(uint8_t *image, uint32_t rx_id,
                                     uint32_t tx_id, uint32_t tx_initial)
{
    uint8_t *extension;
    uint8_t *profile;
    uint8_t *tx;
    uint8_t *counter;

    memset(image, 0, ACT_IMAGE_SIZE);
    memcpy(image, "SCIMG01\0", 8u);
    put_u16(image + 8u, 1u);
    put_u16(image + 10u, 7u);
    put_u32(image + 12u, ACT_IMAGE_SIZE);
    put_u16(image + 16u, 1u);
    put_u16(image + 18u, 1u);
    put_u16(image + 20u, 1u);
    put_u16(image + 22u, ACT_POOL_COUNT);
    put_u32(image + 24u, ACT_EXTENSION_OFFSET);
    put_u32(image + 28u, ACT_IMAGE_SIZE - ACT_EXTENSION_OFFSET - 4u);
    put_u32(image + 32u, 64u);
    put_u32(image + 36u, 8u);
    put_u32(image + 40u, 72u);
    put_u32(image + 44u, 16u);
    put_u32(image + 48u, 88u);
    put_u32(image + 52u, 24u);
    put_u32(image + 56u, 112u);
    put_u32(image + 60u, 32u);

    put_u32(image + 64u, rx_id);
    put_u16(image + 68u, 1u);
    put_u16(image + 70u, 0u);
    put_program(image + 72u, 8u, 16u, 0u);
    image[88] = 0u;
    put_u64(image + 96u, UINT64_C(0x3FF0000000000000));
    put_u64(image + 104u, 0u);

    put_u16(image + 112u, ACT_POOL_COUNT);
    put_u16(image + 114u, 1u);
    put_u16(image + 116u, 2u);
    memcpy(image + 118u, "rx", 2u);
    put_u16(image + 120u, 2u);
    memcpy(image + 122u, "tx", 2u);
    put_u16(image + 124u, 4u);
    memcpy(image + 126u, "mark", 4u);
    put_u16(image + 130u, 5u);
    memcpy(image + 132u, "spare", 5u);
    put_u16(image + 137u, 1u);
    image[139] = 'm';

    extension = image + ACT_EXTENSION_OFFSET;
    put_u32(extension, UINT32_C(0x31305845));
    put_u16(extension + 4u, 14u);
    extension[6] = 4u;
    extension[7] = 0u;
    put_u16(extension + 8u, 0u);
    put_u16(extension + 10u, ACT_POOL_COUNT);
    put_u32(extension + 12u, 40u);
    put_u32(extension + 16u, 40u);
    put_u32(extension + 20u, ACT_TX_OFFSET);
    put_u32(extension + 24u, 120u);
    put_u32(extension + 28u, ACT_PROFILE_OFFSET);
    put_u32(extension + 32u, 104u);
    put_u32(extension + 40u, 30u);

    profile = extension + ACT_PROFILE_OFFSET;
    put_u32(profile, UINT32_C(0x31305250));
    put_u16(profile + 4u, 1u);
    put_u16(profile + 6u, 1u);
    put_u16(profile + 8u, 1u);
    put_u16(profile + 10u, 2u);
    put_u32(profile + 12u, PR_HEADER_SIZE);
    put_u32(profile + 16u, 64u);
    put_u32(profile + 20u, 80u);
    put_u32(profile + 24u, 96u);
    put_u32(profile + 28u, 104u);
    put_plan(profile + 48u, 3u, 2u, 2u, 48u, 0u, 0u);
    put_plan(profile + 64u, 3u, 1u, 1u, 56u, 1u, 0u);
    put_u16(profile + 80u, 0u);
    put_u16(profile + 82u, 4u);
    profile[84] = 0u;
    put_u32(profile + 88u, 16u);
    put_u32(profile + 92u, 1u);
    profile[96] = 0u;
    profile[97] = 6u;
    profile[100] = 0u;
    profile[101] = 7u;

    tx = extension + ACT_TX_OFFSET;
    put_u32(tx, UINT32_C(0x31305854));
    put_u16(tx + 4u, 1u);
    put_u16(tx + 6u, 2u);
    put_u16(tx + 8u, 1u);
    put_u32(tx + 12u, TX_HEADER_SIZE);
    put_u32(tx + 16u, TX_HEADER_SIZE + TX_MESSAGE_SIZE);
    put_u32(tx + 20u,
            TX_HEADER_SIZE + TX_MESSAGE_SIZE + 2u * TX_PROGRAM_SIZE);
    put_u32(tx + 24u,
            TX_HEADER_SIZE + TX_MESSAGE_SIZE + 2u * TX_PROGRAM_SIZE +
                TX_COUNTER_SIZE);
    put_u32(tx + 28u, 8u);
    put_u32(tx + 32u, ACT_LOGICAL_TX_ID);
    put_u32(tx + 36u, tx_id);
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
    put_u32(counter + 16u, tx_initial);

    fix_footer(image);
}

static void fill_pool(sc_slot_t *pool)
{
    unsigned i;
    for (i = 0u; i < ACT_POOL_COUNT; ++i) {
        pool[i].raw = UINT64_C(0x1122334455667700) + i;
        pool[i].flags = SC_SLOT_VALID | SC_SLOT_UPDATED | SC_SLOT_CHANGED |
                        SC_SLOT_STALE | (ACT_PRIVATE_FLAG << i);
    }
}

static sc_activation_descriptor_t make_descriptor(const uint8_t *image,
                                                  const uint8_t pool_hash[32])
{
    sc_activation_descriptor_t descriptor;
    memset(&descriptor, 0, sizeof(descriptor));
    descriptor.struct_size = (uint32_t)sizeof(descriptor);
    descriptor.descriptor_major = SC_ACTIVATION_DESCRIPTOR_VERSION_MAJOR;
    descriptor.descriptor_minor = SC_ACTIVATION_DESCRIPTOR_VERSION_MINOR;
    descriptor.image = image;
    descriptor.image_size = ACT_IMAGE_SIZE;
    fixture_sha256(image, ACT_IMAGE_SIZE, descriptor.image_sha256);
    memcpy(descriptor.pool_abi_sha256, pool_hash, 32u);
    descriptor.runtime_image_major = 1u;
    descriptor.runtime_image_minor = 0u;
    descriptor.image_feature_flags = 7u;
    descriptor.runtime_abi = SC_RUNTIME_ABI_ILP32;
    descriptor.required_features = SC_RUNTIME_FEATURE_RX |
                                   SC_RUNTIME_FEATURE_TX |
                                   SC_RUNTIME_FEATURE_RX_QUALITY |
                                   SC_RUNTIME_FEATURE_CRC8_SAE_J1850 |
                                   SC_RUNTIME_FEATURE_CRC16_CCITT_FALSE |
                                   SC_RUNTIME_FEATURE_RX_COUNTER |
                                   SC_RUNTIME_FEATURE_TX_COUNTER;
    descriptor.runtime_state_bytes = ACT_STATE_ILP32_BYTES;
    descriptor.runtime_scratch_bytes = 8u;
    descriptor.pool_slots = ACT_POOL_COUNT;
    return descriptor;
}

static sc_activation_target_t make_target(sc_slot_t *pool,
                                          const uint8_t pool_hash[32])
{
    sc_activation_target_t target;
    memset(&target, 0, sizeof(target));
    target.struct_size = (uint32_t)sizeof(target);
    target.runtime_abi = SC_RUNTIME_ABI_ILP32;
    target.runtime_image_major = 1u;
    target.runtime_image_minor = 0u;
    target.supported_features = UINT32_C(0x00003FFF);
    memcpy(target.pool_abi_sha256, pool_hash, 32u);
    target.scratch_capacity = 8u;
    target.pool = pool;
    target.pool_count = ACT_POOL_COUNT;
    return target;
}

static sc_activation_storage_t make_storage(aligned_storage_t *schema,
                                            aligned_storage_t *state,
                                            size_t state_capacity)
{
    sc_activation_storage_t storage;
    storage.schema = (sc_schema_t *)(void *)schema->bytes;
    storage.schema_capacity = sc_schema_size();
    storage.state = (sc_runtime_state_t *)(void *)state->bytes;
    storage.state_capacity = state_capacity;
    return storage;
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

static int exact_initialized_state(const sc_activation_storage_t *storage,
                                   size_t required, uint32_t initial,
                                   uint8_t tail_fill)
{
    aligned_storage_t expected_storage;
    uint8_t *expected = expected_storage.bytes;
    sc_runtime_state_t *state =
        (sc_runtime_state_t *)(void *)expected_storage.bytes;
    size_t i;

    memset(expected, 0, required);
    state->schema = storage->schema;
    state->counter_count = 1u;
    state->counters[0].current = initial;
    state->counters[0].pending_generation = 0u;
    state->counters[0].next_generation = 1u;

    if (memcmp(storage->state, expected, required) != 0) {
        return 0;
    }
    for (i = required; i < storage->state_capacity; ++i) {
        if (((const uint8_t *)(const void *)storage->state)[i] != tail_fill) {
            return 0;
        }
    }
    return 1;
}

static int init_failure_is_atomic(sc_activation_controller_t *controller,
                                  const sc_activation_target_t *target,
                                  const sc_activation_descriptor_t *descriptor,
                                  const sc_activation_storage_t *storage,
                                  sc_status_t expected)
{
    uint8_t schema_before[1024];
    uint8_t state_before[1024];
    sc_slot_t pool_before[ACT_POOL_COUNT];
    memcpy(schema_before, storage->schema, sizeof(schema_before));
    memcpy(state_before, storage->state, sizeof(state_before));
    memcpy(pool_before, target->pool, sizeof(pool_before));

    return sc_activation_init(controller, target, descriptor, storage) ==
               expected &&
           all_zero(controller, sizeof(*controller)) &&
           memcmp(schema_before, storage->schema, sizeof(schema_before)) == 0 &&
           memcmp(state_before, storage->state, sizeof(state_before)) == 0 &&
           memcmp(pool_before, target->pool, sizeof(pool_before)) == 0;
}

static int prepare_failure_is_atomic(
    sc_activation_controller_t *controller,
    const sc_activation_descriptor_t *descriptor,
    const sc_activation_storage_t *storage, sc_activation_token_t *token,
    sc_slot_t *pool, sc_status_t expected)
{
    sc_activation_controller_t controller_before;
    sc_activation_token_t token_before;
    uint8_t schema_before[1024];
    uint8_t state_before[1024];
    uint8_t active_schema_before[1024];
    uint8_t active_state_before[1024];
    sc_slot_t pool_before[ACT_POOL_COUNT];

    controller_before = *controller;
    token_before = *token;
    memcpy(schema_before, storage->schema, sizeof(schema_before));
    memcpy(state_before, storage->state, sizeof(state_before));
    memcpy(active_schema_before, controller->active.storage.schema,
           sizeof(active_schema_before));
    memcpy(active_state_before, controller->active.storage.state,
           sizeof(active_state_before));
    memcpy(pool_before, pool, sizeof(pool_before));

    return sc_activation_prepare(controller, descriptor, storage, token) ==
               expected &&
           memcmp(&controller_before, controller, sizeof(*controller)) == 0 &&
           memcmp(&token_before, token, sizeof(*token)) == 0 &&
           memcmp(schema_before, storage->schema, sizeof(schema_before)) == 0 &&
           memcmp(state_before, storage->state, sizeof(state_before)) == 0 &&
           memcmp(active_schema_before, controller->active.storage.schema,
                  sizeof(active_schema_before)) == 0 &&
           memcmp(active_state_before, controller->active.storage.state,
                  sizeof(active_state_before)) == 0 &&
           memcmp(pool_before, pool, sizeof(pool_before)) == 0;
}

static void report(unsigned *tests, unsigned *failures,
                   const char *name, int passed)
{
    ++*tests;
    if (passed) {
        printf("PASS: %s\n", name);
    } else {
        ++*failures;
        printf("FAIL: %s\n", name);
    }
}

int main(void)
{
    uint8_t image_a[ACT_IMAGE_SIZE];
    uint8_t image_b[ACT_IMAGE_SIZE];
    uint8_t malformed_image[ACT_IMAGE_SIZE];
    uint8_t pool_hash[32];
    aligned_storage_t schema_a_storage;
    aligned_storage_t schema_b_storage;
    aligned_storage_t state_a_storage;
    aligned_storage_t state_b_storage;
    aligned_storage_t foreign_schema_storage;
    aligned_storage_t foreign_state_storage;
    sc_activation_controller_t controller;
    sc_activation_controller_t foreign_controller;
    sc_activation_descriptor_t descriptor_a;
    sc_activation_descriptor_t descriptor_b;
    sc_activation_descriptor_t bad;
    sc_activation_target_t target;
    sc_activation_target_t foreign_target;
    sc_activation_storage_t storage_a;
    sc_activation_storage_t storage_b;
    sc_activation_storage_t foreign_storage;
    sc_activation_storage_t short_storage;
    sc_activation_slot_t released;
    sc_activation_slot_t previous;
    sc_activation_view_t view;
    sc_activation_token_t token;
    sc_activation_token_t copied;
    sc_slot_t pool[ACT_POOL_COUNT];
    sc_slot_t foreign_pool[ACT_POOL_COUNT];
    sc_slot_t pool_before[ACT_POOL_COUNT];
    uint64_t raw_before[ACT_POOL_COUNT];
    sc_frame_t frame;
    sc_tx_token_t tx_token;
    uint8_t scratch[8];
    size_t native_state_bytes;
    unsigned tests = 0u;
    unsigned failures = 0u;
    unsigned i;
    int passed;

    for (i = 0u; i < 32u; ++i) {
        pool_hash[i] = (uint8_t)i;
    }
    build_activation_fixture(image_a, ACT_SCHEMA_A_RX_ID,
                             ACT_SCHEMA_A_TX_ID, 0u);
    build_activation_fixture(image_b, ACT_SCHEMA_B_RX_ID,
                             ACT_SCHEMA_B_TX_ID, 9u);
    descriptor_a = make_descriptor(image_a, pool_hash);
    descriptor_b = make_descriptor(image_b, pool_hash);

    report(&tests, &failures, "public activation constants are exact",
           SC_ACTIVATION_DESCRIPTOR_VERSION_MAJOR == UINT16_C(1) &&
               SC_ACTIVATION_DESCRIPTOR_VERSION_MINOR == UINT16_C(0) &&
               SC_RUNTIME_ABI_ILP32 == UINT16_C(1) &&
               SC_RUNTIME_FEATURE_RX == UINT32_C(0x00000001) &&
               SC_RUNTIME_FEATURE_TX == UINT32_C(0x00000002) &&
               SC_RUNTIME_FEATURE_MULTIPLEXING == UINT32_C(0x00000004) &&
               SC_RUNTIME_FEATURE_NESTED_MUX == UINT32_C(0x00000008) &&
               SC_RUNTIME_FEATURE_RX_QUALITY == UINT32_C(0x00000010) &&
               SC_RUNTIME_FEATURE_CAN_FD == UINT32_C(0x00000020) &&
               SC_RUNTIME_FEATURE_EXTENDED_CAN == UINT32_C(0x00000040) &&
               SC_RUNTIME_FEATURE_MOTOROLA == UINT32_C(0x00000080) &&
               SC_RUNTIME_FEATURE_AFFINE == UINT32_C(0x00000100) &&
               SC_RUNTIME_FEATURE_CRC8_SAE_J1850 == UINT32_C(0x00000200) &&
               SC_RUNTIME_FEATURE_CRC16_CCITT_FALSE == UINT32_C(0x00000400) &&
               SC_RUNTIME_FEATURE_CRC_DATA_ID == UINT32_C(0x00000800) &&
               SC_RUNTIME_FEATURE_RX_COUNTER == UINT32_C(0x00001000) &&
               SC_RUNTIME_FEATURE_TX_COUNTER == UINT32_C(0x00002000));

    report(&tests, &failures, "public activation host layouts are exact",
           sizeof(sc_activation_descriptor_t) == 128u &&
               sizeof(sc_activation_target_t) == 72u &&
               sizeof(sc_activation_storage_t) == 32u &&
               sizeof(sc_activation_slot_t) == 40u &&
               sizeof(sc_activation_token_t) == 24u &&
               sizeof(sc_activation_view_t) == 40u &&
               sizeof(sc_activation_controller_t) == 184u &&
               offsetof(sc_activation_token_t, serial) == 8u &&
               offsetof(sc_activation_controller_t, next_serial) == 8u);

    memset(&schema_a_storage, 0xA1, sizeof(schema_a_storage));
    memset(&state_a_storage, 0xA2, sizeof(state_a_storage));
    fill_pool(pool);
    memset(&controller, 0, sizeof(controller));
    target = make_target(pool, pool_hash);
    storage_a = make_storage(&schema_a_storage, &state_a_storage,
                             sizeof(state_a_storage.bytes));

    target.runtime_abi += 1u;
    report(&tests, &failures, "target runtime ABI mismatch is atomic",
           init_failure_is_atomic(&controller, &target, &descriptor_a,
                                  &storage_a, SC_ERR_VERSION));
    target = make_target(pool, pool_hash);
    target.runtime_image_major += 1u;
    report(&tests, &failures, "target image major mismatch is atomic",
           init_failure_is_atomic(&controller, &target, &descriptor_a,
                                  &storage_a, SC_ERR_VERSION));
    target = make_target(pool, pool_hash);
    target.pool_abi_sha256[0] ^= 1u;
    report(&tests, &failures, "target pool hash mismatch is atomic",
           init_failure_is_atomic(&controller, &target, &descriptor_a,
                                  &storage_a, SC_ERR_POOL));
    target = make_target(pool, pool_hash);

    bad = descriptor_a;
    bad.image_sha256[0] ^= 1u;
    report(&tests, &failures, "initial image hash failure is atomic",
           init_failure_is_atomic(&controller, &target, &bad, &storage_a,
                                  SC_ERR_CRC));

    short_storage = storage_a;
    short_storage.schema_capacity = sc_schema_size() - 1u;
    report(&tests, &failures, "initial schema capacity failure is atomic",
           init_failure_is_atomic(&controller, &target, &descriptor_a,
                                  &short_storage, SC_ERR_SIZE));

    memset(&schema_a_storage, 0xA1, sizeof(schema_a_storage));
    memset(&state_a_storage, 0xA2, sizeof(state_a_storage));
    fill_pool(pool);
    {
        aligned_storage_t probe_schema;
        sc_schema_t *probe = (sc_schema_t *)(void *)probe_schema.bytes;
        memset(&probe_schema, 0, sizeof(probe_schema));
        passed = sc_schema_open(probe, image_a, sizeof(image_a)) == SC_OK;
        native_state_bytes = sc_schema_required_state_bytes(probe);
    }
    short_storage = storage_a;
    short_storage.state_capacity = native_state_bytes - 1u;
    report(&tests, &failures, "initial state capacity failure is atomic",
           passed && init_failure_is_atomic(&controller, &target,
                                            &descriptor_a, &short_storage,
                                            SC_ERR_STATE));

    target.scratch_capacity = 7u;
    report(&tests, &failures, "initial scratch capacity failure is atomic",
           init_failure_is_atomic(&controller, &target, &descriptor_a,
                                  &storage_a, SC_ERR_SCRATCH));
    target = make_target(pool, pool_hash);
    target.pool_count = ACT_POOL_COUNT - 1u;
    report(&tests, &failures, "initial pool capacity failure is atomic",
           init_failure_is_atomic(&controller, &target, &descriptor_a,
                                  &storage_a, SC_ERR_POOL));
    target = make_target(pool, pool_hash);
    target.pool_count = (size_t)-1 / sizeof(sc_slot_t) + 1u;
    report(&tests, &failures, "initial pool span wrap failure is atomic",
           init_failure_is_atomic(&controller, &target, &descriptor_a,
                                  &storage_a, SC_ERR_POOL));
    target = make_target(pool, pool_hash);
    target.supported_features &= ~SC_RUNTIME_FEATURE_TX_COUNTER;
    report(&tests, &failures, "initial feature capacity failure is atomic",
           init_failure_is_atomic(&controller, &target, &descriptor_a,
                                  &storage_a, SC_ERR_FEATURE));

    target = make_target(pool, pool_hash);
    memset(&schema_a_storage, 0xA1, sizeof(schema_a_storage));
    memset(&state_a_storage, 0xA2, sizeof(state_a_storage));
    fill_pool(pool);
    passed = sc_activation_init(&controller, &target, &descriptor_a,
                                &storage_a) == SC_OK &&
             sc_activation_view(&controller, &view) == SC_OK &&
             view.descriptor == &descriptor_a &&
             view.schema == storage_a.schema && view.state == storage_a.state &&
             view.state_capacity == storage_a.state_capacity &&
             view.generation == 1u && view.reserved == 0u &&
             exact_initialized_state(&storage_a, native_state_bytes, 0u,
                                     UINT8_C(0xA2));
    report(&tests, &failures,
           "initial publication starts at one and resets exact state bytes",
           passed);

    passed = 1;
    for (i = 0u; i < ACT_POOL_COUNT; ++i) {
        uint32_t expected_flags = ACT_PRIVATE_FLAG << i;
        if (i != 0u) {
            expected_flags |= SC_SLOT_VALID;
        }
        passed = passed &&
                 pool[i].raw == UINT64_C(0x1122334455667700) + i &&
                 pool[i].flags == expected_flags;
    }
    report(&tests, &failures,
           "initial pool reset distinguishes RX TX and unreferenced slots",
           passed);

    memset(&foreign_schema_storage, 0, sizeof(foreign_schema_storage));
    memset(&foreign_state_storage, 0, sizeof(foreign_state_storage));
    fill_pool(foreign_pool);
    foreign_target = make_target(foreign_pool, pool_hash);
    foreign_storage = make_storage(&foreign_schema_storage,
                                   &foreign_state_storage,
                                   sizeof(foreign_state_storage.bytes));
    memset(&foreign_controller, 0, sizeof(foreign_controller));
    report(&tests, &failures, "second caller owned controller initializes",
           sc_activation_init(&foreign_controller, &foreign_target,
                              &descriptor_a, &foreign_storage) == SC_OK);

    memset(&schema_b_storage, 0xB1, sizeof(schema_b_storage));
    memset(&state_b_storage, 0xB2, sizeof(state_b_storage));
    storage_b = make_storage(&schema_b_storage, &state_b_storage,
                             sizeof(state_b_storage.bytes));
    memset(&token, 0x5A, sizeof(token));

    bad = descriptor_b;
    bad.struct_size -= 1u;
    report(&tests, &failures, "descriptor struct size failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_SIZE));
    bad = descriptor_b;
    bad.descriptor_major += 1u;
    report(&tests, &failures, "descriptor major failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_VERSION));
    bad = descriptor_b;
    bad.descriptor_minor += 1u;
    report(&tests, &failures, "descriptor minor failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_VERSION));
    bad = descriptor_b;
    bad.reserved[2] = 1u;
    report(&tests, &failures, "descriptor reserved failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_VALUE));
    bad = descriptor_b;
    bad.runtime_abi += 1u;
    report(&tests, &failures, "runtime ABI failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_VERSION));
    bad = descriptor_b;
    bad.runtime_image_major += 1u;
    report(&tests, &failures, "runtime image major failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_VERSION));
    bad = descriptor_b;
    bad.runtime_image_minor += 1u;
    report(&tests, &failures, "runtime image minor failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_VERSION));
    bad = descriptor_b;
    bad.image_feature_flags ^= 1u;
    report(&tests, &failures, "image feature claim failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_FEATURE));
    bad = descriptor_b;
    bad.required_features &= ~SC_RUNTIME_FEATURE_RX;
    report(&tests, &failures, "semantic feature claim failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_FEATURE));
    bad = descriptor_b;
    bad.required_features |= UINT32_C(0x80000000);
    report(&tests, &failures, "unsupported semantic feature failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_FEATURE));
    bad = descriptor_b;
    bad.image_sha256[31] ^= 1u;
    report(&tests, &failures, "candidate image hash failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_CRC));
    bad = descriptor_b;
    bad.pool_abi_sha256[0] ^= 1u;
    report(&tests, &failures, "candidate pool ABI failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_POOL));
    bad = descriptor_b;
    bad.image_size -= 1u;
    report(&tests, &failures, "candidate image resource failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_SIZE));
    bad = descriptor_b;
    bad.runtime_state_bytes -= 1u;
    report(&tests, &failures, "candidate state resource failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_STATE));
    bad = descriptor_b;
    bad.runtime_scratch_bytes -= 1u;
    report(&tests, &failures, "candidate scratch resource failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_SCRATCH));
    bad = descriptor_b;
    bad.pool_slots -= 1u;
    report(&tests, &failures, "candidate pool resource failure is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_POOL));

    short_storage = storage_b;
    short_storage.schema_capacity = sc_schema_size() - 1u;
    report(&tests, &failures, "staging schema capacity failure is atomic",
           prepare_failure_is_atomic(&controller, &descriptor_b,
                                     &short_storage, &token, pool,
                                     SC_ERR_SIZE));
    short_storage = storage_b;
    short_storage.state_capacity = native_state_bytes - 1u;
    report(&tests, &failures, "staging state capacity failure is atomic",
           prepare_failure_is_atomic(&controller, &descriptor_b,
                                     &short_storage, &token, pool,
                                     SC_ERR_STATE));
    short_storage = storage_b;
    short_storage.schema = storage_a.schema;
    report(&tests, &failures, "active staging schema alias is atomic",
           prepare_failure_is_atomic(&controller, &descriptor_b,
                                     &short_storage, &token, pool,
                                     SC_ERR_VALUE));
    short_storage = storage_b;
    short_storage.state = storage_a.state;
    report(&tests, &failures, "active staging state alias is atomic",
           prepare_failure_is_atomic(&controller, &descriptor_b,
                                     &short_storage, &token, pool,
                                     SC_ERR_VALUE));
    bad = descriptor_a;
    report(&tests, &failures, "active candidate image alias is atomic",
           prepare_failure_is_atomic(&controller, &bad, &storage_b, &token,
                                     pool, SC_ERR_VALUE));

    memset(&token, 0x5A, sizeof(token));
    memcpy(pool_before, pool, sizeof(pool));
    passed = sc_activation_prepare(&controller, &descriptor_b, &storage_b,
                                   &token) == SC_OK &&
             token.controller == &controller && token.serial != 0u &&
             token.prepared_generation == 1u && token.reserved == 0u &&
             exact_initialized_state(&storage_b, native_state_bytes, 9u,
                                     UINT8_C(0xB2)) &&
             memcmp(pool_before, pool, sizeof(pool_before)) == 0 &&
             sc_activation_view(&controller, &view) == SC_OK &&
             view.descriptor == &descriptor_a && view.schema == storage_a.schema &&
             view.state == storage_a.state && view.generation == 1u;
    report(&tests, &failures,
           "prepare owns exact token and initializes only staging state",
           passed);

    memset(&copied, 0x6B, sizeof(copied));
    report(&tests, &failures, "busy second prepare preserves second token",
           prepare_failure_is_atomic(&controller, &descriptor_a, &storage_a,
                                     &copied, pool, SC_ERR_BUSY));

    memset(&frame, 0, sizeof(frame));
    memset(&tx_token, 0, sizeof(tx_token));
    pool[1].raw = UINT64_C(0x1234);
    pool[2].raw = UINT64_C(0xA5);
    pool[1].flags |= SC_SLOT_VALID;
    pool[2].flags |= SC_SLOT_VALID;
    passed = sc_encode_prepare(view.schema, view.state, ACT_LOGICAL_TX_ID,
                               pool, ACT_POOL_COUNT, &frame, scratch,
                               sizeof(scratch), &tx_token) == SC_OK &&
             frame.id == ACT_SCHEMA_A_TX_ID && frame.data[0] == 0u &&
             sc_encode_commit(&tx_token, 1) == SC_OK;
    report(&tests, &failures, "prepared candidate leaves schema A behavior active",
           passed);

    copied = token;
    {
        sc_activation_controller_t controller_before = controller;
        sc_activation_controller_t foreign_before = foreign_controller;
        sc_activation_token_t token_before = token;
        passed = sc_activation_abort(&controller, &copied, &released) ==
                     SC_ERR_TOKEN &&
                 memcmp(&controller_before, &controller,
                        sizeof(controller)) == 0 &&
                 memcmp(&token_before, &token, sizeof(token)) == 0 &&
                 sc_activation_commit(&foreign_controller, &token,
                                      &previous) == SC_ERR_TOKEN &&
                 memcmp(&foreign_before, &foreign_controller,
                        sizeof(foreign_controller)) == 0 &&
                 memcmp(&token_before, &token, sizeof(token)) == 0;
    }
    report(&tests, &failures,
           "copied address and foreign controller tokens fail atomically",
           passed);

    {
        uint8_t active_before[1024];
        uint8_t staging_before[1024];
        memcpy(pool_before, pool, sizeof(pool));
        memcpy(active_before, storage_a.state, sizeof(active_before));
        memcpy(staging_before, storage_b.state, sizeof(staging_before));
        passed = sc_activation_abort(&controller, &token, &released) == SC_OK &&
                 released.descriptor == &descriptor_b &&
                 released.storage.schema == storage_b.schema &&
                 released.storage.state == storage_b.state &&
                 all_zero(&token, sizeof(token)) &&
                 memcmp(pool_before, pool, sizeof(pool_before)) == 0 &&
                 memcmp(active_before, storage_a.state,
                        sizeof(active_before)) == 0 &&
                 memcmp(staging_before, storage_b.state,
                        sizeof(staging_before)) == 0 &&
                 sc_activation_view(&controller, &view) == SC_OK &&
                 view.descriptor == &descriptor_a && view.generation == 1u;
    }
    report(&tests, &failures,
           "abort returns staging ownership and is an exact no-op", passed);

    controller.next_serial = UINT64_MAX;
    memset(&token, 0, sizeof(token));
    passed = sc_activation_prepare(&controller, &descriptor_b, &storage_b,
                                   &token) == SC_OK &&
             token.serial == UINT64_MAX &&
             sc_activation_abort(&controller, &token, &released) == SC_OK &&
             controller.next_serial == 1u &&
             sc_activation_prepare(&controller, &descriptor_b, &storage_b,
                                   &token) == SC_OK &&
             token.serial == 1u && controller.next_serial == 2u &&
             sc_activation_abort(&controller, &token, &released) == SC_OK;
    report(&tests, &failures, "prepare serial wraps and permanently skips zero",
           passed);

    memset(&token, 0, sizeof(token));
    passed = sc_activation_prepare(&controller, &descriptor_b, &storage_b,
                                   &token) == SC_OK;
    copied = token;
    memset(&frame, 0, sizeof(frame));
    memset(&tx_token, 0, sizeof(tx_token));
    pool[1].raw = UINT64_C(0x1234);
    pool[2].raw = UINT64_C(0xA5);
    pool[1].flags |= SC_SLOT_VALID;
    pool[2].flags |= SC_SLOT_VALID;
    passed = passed &&
             sc_encode_prepare(view.schema, view.state, ACT_LOGICAL_TX_ID,
                               pool, ACT_POOL_COUNT, &frame, scratch,
                               sizeof(scratch), &tx_token) == SC_OK;
    {
        sc_activation_controller_t controller_before = controller;
        sc_activation_token_t token_before = token;
        sc_activation_slot_t previous_before;
        sc_slot_t before_pool[ACT_POOL_COUNT];
        memset(&previous_before, 0xA5, sizeof(previous_before));
        previous = previous_before;
        memcpy(before_pool, pool, sizeof(before_pool));
        passed = passed &&
                 sc_activation_commit(&controller, &token, &previous) ==
                     SC_ERR_BUSY &&
                 memcmp(&controller_before, &controller,
                        sizeof(controller)) == 0 &&
                 memcmp(&token_before, &token, sizeof(token)) == 0 &&
                 memcmp(&previous_before, &previous, sizeof(previous)) == 0 &&
                 memcmp(before_pool, pool, sizeof(before_pool)) == 0 &&
                 sc_activation_view(&controller, &view) == SC_OK &&
                 view.descriptor == &descriptor_a && view.generation == 1u &&
                 sc_encode_commit(&tx_token, 0) == SC_OK;
    }
    report(&tests, &failures,
           "commit rejects outstanding TX reservation without mutation",
           passed);
    {
        sc_activation_controller_t controller_before = controller;
        sc_activation_token_t token_before = token;
        sc_activation_slot_t previous_before;
        uint16_t saved_counter_count = storage_a.state->counter_count;
        memset(&previous_before, 0x5A, sizeof(previous_before));
        previous = previous_before;
        storage_a.state->counter_count = UINT16_MAX;
        passed = sc_activation_commit(&controller, &token, &previous) ==
                     SC_ERR_STATE &&
                 memcmp(&controller_before, &controller,
                        sizeof(controller)) == 0 &&
                 memcmp(&token_before, &token, sizeof(token)) == 0 &&
                 memcmp(&previous_before, &previous, sizeof(previous)) == 0;
        storage_a.state->counter_count = saved_counter_count;
    }
    report(&tests, &failures,
           "commit rejects corrupted active state without scanning or mutation",
           passed);
    for (i = 0u; i < ACT_POOL_COUNT; ++i) {
        pool[i].raw = UINT64_C(0xFFEEDDCCBBAA0000) + i;
        raw_before[i] = pool[i].raw;
        pool[i].flags = SC_SLOT_VALID | SC_SLOT_UPDATED | SC_SLOT_CHANGED |
                        SC_SLOT_STALE | (ACT_PRIVATE_FLAG << i);
    }
    passed = passed &&
             sc_activation_commit(&controller, &token, &previous) == SC_OK &&
             previous.descriptor == &descriptor_a &&
             previous.storage.schema == storage_a.schema &&
             previous.storage.state == storage_a.state &&
             all_zero(&token, sizeof(token)) &&
             sc_activation_view(&controller, &view) == SC_OK &&
             view.descriptor == &descriptor_b && view.schema == storage_b.schema &&
             view.state == storage_b.state && view.generation == 2u &&
             exact_initialized_state(&storage_b, native_state_bytes, 9u,
                                     UINT8_C(0xB2));
    for (i = 0u; i < ACT_POOL_COUNT; ++i) {
        uint32_t expected_flags = ACT_PRIVATE_FLAG << i;
        if (i != 0u) {
            expected_flags |= SC_SLOT_VALID;
        }
        passed = passed && pool[i].raw == raw_before[i] &&
                 pool[i].flags == expected_flags;
    }
    report(&tests, &failures,
           "commit publishes B once and resets exact state pool bytes", passed);

    {
        sc_activation_controller_t before = controller;
        sc_slot_t before_pool[ACT_POOL_COUNT];
        memcpy(before_pool, pool, sizeof(before_pool));
        passed = sc_activation_commit(&controller, &copied, &previous) ==
                     SC_ERR_TOKEN &&
                 sc_activation_commit(&controller, &token, &previous) ==
                     SC_ERR_TOKEN &&
                 memcmp(&before, &controller, sizeof(controller)) == 0 &&
                 memcmp(before_pool, pool, sizeof(before_pool)) == 0;
    }
    report(&tests, &failures,
           "stale copied and reused zero tokens fail without mutation", passed);

    memset(&state_a_storage, 0xA3, sizeof(state_a_storage));
    for (i = 0u; i < ACT_POOL_COUNT; ++i) {
        pool[i].flags = SC_SLOT_VALID | SC_SLOT_UPDATED | SC_SLOT_CHANGED |
                        SC_SLOT_STALE | (ACT_PRIVATE_FLAG << i);
    }
    memset(&token, 0, sizeof(token));
    passed = sc_activation_prepare(&controller, &descriptor_a, &storage_a,
                                   &token) == SC_OK &&
             sc_activation_commit(&controller, &token, &previous) == SC_OK &&
             previous.descriptor == &descriptor_b &&
             sc_activation_view(&controller, &view) == SC_OK &&
             view.descriptor == &descriptor_a && view.generation == 3u &&
             exact_initialized_state(&storage_a, native_state_bytes, 0u,
                                     UINT8_C(0xA3));
    for (i = 0u; i < ACT_POOL_COUNT; ++i) {
        uint32_t expected_flags = ACT_PRIVATE_FLAG << i;
        if (i != 0u) expected_flags |= SC_SLOT_VALID;
        passed = passed && pool[i].flags == expected_flags;
    }
    report(&tests, &failures,
           "A-B-A activation increments generation and resets state pool",
           passed);

    controller.generation = UINT32_MAX;
    memset(&state_b_storage, 0xB3, sizeof(state_b_storage));
    memset(&token, 0, sizeof(token));
    passed = sc_activation_prepare(&controller, &descriptor_b, &storage_b,
                                   &token) == SC_OK &&
             token.prepared_generation == UINT32_MAX &&
             sc_activation_commit(&controller, &token, &previous) == SC_OK &&
             previous.descriptor == &descriptor_a &&
             controller.generation == 1u &&
             sc_activation_view(&controller, &view) == SC_OK &&
             view.descriptor == &descriptor_b && view.generation == 1u;
    report(&tests, &failures,
           "generation wraps from UINT32_MAX to one and skips zero", passed);

    memset(&state_a_storage, 0xA4, sizeof(state_a_storage));
    memset(&token, 0, sizeof(token));
    passed = sc_activation_prepare(&controller, &descriptor_a, &storage_a,
                                   &token) == SC_OK &&
             sc_activation_commit(&controller, &token, &previous) == SC_OK &&
             sc_activation_view(&controller, &view) == SC_OK &&
             view.descriptor == &descriptor_a && view.generation == 2u;
    report(&tests, &failures,
           "returned A buffers are reusable only after prior commit returns",
           passed);

    memset(&frame, 0, sizeof(frame));
    memset(&tx_token, 0, sizeof(tx_token));
    pool[1].raw = UINT64_C(0x1234);
    pool[2].raw = UINT64_C(0xA5);
    pool[1].flags |= SC_SLOT_VALID;
    pool[2].flags |= SC_SLOT_VALID;
    passed = sc_encode_prepare(view.schema, view.state, ACT_LOGICAL_TX_ID,
                               pool, ACT_POOL_COUNT, &frame, scratch,
                               sizeof(scratch), &tx_token) == SC_OK &&
             frame.id == ACT_SCHEMA_A_TX_ID && frame.data[0] == 0u &&
             sc_encode_commit(&tx_token, 0) == SC_OK;
    report(&tests, &failures,
           "recovered A schema exposes reset TX counter after publication",
           passed);

    for (i = 0u; i < SC_TEST_MALFORMED_REPRESENTATIVE_COUNT; ++i) {
        const sc_test_malformed_representative_t *representative =
            &sc_test_malformed_representatives[i];
        size_t malformed_size = 0u;

        passed = sc_test_make_malformed_representative(
            malformed_image, sizeof(malformed_image), &malformed_size,
            image_b, sizeof(image_b), i);
        bad = make_descriptor(malformed_image, pool_hash);
        bad.image_size = malformed_size;
        fixture_sha256(malformed_image, malformed_size, bad.image_sha256);
        bad.image_feature_flags =
            (uint16_t)((uint16_t)malformed_image[10] |
                       ((uint16_t)malformed_image[11] << 8));
        memset(&token, 0x7C, sizeof(token));
        passed = passed &&
                 prepare_failure_is_atomic(
                     &controller, &bad, &storage_b, &token, pool,
                     representative->expected) &&
                 sc_activation_view(&controller, &view) == SC_OK &&
                 view.descriptor == &descriptor_a && view.generation == 2u &&
                 sc_activation_prepare(&controller, &descriptor_b, &storage_b,
                                       &token) == SC_OK &&
                 sc_activation_abort(&controller, &token, &released) == SC_OK &&
                 sc_activation_view(&controller, &view) == SC_OK &&
                 view.descriptor == &descriptor_a && view.generation == 2u;
        report(&tests, &failures, representative->id, passed);
    }

    if (failures != 0u) {
        printf("FAILED (%u of %u tests)\n", failures, tests);
        return EXIT_FAILURE;
    }
    printf("ALL PASS (%u tests)\n", tests);
    return EXIT_SUCCESS;
}
