/* Same-DBC generated-AOT versus SCIMG runtime differential conformance. */
#include "signal_candy_runtime.h"
#include "diff_classic_tx.h"
#include "diff_classic_rx.h"
#include "diff_fd_tx.h"
#include "diff_fd_rx.h"
#include "diff_scaled_tx.h"
#include "diff_scaled_rx.h"
#include "diff_mux_tx.h"
#include "diff_mux_rx.h"
#include "diff_protected_tx.h"
#include "diff_protected_rx.h"
#include "p0_utils.h"

#include <inttypes.h>
#include <stdio.h>
#include <string.h>

#define IMAGE_CAPACITY ((size_t)1048576u)
#define SLOT_COUNT ((size_t)14u)
#define STATE_CAPACITY ((size_t)512u)
#define SCRATCH_CAPACITY ((size_t)64u)

#define LOGICAL_CLASSIC UINT32_C(1001)
#define LOGICAL_FD UINT32_C(1002)
#define LOGICAL_SCALED UINT32_C(1003)
#define LOGICAL_MUX UINT32_C(1004)
#define LOGICAL_PROTECTED UINT32_C(1005)

#define SLOT_CLASSIC_TX 0u
#define SLOT_CLASSIC_RX 1u
#define SLOT_FD_TX 2u
#define SLOT_FD_RX 3u
#define SLOT_SCALED_TX 4u
#define SLOT_SCALED_RX 5u
#define SLOT_MUX_SELECTOR_TX 6u
#define SLOT_MUX_BASE_TX 7u
#define SLOT_MUX_BRANCH_TX 8u
#define SLOT_MUX_SELECTOR_RX 9u
#define SLOT_MUX_BASE_RX 10u
#define SLOT_MUX_BRANCH_RX 11u
#define SLOT_PROTECTED_TX 12u
#define SLOT_PROTECTED_RX 13u

typedef union {
    void *pointer_alignment;
    uint64_t integer_alignment;
    double double_alignment;
    unsigned char bytes[128];
} schema_storage_t;

typedef union {
    void *pointer_alignment;
    uint64_t integer_alignment;
    double double_alignment;
    unsigned char bytes[STATE_CAPACITY];
} state_storage_t;

static unsigned char image[IMAGE_CAPACITY];
static sc_slot_t pool[SLOT_COUNT];
static unsigned char scratch[SCRATCH_CAPACITY];
static unsigned failures;

static void require(int condition, const char *name)
{
    if (!condition) {
        printf("FAIL %s\n", name);
        ++failures;
    }
}

static void report_class(const char *name, unsigned before)
{
    if (failures == before) {
        printf("PASS %s\n", name);
    }
}

static int load_image(const char *path, size_t *size)
{
    FILE *file = fopen(path, "rb");
    long length;

    if (file == NULL || fseek(file, 0L, SEEK_END) != 0) {
        if (file != NULL) {
            (void)fclose(file);
        }
        return 0;
    }
    length = ftell(file);
    if (length < 0L || (size_t)length > IMAGE_CAPACITY ||
        fseek(file, 0L, SEEK_SET) != 0 ||
        fread(image, 1u, (size_t)length, file) != (size_t)length ||
        fclose(file) != 0) {
        return 0;
    }
    *size = (size_t)length;
    return 1;
}

static uint64_t double_bits(double value)
{
    uint64_t bits;
    memcpy(&bits, &value, sizeof(bits));
    return bits;
}

static void set_u64_slot(size_t slot, uint64_t value)
{
    pool[slot].raw = value;
    pool[slot].flags = SC_SLOT_VALID;
}

static void set_f64_slot(size_t slot, double value)
{
    pool[slot].raw = double_bits(value);
    pool[slot].flags = SC_SLOT_VALID;
}

static sc_frame_t runtime_encode(const sc_schema_t *schema,
                                 sc_runtime_state_t *state,
                                 uint32_t logical_id, sc_tx_token_t *token)
{
    sc_frame_t frame;
    memset(&frame, 0, sizeof(frame));
    memset(token, 0, sizeof(*token));
    require(sc_encode_prepare(schema, state, logical_id, pool, SLOT_COUNT,
                              &frame, scratch, sizeof(scratch), token) == SC_OK,
            "SCIMG encode prepare");
    return frame;
}

static void require_same_payload(const sc_frame_t *frame,
                                 const uint8_t *aot, uint8_t aot_len,
                                 const char *name)
{
    size_t i;
    int same = frame->len == aot_len &&
               memcmp(frame->data, aot, (size_t)aot_len) == 0;
    if (!same) {
        printf("DETAIL %s AOT=", name);
        for (i = 0u; i < (size_t)aot_len; ++i) {
            printf("%02X", aot[i]);
        }
        printf(" SCIMG=");
        for (i = 0u; i < (size_t)frame->len; ++i) {
            printf("%02X", frame->data[i]);
        }
        printf("\n");
    }
    require(same, name);
}

static void decode_runtime(const sc_schema_t *schema,
                           sc_runtime_state_t *state, sc_frame_t frame,
                           uint32_t rx_id)
{
    frame.id = rx_id;
    require(sc_decode_state(schema, state, &frame, pool, SLOT_COUNT) == SC_OK,
            "SCIMG RX decode");
}

static void require_frame_identity(const sc_frame_t *frame, uint32_t can_id,
                                   int fd, const char *name)
{
    int same = frame->id == can_id &&
               ((frame->flags & SC_FRAME_FD) != 0u) == (fd != 0) &&
               (frame->flags & SC_FRAME_EXTENDED) == 0u;
    if (!same) {
        printf("DETAIL %s id=%lu flags=%u expected id=%lu fd=%d\n", name,
               (unsigned long)frame->id, (unsigned)frame->flags,
               (unsigned long)can_id, fd);
    }
    require(same, name);
}

static void classic_fd_classes(const sc_schema_t *schema,
                               sc_runtime_state_t *state)
{
    uint8_t aot[64];
    uint8_t length;
    sc_frame_t frame;
    sc_tx_token_t token;
    DIFF_CLASSIC_TX_t classic_tx;
    DIFF_CLASSIC_RX_t classic_rx;
    DIFF_FD_TX_t fd_tx;
    DIFF_FD_RX_t fd_rx;
    unsigned before = failures;

    memset(&classic_tx, 0, sizeof(classic_tx));
    classic_tx.LeValue = 4660.0f;
    set_u64_slot(SLOT_CLASSIC_TX, UINT64_C(4660));
    require(DIFF_CLASSIC_TX_encode(aot, &length, &classic_tx),
            "AOT classic encode");
    frame = runtime_encode(schema, state, LOGICAL_CLASSIC, &token);
    require_same_payload(&frame, aot, length, "classic exact TX bytes");
    require_frame_identity(&frame, UINT32_C(256), 0, "classic TX CAN identity");
    require(frame.len == 8u && (frame.flags & SC_FRAME_FD) == 0u,
            "classic frame shape");
    require(sc_encode_commit(&token, 1) == SC_OK, "classic commit");
    memset(&classic_rx, 0, sizeof(classic_rx));
    require(DIFF_CLASSIC_RX_decode(&classic_rx, frame.data, frame.len),
            "AOT classic decode");
    decode_runtime(schema, state, frame, UINT32_C(257));
    require(get_bits_le(frame.data, 0u, 16u) == UINT64_C(4660) &&
                classic_rx.LeValue == 4660.0f &&
                pool[SLOT_CLASSIC_RX].raw == UINT64_C(4660) &&
                pool[SLOT_CLASSIC_RX].flags ==
                    (SC_SLOT_VALID | SC_SLOT_UPDATED),
            "classic RX raw physical slot state");

    memset(&fd_tx, 0, sizeof(fd_tx));
    fd_tx.MotoValue = 165.0f;
    set_u64_slot(SLOT_FD_TX, UINT64_C(165));
    require(DIFF_FD_TX_encode(aot, &length, &fd_tx), "AOT FD encode");
    frame = runtime_encode(schema, state, LOGICAL_FD, &token);
    require_same_payload(&frame, aot, length, "FD exact TX bytes");
    require_frame_identity(&frame, UINT32_C(272), 1, "FD TX CAN identity");
    require(frame.len == 12u && (frame.flags & SC_FRAME_FD) != 0u,
            "FD frame shape");
    require(sc_encode_commit(&token, 1) == SC_OK, "FD commit");
    memset(&fd_rx, 0, sizeof(fd_rx));
    require(DIFF_FD_RX_decode(&fd_rx, frame.data, frame.len),
            "AOT FD decode");
    decode_runtime(schema, state, frame, UINT32_C(273));
    require(get_bits_be(frame.data, 7u, 8u) == UINT64_C(165) &&
                fd_rx.MotoValue == 165.0f &&
                pool[SLOT_FD_RX].raw == UINT64_C(165) &&
                pool[SLOT_FD_RX].flags ==
                    (SC_SLOT_VALID | SC_SLOT_UPDATED),
            "FD Motorola RX raw physical slot state");
    report_class("classic-fd", before);

    before = failures;
    require(aot[0] != 0u || aot[1] != 0u, "Motorola payload populated");
    require(pool[SLOT_CLASSIC_RX].raw == UINT64_C(4660) &&
                pool[SLOT_FD_RX].raw == UINT64_C(165),
            "LE and Motorola runtime agreement");
    report_class("le-motorola", before);
}

static void signed_scaled_class(const sc_schema_t *schema,
                                sc_runtime_state_t *state)
{
    uint8_t aot[64];
    uint8_t length;
    sc_frame_t frame;
    sc_tx_token_t token;
    DIFF_SCALED_TX_t tx;
    DIFF_SCALED_RX_t rx;
    unsigned before = failures;

    memset(&tx, 0, sizeof(tx));
    tx.SignedScaled = -30.0f;
    set_f64_slot(SLOT_SCALED_TX, -30.0);
    require(DIFF_SCALED_TX_encode(aot, &length, &tx),
            "AOT signed scaled encode");
    frame = runtime_encode(schema, state, LOGICAL_SCALED, &token);
    require_same_payload(&frame, aot, length, "signed scaled exact TX bytes");
    require_frame_identity(&frame, UINT32_C(288), 0,
                           "signed scaled TX CAN identity");
    require(sc_encode_commit(&token, 1) == SC_OK, "scaled commit");
    memset(&rx, 0, sizeof(rx));
    require(DIFF_SCALED_RX_decode(&rx, frame.data, frame.len),
            "AOT signed scaled decode");
    decode_runtime(schema, state, frame, UINT32_C(289));
    require((get_bits_le(frame.data, 0u, 12u) == UINT64_C(0xFB0)) &&
                rx.SignedScaled == -30.0f &&
                pool[SLOT_SCALED_RX].raw == double_bits(-30.0) &&
                pool[SLOT_SCALED_RX].flags ==
                    (SC_SLOT_VALID | SC_SLOT_UPDATED),
            "signed scaled RX raw physical slot state");
    report_class("signed-scaled", before);
}

static void mux_class(const sc_schema_t *schema, sc_runtime_state_t *state)
{
    uint8_t aot[64];
    uint8_t length;
    sc_frame_t frame;
    sc_tx_token_t token;
    DIFF_MUX_TX_t tx;
    DIFF_MUX_RX_t rx;
    unsigned before = failures;

    memset(&tx, 0, sizeof(tx));
    tx.Selector = 1.0f;
    tx.BaseValue = 90.0f;
    tx.BranchValue = 48879.0f;
    set_u64_slot(SLOT_MUX_SELECTOR_TX, UINT64_C(1));
    set_u64_slot(SLOT_MUX_BASE_TX, UINT64_C(90));
    set_u64_slot(SLOT_MUX_BRANCH_TX, UINT64_C(48879));
    require(DIFF_MUX_TX_encode(aot, &length, &tx), "AOT mux encode");
    frame = runtime_encode(schema, state, LOGICAL_MUX, &token);
    require_same_payload(&frame, aot, length, "mux exact TX bytes");
    require_frame_identity(&frame, UINT32_C(304), 0, "mux TX CAN identity");
    require(sc_encode_commit(&token, 1) == SC_OK, "mux commit");
    memset(&rx, 0, sizeof(rx));
    require(DIFF_MUX_RX_decode(&rx, frame.data, frame.len), "AOT mux decode");
    decode_runtime(schema, state, frame, UINT32_C(305));
    require((rx.valid & DIFF_MUX_RX_VALID_BRANCHVALUE) != 0u &&
                rx.BranchValue == 48879.0f &&
                pool[SLOT_MUX_SELECTOR_RX].raw == UINT64_C(1) &&
                pool[SLOT_MUX_BASE_RX].raw == UINT64_C(90) &&
                pool[SLOT_MUX_BRANCH_RX].raw == UINT64_C(48879) &&
                pool[SLOT_MUX_BRANCH_RX].flags ==
                    (SC_SLOT_VALID | SC_SLOT_UPDATED),
            "mux RX raw physical slot state");
    report_class("mux", before);
}

static sc_frame_t protected_prepare(const sc_schema_t *schema,
                                    sc_runtime_state_t *state,
                                    uint8_t counter,
                                    sc_tx_token_t *token)
{
    uint8_t aot[64];
    uint8_t length;
    sc_frame_t frame;
    DIFF_PROTECTED_TX_t tx;

    memset(&tx, 0, sizeof(tx));
    tx.Alive = (float)counter;
    tx.ProtectedValue = 51966.0f;
    require(DIFF_PROTECTED_TX_encode(aot, &length, &tx),
            "AOT protected encode");
    frame = runtime_encode(schema, state, LOGICAL_PROTECTED, token);
    require_same_payload(&frame, aot, length, "protected exact TX bytes");
    return frame;
}

static void protected_classes(const sc_schema_t *schema,
                              sc_runtime_state_t *state)
{
    sc_frame_t first;
    sc_frame_t repeated;
    sc_frame_t advanced;
    sc_frame_t corrupted;
    sc_tx_token_t token;
    DIFF_PROTECTED_RX_t rx;
    DIFF_PROTECTED_RX_counter_state_t aot_counter;
    unsigned before = failures;

    set_u64_slot(SLOT_PROTECTED_TX, UINT64_C(51966));
    memset(&aot_counter, 0, sizeof(aot_counter));
    first = protected_prepare(schema, state, 0u, &token);
    require_frame_identity(&first, UINT32_C(320), 0,
                           "protected TX CAN identity");
    memset(&rx, 0, sizeof(rx));
    require(DIFF_PROTECTED_RX_decode(&rx, first.data, first.len),
            "AOT protected CRC decode");
    require(DIFF_PROTECTED_RX_check_counter(&aot_counter, &rx),
            "AOT protected counter seed");
    decode_runtime(schema, state, first, UINT32_C(321));
    require(rx.ProtectedValue == 51966.0f && rx.Alive == 0.0f &&
                pool[SLOT_PROTECTED_RX].raw == UINT64_C(51966) &&
                pool[SLOT_PROTECTED_RX].flags ==
                    (SC_SLOT_VALID | SC_SLOT_UPDATED),
            "protected RX raw physical slot state");
    corrupted = first;
    corrupted.data[4] ^= UINT8_C(0x40);
    memset(&rx, 0, sizeof(rx));
    require(!DIFF_PROTECTED_RX_decode(&rx, corrupted.data, corrupted.len),
            "AOT corrupted CRC rejected");
    report_class("crc-counter", before);

    before = failures;
    require(sc_encode_commit(&token, 0) == SC_OK,
            "counter transmitted zero commit");
    repeated = protected_prepare(schema, state, 0u, &token);
    require(repeated.len == first.len &&
                memcmp(repeated.data, first.data, first.len) == 0,
            "transmitted zero repeats counter and CRC");
    require(sc_encode_commit(&token, 1) == SC_OK,
            "counter transmitted one commit");
    advanced = protected_prepare(schema, state, 1u, &token);
    require(advanced.len == first.len && advanced.data[0] == 1u &&
                memcmp(advanced.data, first.data, first.len) != 0,
            "transmitted one advances counter and CRC");
    memset(&rx, 0, sizeof(rx));
    require(DIFF_PROTECTED_RX_decode(&rx, advanced.data, advanced.len) &&
                DIFF_PROTECTED_RX_check_counter(&aot_counter, &rx),
            "AOT advanced counter decode");
    memset(&rx, 0, sizeof(rx));
    require(DIFF_PROTECTED_RX_decode(&rx, first.data, first.len) &&
                !DIFF_PROTECTED_RX_check_counter(&aot_counter, &rx),
            "AOT duplicate counter rejected");
    decode_runtime(schema, state, advanced, UINT32_C(321));
    require(pool[SLOT_PROTECTED_RX].raw == UINT64_C(51966),
            "SCIMG advanced counter decode");
    require(sc_encode_commit(&token, 0) == SC_OK,
            "counter final cancellation");
    report_class("counter-transmitted-0-1", before);
}

int main(int argc, char **argv)
{
    schema_storage_t schema_storage;
    state_storage_t state_storage;
    sc_schema_t *schema = (sc_schema_t *)(void *)schema_storage.bytes;
    sc_runtime_state_t *state =
        (sc_runtime_state_t *)(void *)state_storage.bytes;
    size_t image_size;
    size_t state_size;

    if (argc != 2) {
        fprintf(stderr, "usage: diff_harness <p0-aot-scimg.scimg>\n");
        return 2;
    }
    if (sc_schema_size() > sizeof(schema_storage.bytes) ||
        !load_image(argv[1], &image_size)) {
        fprintf(stderr, "could not load differential schema\n");
        return 2;
    }
    memset(&schema_storage, 0, sizeof(schema_storage));
    if (sc_schema_open(schema, image, image_size) != SC_OK) {
        fprintf(stderr, "could not open differential schema\n");
        return 2;
    }
    state_size = sc_schema_required_state_bytes(schema);
    if (state_size > sizeof(state_storage.bytes)) {
        fprintf(stderr, "runtime state exceeds bounded harness storage\n");
        return 2;
    }
    memset(&state_storage, 0, sizeof(state_storage));
    memset(pool, 0, sizeof(pool));
    if (sc_runtime_state_init(schema, state, state_size) != SC_OK) {
        fprintf(stderr, "could not initialize differential runtime state\n");
        return 2;
    }

    classic_fd_classes(schema, state);
    signed_scaled_class(schema, state);
    mux_class(schema, state);
    protected_classes(schema, state);

    if (failures != 0u) {
        printf("%u FAILURE(S)\n", failures);
        return 1;
    }
    printf("ALL PASS\n");
    return 0;
}
