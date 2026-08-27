#include "scimg_activation_a.h"
#include "scimg_activation_b.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define SCHEMA_STORAGE_BYTES 128u
#define STATE_STORAGE_BYTES 64u
#define POOL_COUNT 3u
#define TX_LOGICAL_ID 33u

typedef union {
    void *pointer_alignment;
    uint64_t integer_alignment;
    double double_alignment;
    uint8_t bytes[SCHEMA_STORAGE_BYTES];
} aligned_schema_storage_t;

typedef union {
    void *pointer_alignment;
    uint64_t integer_alignment;
    double double_alignment;
    uint8_t bytes[STATE_STORAGE_BYTES];
} aligned_state_storage_t;

static int failures;

static void check(const char *name, int passed)
{
    if (passed) {
        printf("PASS: %s\n", name);
    } else {
        printf("FAIL: %s\n", name);
        failures++;
    }
}

static sc_activation_storage_t make_storage(aligned_schema_storage_t *schema,
                                            aligned_state_storage_t *state)
{
    sc_activation_storage_t storage;
    storage.schema = (sc_schema_t *)(void *)schema->bytes;
    storage.schema_capacity = sizeof(schema->bytes);
    storage.state = (sc_runtime_state_t *)(void *)state->bytes;
    storage.state_capacity = sizeof(state->bytes);
    return storage;
}

static int encode_exact(const sc_activation_view_t *view, sc_slot_t *pool,
                        uint32_t expected_id, const uint8_t expected[8])
{
    uint8_t scratch[8];
    sc_frame_t frame;
    sc_tx_token_t token;

    memset(&frame, 0, sizeof(frame));
    memset(&token, 0, sizeof(token));
    if (sc_encode_prepare(view->schema, view->state, TX_LOGICAL_ID, pool,
                          POOL_COUNT, &frame, scratch, sizeof(scratch),
                          &token) != SC_OK) {
        return 0;
    }
    if (frame.id != expected_id || frame.flags != 0u || frame.len != 8u ||
        memcmp(frame.data, expected, 8u) != 0) {
        (void)sc_encode_commit(&token, 0);
        return 0;
    }
    return sc_encode_commit(&token, 1) == SC_OK;
}

static int decode_exact(const sc_activation_view_t *view, sc_slot_t *pool,
                        uint32_t id, const uint8_t payload[8],
                        uint64_t expected_raw)
{
    sc_frame_t frame;

    memset(&frame, 0, sizeof(frame));
    frame.id = id;
    frame.len = 8u;
    memcpy(frame.data, payload, 8u);
    return sc_decode_at(view->schema, view->state, 1u, &frame, pool,
                        POOL_COUNT) == SC_OK &&
           pool[0].raw == expected_raw &&
           (pool[0].flags & SC_SLOT_VALID) != 0u;
}

int main(void)
{
    static const uint8_t tx_a_0[8] =
        {0x00u, 0x34u, 0x12u, 0xA5u, 0x00u, 0x00u, 0x00u, 0xA5u};
    static const uint8_t tx_a_1[8] =
        {0x01u, 0x34u, 0x12u, 0xA5u, 0x00u, 0x00u, 0x00u, 0xF8u};
    static const uint8_t tx_b_9[8] =
        {0x09u, 0x34u, 0x12u, 0xA5u, 0x00u, 0x00u, 0x00u, 0x2Au};
    static const uint8_t rx_a_0[8] =
        {0x00u, 0x78u, 0x56u, 0xBCu, 0x00u, 0x00u, 0x87u, 0xC8u};
    static const uint8_t rx_b_9[8] =
        {0x09u, 0x68u, 0x24u, 0xBCu, 0x00u, 0x00u, 0x22u, 0x2Bu};
    aligned_schema_storage_t schema_a;
    aligned_schema_storage_t schema_b;
    aligned_state_storage_t state_a;
    aligned_state_storage_t state_b;
    sc_activation_storage_t storage_a;
    sc_activation_storage_t storage_b;
    sc_activation_target_t target;
    sc_activation_controller_t controller;
    sc_activation_token_t token;
    sc_activation_slot_t released;
    sc_activation_slot_t previous;
    sc_activation_view_t view;
    sc_activation_descriptor_t descriptor_c;
    sc_activation_controller_t controller_before_c;
    sc_activation_token_t token_before_c;
    sc_slot_t pool_before_c[POOL_COUNT];
    sc_slot_t pool[POOL_COUNT];
    uint8_t schema_before_c[SCHEMA_STORAGE_BYTES];
    uint8_t state_before_c[STATE_STORAGE_BYTES];
    uint8_t image_c[GSCIMG_SCHEMA_B_BYTE_COUNT];
    sc_status_t status;

    memset(&schema_a, 0, sizeof(schema_a));
    memset(&schema_b, 0, sizeof(schema_b));
    memset(&state_a, 0, sizeof(state_a));
    memset(&state_b, 0, sizeof(state_b));
    memset(&controller, 0, sizeof(controller));
    memset(&target, 0, sizeof(target));
    memset(&token, 0, sizeof(token));
    memset(pool, 0, sizeof(pool));
    pool[1] = (sc_slot_t){UINT64_C(0x1234), SC_SLOT_VALID};
    pool[2] = (sc_slot_t){UINT64_C(0xA5), SC_SLOT_VALID};
    storage_a = make_storage(&schema_a, &state_a);
    storage_b = make_storage(&schema_b, &state_b);

    target.struct_size = (uint32_t)sizeof(target);
    target.runtime_abi = SC_RUNTIME_ABI_ILP32;
    target.runtime_image_major = 1u;
    target.runtime_image_minor = 0u;
    target.supported_features = UINT32_C(0x00003FFF);
    memcpy(target.pool_abi_sha256, gScimgSchemaAPoolAbiSha256,
           sizeof(target.pool_abi_sha256));
    target.scratch_capacity = 8u;
    target.pool = pool;
    target.pool_count = POOL_COUNT;

    status = sc_activation_init(&controller, &target,
                                &gScimgSchemaAActivationDescriptor,
                                &storage_a);
    if (status != SC_OK) {
        printf("activation init status: %d\n", (int)status);
    }
    check("initialize schema A generation 1",
          status == SC_OK &&
          sc_activation_view(&controller, &view) == SC_OK &&
          view.descriptor == &gScimgSchemaAActivationDescriptor &&
          view.generation == 1u);
    check("schema A exact TX counter 0",
          encode_exact(&view, pool, 0x325u, tx_a_0));
    check("schema A exact RX counter 0",
          decode_exact(&view, pool, 0x326u, rx_a_0, UINT64_C(0x5678)));

    check("prepare B leaves A active",
          sc_activation_prepare(&controller,
                                &gScimgSchemaBActivationDescriptor,
                                &storage_b, &token) == SC_OK &&
          sc_activation_view(&controller, &view) == SC_OK &&
          view.descriptor == &gScimgSchemaAActivationDescriptor &&
          view.generation == 1u);
    check("abort B leaves A active",
          sc_activation_abort(&controller, &token, &released) == SC_OK &&
          released.descriptor == &gScimgSchemaBActivationDescriptor &&
          sc_activation_view(&controller, &view) == SC_OK &&
          view.descriptor == &gScimgSchemaAActivationDescriptor &&
          view.generation == 1u);
    check("schema A continues at TX counter 1 after abort",
          encode_exact(&view, pool, 0x325u, tx_a_1));

    check("prepare B again",
          sc_activation_prepare(&controller,
                                &gScimgSchemaBActivationDescriptor,
                                &storage_b, &token) == SC_OK);
    check("commit B publishes generation 2 and resets RX validity",
          sc_activation_commit(&controller, &token, &previous) == SC_OK &&
          previous.descriptor == &gScimgSchemaAActivationDescriptor &&
          sc_activation_view(&controller, &view) == SC_OK &&
          view.descriptor == &gScimgSchemaBActivationDescriptor &&
          view.generation == 2u && pool[0].raw == UINT64_C(0x5678) &&
          (pool[0].flags & (SC_SLOT_VALID | SC_SLOT_UPDATED |
                            SC_SLOT_CHANGED | SC_SLOT_STALE)) == 0u);
    check("schema B exact RX counter 9 after reset",
          decode_exact(&view, pool, 0x336u, rx_b_9, UINT64_C(0x2468)));
    check("schema B exact TX counter 9 after reset",
          encode_exact(&view, pool, 0x335u, tx_b_9));

    memcpy(image_c, gScimgSchemaBBytes, sizeof(image_c));
    image_c[64] ^= UINT8_C(0x01);
    descriptor_c = gScimgSchemaBActivationDescriptor;
    descriptor_c.image = image_c;
    controller_before_c = controller;
    memset(&token, 0xC3, sizeof(token));
    token_before_c = token;
    memcpy(pool_before_c, pool, sizeof(pool_before_c));
    memcpy(schema_before_c, storage_a.schema, sizeof(schema_before_c));
    memcpy(state_before_c, storage_a.state, sizeof(state_before_c));
    check("malformed C is rejected atomically",
          sc_activation_prepare(&controller, &descriptor_c, &storage_a,
                                &token) == SC_ERR_CRC &&
          memcmp(&controller_before_c, &controller, sizeof(controller)) == 0 &&
          memcmp(&token_before_c, &token, sizeof(token)) == 0 &&
          memcmp(pool_before_c, pool, sizeof(pool_before_c)) == 0 &&
          memcmp(schema_before_c, storage_a.schema,
                 sizeof(schema_before_c)) == 0 &&
          memcmp(state_before_c, storage_a.state, sizeof(state_before_c)) == 0 &&
          sc_activation_view(&controller, &view) == SC_OK &&
          view.descriptor == &gScimgSchemaBActivationDescriptor &&
          view.generation == 2u);

    if (failures != 0) {
        printf("FAILED (%d tests)\n", failures);
        return EXIT_FAILURE;
    }
    printf("ALL PASS\n");
    return EXIT_SUCCESS;
}
