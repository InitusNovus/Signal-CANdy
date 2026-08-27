#ifndef SIGNAL_CANDY_RUNTIME_H
#define SIGNAL_CANDY_RUNTIME_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/** Status returned by Signal-CANdy runtime operations. */
typedef enum sc_status {
    /** Operation completed successfully. */
    SC_OK = 0,
    /** Decode completed successfully, but no CAN message matched the frame. */
    SC_OK_NO_MATCH = 1,
    /** A required pointer argument was NULL. */
    SC_ERR_NULL = -1,
    /** The runtime-image magic bytes are invalid. */
    SC_ERR_MAGIC = -2,
    /** The runtime-image format version is unsupported. */
    SC_ERR_VERSION = -3,
    /** The supplied image size or encoded total size is invalid. */
    SC_ERR_SIZE = -4,
    /** An offset, range, frame length, or descriptor value is out of bounds. */
    SC_ERR_BOUNDS = -5,
    /** A runtime-image section does not satisfy the required alignment. */
    SC_ERR_ALIGN = -6,
    /** A table invariant, reserved field, ordering rule, or conversion is invalid. */
    SC_ERR_TABLE = -7,
    /** The runtime-image CRC32 does not match its contents. */
    SC_ERR_CRC = -8,
    /** A v1 runtime-image resource limit is exceeded. */
    SC_ERR_LIMIT = -9,
    /** The caller-provided signal pool is too small. */
    SC_ERR_POOL = -10
} sc_status_t;

/** A normalized CAN/CAN-FD receive frame. flags bit 0 denotes an extended frame. */
typedef struct {
    uint32_t id;
    uint8_t flags;
    uint8_t len;
    uint8_t data[64];
} sc_frame_t;

/**
 * One uniform signal-pool slot.
 *
 * flags bit 0 is valid, bit 1 is updated, and bit 2 is changed. Integer values
 * use their uint64_t representation; floating-point values use IEEE-754 bits.
 */
typedef struct {
    uint64_t raw;
    uint32_t flags;
} sc_slot_t;

/** Validated, immutable runtime-image view. Its representation is private. */
typedef struct sc_schema sc_schema_t;

/**
 * Return the number of bytes required for one sc_schema_t object.
 *
 * The caller owns this suitably aligned storage. The runtime performs no heap
 * allocation. The returned size is constant for a given runtime build.
 */
size_t sc_schema_size(void);

/**
 * Validate and open a v1 runtime image.
 *
 * schema must point to at least sc_schema_size() suitably aligned writable
 * bytes. image remains caller-owned and must remain unchanged and alive for the
 * lifetime of schema. Every v1 header, directory, table, padding, limit, and
 * CRC invariant is checked before schema is modified.
 *
 * Returns SC_OK on success or a negative sc_status_t value on failure. A failed
 * call leaves the previous contents of schema unchanged.
 */
sc_status_t sc_schema_open(sc_schema_t *schema, const void *image,
                           size_t image_size);

/**
 * Return the number of messages in an opened schema.
 *
 * Returns zero when schema is NULL or is not an opened schema.
 */
uint16_t sc_schema_message_count(const sc_schema_t *schema);

/**
 * Return the number of signal programs (and required pool slots) in a schema.
 *
 * Returns zero when schema is NULL or is not an opened schema.
 */
uint16_t sc_schema_signal_count(const sc_schema_t *schema);

/**
 * Return caller-owned persistent state bytes required by a schema.
 *
 * Runtime image v1 is state-independent outside the pool, so this always
 * returns zero, including for NULL schema.
 */
size_t sc_schema_required_state_bytes(const sc_schema_t *schema);

/**
 * Return caller-owned scratch bytes required by decode.
 *
 * Runtime image v1 decodes with stack temporaries, so this always returns zero,
 * including for NULL schema.
 */
size_t sc_schema_required_scratch_bytes(const sc_schema_t *schema);

/**
 * Decode one receive frame into a caller-owned uniform slot pool.
 *
 * pool_count must be at least sc_schema_signal_count(schema). Frame length must
 * be in 0..64. A program outside the supplied payload is skipped without
 * changing its slot. A matching decoded signal sets valid and updated; changed
 * is set only when a previously valid slot receives different raw bits.
 * Identity conversions retain sign-extended or unsigned integer bits. Affine
 * conversions store IEEE-754 double bits. The v1 descriptor has no f32 storage
 * discriminator; integer and double slots are the executable v1 encodings (the
 * conformance harness therefore exercises those two forms).
 *
 * Calls for one schema/pool are single-writer. Synchronization against readers
 * and protection from torn reads are the caller's responsibility.
 *
 * Returns SC_OK, SC_OK_NO_MATCH, or a negative sc_status_t value.
 */
sc_status_t sc_decode(const sc_schema_t *schema, const sc_frame_t *frame,
                      sc_slot_t *pool, size_t pool_count);

#ifdef __cplusplus
}
#endif

#endif /* SIGNAL_CANDY_RUNTIME_H */
