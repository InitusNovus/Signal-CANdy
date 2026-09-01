#ifndef SIGNAL_CANDY_RUNTIME_H
#define SIGNAL_CANDY_RUNTIME_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SC_FRAME_EXTENDED UINT8_C(0x01)
#define SC_FRAME_FD UINT8_C(0x02)
#define SC_FEATURE_PROTECTION UINT16_C(0x0004)

#define SC_SLOT_VALID UINT32_C(0x01)
#define SC_SLOT_UPDATED UINT32_C(0x02)
#define SC_SLOT_CHANGED UINT32_C(0x04)
#define SC_SLOT_STALE UINT32_C(0x08)

#define SC_ACTIVATION_DESCRIPTOR_VERSION_MAJOR UINT16_C(1)
#define SC_ACTIVATION_DESCRIPTOR_VERSION_MINOR UINT16_C(0)

#define SC_RUNTIME_ABI_ILP32 UINT16_C(1)

#define SC_RUNTIME_FEATURE_RX UINT32_C(0x00000001)
#define SC_RUNTIME_FEATURE_TX UINT32_C(0x00000002)
#define SC_RUNTIME_FEATURE_MULTIPLEXING UINT32_C(0x00000004)
#define SC_RUNTIME_FEATURE_NESTED_MUX UINT32_C(0x00000008)
#define SC_RUNTIME_FEATURE_RX_QUALITY UINT32_C(0x00000010)
#define SC_RUNTIME_FEATURE_CAN_FD UINT32_C(0x00000020)
#define SC_RUNTIME_FEATURE_EXTENDED_CAN UINT32_C(0x00000040)
#define SC_RUNTIME_FEATURE_MOTOROLA UINT32_C(0x00000080)
#define SC_RUNTIME_FEATURE_AFFINE UINT32_C(0x00000100)
#define SC_RUNTIME_FEATURE_CRC8_SAE_J1850 UINT32_C(0x00000200)
#define SC_RUNTIME_FEATURE_CRC16_CCITT_FALSE UINT32_C(0x00000400)
#define SC_RUNTIME_FEATURE_CRC_DATA_ID UINT32_C(0x00000800)
#define SC_RUNTIME_FEATURE_RX_COUNTER UINT32_C(0x00001000)
#define SC_RUNTIME_FEATURE_TX_COUNTER UINT32_C(0x00002000)

/** Status returned by Signal-CANdy runtime operations. */
typedef enum sc_status {
    SC_OK = 0,
    SC_OK_NO_MATCH = 1,
    SC_ERR_NULL = -1,
    SC_ERR_MAGIC = -2,
    SC_ERR_VERSION = -3,
    SC_ERR_SIZE = -4,
    SC_ERR_BOUNDS = -5,
    SC_ERR_ALIGN = -6,
    SC_ERR_TABLE = -7,
    SC_ERR_CRC = -8,
    SC_ERR_LIMIT = -9,
    SC_ERR_POOL = -10,
    SC_ERR_FEATURE = -11,
    SC_ERR_STATE = -12,
    SC_ERR_SCRATCH = -13,
    SC_ERR_VALUE = -14,
    SC_ERR_BUSY = -15,
    SC_ERR_TOKEN = -16,
    SC_ERR_TIME = -17,
    SC_ERR_FRAME_CRC = -18,
    SC_ERR_COUNTER = -19
} sc_status_t;

/** A normalized CAN/CAN-FD frame. */
typedef struct {
    uint32_t id;
    uint8_t flags;
    uint8_t len;
    uint8_t data[64];
} sc_frame_t;

/** One uniform signal-pool slot. */
typedef struct {
    uint64_t raw;
    uint32_t flags;
} sc_slot_t;

/** Validated, immutable runtime-image view. Its representation is private. */
typedef struct sc_schema sc_schema_t;

/** Persistent state for one stateful TX counter. */
typedef struct {
    uint32_t current;
    uint32_t pending_generation;
    uint32_t next_generation;
} sc_tx_counter_state_t;

/** Caller-owned persistent state bound to one opened schema. */
typedef struct sc_runtime_state {
    const sc_schema_t *schema;
    uint16_t counter_count;
    uint16_t reserved;
    sc_tx_counter_state_t counters[];
} sc_runtime_state_t;

/** Caller-owned reservation token returned by encode prepare. */
typedef struct {
    const sc_schema_t *schema;
    sc_runtime_state_t *state;
    uint16_t counter_index;
    uint16_t reserved;
    uint32_t generation;
    uint32_t counter_value;
} sc_tx_token_t;

/** Host-emitted, image-bound activation requirements. */
typedef struct {
    uint32_t struct_size;
    uint16_t descriptor_major;
    uint16_t descriptor_minor;
    const uint8_t *image;
    size_t image_size;
    uint8_t image_sha256[32];
    uint8_t pool_abi_sha256[32];
    uint16_t runtime_image_major;
    uint16_t runtime_image_minor;
    uint16_t image_feature_flags;
    uint16_t runtime_abi;
    uint32_t required_features;
    uint32_t runtime_state_bytes;
    uint32_t runtime_scratch_bytes;
    uint32_t pool_slots;
    uint32_t reserved[4];
} sc_activation_descriptor_t;

/** Firmware integration capacities and pool identity. */
typedef struct {
    uint32_t struct_size;
    uint16_t runtime_abi;
    uint16_t runtime_image_major;
    uint16_t runtime_image_minor;
    uint16_t reserved;
    uint32_t supported_features;
    uint8_t pool_abi_sha256[32];
    size_t scratch_capacity;
    sc_slot_t *pool;
    size_t pool_count;
} sc_activation_target_t;

typedef struct {
    sc_schema_t *schema;
    size_t schema_capacity;
    sc_runtime_state_t *state;
    size_t state_capacity;
} sc_activation_storage_t;

typedef struct {
    const sc_activation_descriptor_t *descriptor;
    sc_activation_storage_t storage;
} sc_activation_slot_t;

typedef struct sc_activation_controller sc_activation_controller_t;

#if defined(_MSC_VER)
typedef __declspec(align(8)) uint64_t sc_activation_serial_t;
#elif defined(__GNUC__) || defined(__clang__)
typedef uint64_t sc_activation_serial_t __attribute__((aligned(8)));
#else
typedef uint64_t sc_activation_serial_t;
#endif

typedef struct {
    sc_activation_controller_t *controller;
    sc_activation_serial_t serial;
    uint32_t prepared_generation;
    uint32_t reserved;
} sc_activation_token_t;

typedef struct {
    const sc_activation_descriptor_t *descriptor;
    const sc_schema_t *schema;
    sc_runtime_state_t *state;
    size_t state_capacity;
    uint32_t generation;
    uint32_t reserved;
} sc_activation_view_t;

struct sc_activation_controller {
    uint32_t tag;
    uint32_t generation;
    sc_activation_serial_t next_serial;
    sc_slot_t *pool;
    size_t pool_count;
    size_t scratch_capacity;
    uint32_t supported_features;
    uint16_t runtime_abi;
    uint16_t runtime_image_major;
    uint16_t runtime_image_minor;
    uint16_t reserved;
    uint8_t pool_abi_sha256[32];
    sc_activation_slot_t active;
    sc_activation_slot_t pending;
    sc_activation_token_t *pending_token;
    sc_activation_serial_t pending_serial;
};

size_t sc_schema_size(void);
sc_status_t sc_schema_open(sc_schema_t *schema, const void *image,
                           size_t image_size);
uint16_t sc_schema_message_count(const sc_schema_t *schema);
uint16_t sc_schema_signal_count(const sc_schema_t *schema);
uint16_t sc_schema_tx_message_count(const sc_schema_t *schema);
size_t sc_schema_required_state_bytes(const sc_schema_t *schema);
size_t sc_schema_required_scratch_bytes(const sc_schema_t *schema);

sc_status_t sc_runtime_state_init(const sc_schema_t *schema,
                                  sc_runtime_state_t *state,
                                  size_t state_size);

sc_status_t sc_decode(const sc_schema_t *schema, const sc_frame_t *frame,
                      sc_slot_t *pool, size_t pool_count);

sc_status_t sc_decode_state(const sc_schema_t *schema,
                            sc_runtime_state_t *state,
                            const sc_frame_t *frame, sc_slot_t *pool,
                            size_t pool_count);

sc_status_t sc_decode_at(const sc_schema_t *schema,
                         sc_runtime_state_t *state, uint32_t now_ms,
                         const sc_frame_t *frame, sc_slot_t *pool,
                         size_t pool_count);

sc_status_t sc_rx_counter_resync(const sc_schema_t *schema,
                                 sc_runtime_state_t *state,
                                 uint32_t encoded_can_id, uint8_t flags);

sc_status_t sc_expire(const sc_schema_t *schema,
                      sc_runtime_state_t *state, uint32_t now_ms,
                      sc_slot_t *pool, size_t pool_count);

sc_status_t sc_runtime_reset(const sc_schema_t *schema,
                             sc_runtime_state_t *state, size_t state_size,
                             sc_slot_t *pool, size_t pool_count);

sc_status_t sc_encode_prepare(const sc_schema_t *schema,
                              sc_runtime_state_t *state,
                              uint32_t logical_message_id,
                              const sc_slot_t *pool, size_t pool_count,
                              sc_frame_t *frame, void *scratch,
                              size_t scratch_size, sc_tx_token_t *token);

sc_status_t sc_encode_commit(sc_tx_token_t *token, int transmitted);

/**
 * Activation calls are serialized, single-thread operations. The caller must
 * establish a quiescent point before commit: no runtime operation or borrowed
 * activation view may be in use, no ISR may enter the runtime, captured frames
 * for the old schema must be drained or discarded, and no TX reservation may
 * remain. Commit returns SC_ERR_BUSY while a stateful TX reservation is
 * pending. A view is borrowed and expires when the next successful commit
 * begins. All descriptors, images, storage, pool, tokens, and the controller
 * remain caller-owned for their documented active or pending lifetime.
 */
sc_status_t sc_activation_init(
    sc_activation_controller_t *controller,
    const sc_activation_target_t *target,
    const sc_activation_descriptor_t *initial,
    const sc_activation_storage_t *active_storage);
sc_status_t sc_activation_prepare(
    sc_activation_controller_t *controller,
    const sc_activation_descriptor_t *candidate,
    const sc_activation_storage_t *staging_storage,
    sc_activation_token_t *token);
sc_status_t sc_activation_abort(sc_activation_controller_t *controller,
                                sc_activation_token_t *token,
                                sc_activation_slot_t *released);
sc_status_t sc_activation_commit(sc_activation_controller_t *controller,
                                 sc_activation_token_t *token,
                                 sc_activation_slot_t *previous);
sc_status_t sc_activation_view(const sc_activation_controller_t *controller,
                               sc_activation_view_t *view);

#ifdef __cplusplus
}
#endif

#endif /* SIGNAL_CANDY_RUNTIME_H */
