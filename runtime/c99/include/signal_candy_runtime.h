#ifndef SIGNAL_CANDY_RUNTIME_H
#define SIGNAL_CANDY_RUNTIME_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SC_FRAME_EXTENDED UINT8_C(0x01)
#define SC_FRAME_FD UINT8_C(0x02)

#define SC_SLOT_VALID UINT32_C(0x01)
#define SC_SLOT_UPDATED UINT32_C(0x02)
#define SC_SLOT_CHANGED UINT32_C(0x04)

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
    SC_ERR_TOKEN = -16
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

sc_status_t sc_encode_prepare(const sc_schema_t *schema,
                              sc_runtime_state_t *state,
                              uint32_t logical_message_id,
                              const sc_slot_t *pool, size_t pool_count,
                              sc_frame_t *frame, void *scratch,
                              size_t scratch_size, sc_tx_token_t *token);

sc_status_t sc_encode_commit(sc_tx_token_t *token, int transmitted);

#ifdef __cplusplus
}
#endif

#endif /* SIGNAL_CANDY_RUNTIME_H */
