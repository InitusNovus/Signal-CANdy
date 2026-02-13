#include <stdio.h>
#include <string.h>
#include <math.h>
#include "sc_registry.h"
#include "sc_utils.h"

// Include stress test only when available (controlled via -DHAVE_STRESS in Makefile)
#ifdef HAVE_STRESS
extern int test_stress_suite(void);
#endif

#if defined(__has_include)
#  if __has_include("message_1.h")
#    include "message_1.h"
#    define HAVE_MESSAGE_1 1
#  endif
#  if __has_include("fixed_test.h")
#    include "fixed_test.h"
#    define HAVE_FIXED_TEST 1
#  endif
#  if __has_include("c2_msg0280a1_bms2vcu_sts1.h")
#    include "c2_msg0280a1_bms2vcu_sts1.h"
#    define HAVE_EXT_DISPATCH 1
#  endif
#  if __has_include("c2_msg0580a1_bms2vcu_sts6.h")
#    include "c2_msg0580a1_bms2vcu_sts6.h"
#    define HAVE_EXT_DISPATCH 1
#  endif
#  if __has_include("c2_msg1280a1_bms2vcu2.h")
#    include "c2_msg1280a1_bms2vcu2.h"
#    define HAVE_EXT_DISPATCH 1
#  endif
#  if __has_include("lsb_test.h")
#    include "lsb_test.h"
#    define HAVE_LSB_TEST 1
#  endif
#  if __has_include("mux_msg.h")
#    include "mux_msg.h"
#    define HAVE_MUX_MSG 1
#  endif
#  if __has_include("vt_msg.h")
#    include "vt_msg.h"
#    define HAVE_VT_MSG 1
#  endif
#  if __has_include("fd_msg.h")
#    include "fd_msg.h"
#    define HAVE_FD_MSG 1
#  endif
#  if __has_include("msg_comp_le.h")
#    include "msg_comp_le.h"
#    define HAVE_MSG_COMP_LE 1
#  endif
#  if __has_include("msg_comp_be.h")
#    include "msg_comp_be.h"
#    define HAVE_MSG_COMP_BE 1
#  endif
#  if __has_include("msg_comp_signed.h")
#    include "msg_comp_signed.h"
#    define HAVE_MSG_COMP_SIGNED 1
#  endif
#  if __has_include("msg_comp_nonalign.h")
#    include "msg_comp_nonalign.h"
#    define HAVE_MSG_COMP_NONALIGN 1
#  endif
#  if __has_include("msg_comp_packed.h")
#    include "msg_comp_packed.h"
#    define HAVE_MSG_COMP_PACKED 1
#  endif
#  if __has_include("msg_comp_scale.h")
#    include "msg_comp_scale.h"
#    define HAVE_MSG_COMP_SCALE 1
#  endif
#endif

void print_bytes(const uint8_t* data, size_t len) {
    for (size_t i = 0; i < len; ++i) {
        printf("%02X ", data[i]);
    }
    printf("\n");
}

#ifdef HAVE_MESSAGE_1
int test_roundtrip() {
    printf("--- Running test_roundtrip ---\n");
    MESSAGE_1_t msg_tx = { .Signal_1 = 123.0, .Signal_2 = 45.67 }; // Use float literals - Signal_2 max is 100
    uint8_t data[8];
    uint8_t dlc;

    if (!MESSAGE_1_encode(data, &dlc, &msg_tx)) {
        printf("Encode failed\n");
        return 1;
    }

    printf("Encoded data: ");
    print_bytes(data, dlc);

    MESSAGE_1_t msg_rx;
    if (!MESSAGE_1_decode(&msg_rx, data, dlc)) {
        printf("Decode failed\n");
        return 1;
    }

    // Check if the decoded values are close to the original, considering potential float precision issues
    // For fixed-point, this check will be exact.
    if (msg_rx.Signal_1 < 122.9 || msg_rx.Signal_1 > 123.1) { // Allow small tolerance for float
        printf("Decoded Signal_1: %f, Expected: 123\n", msg_rx.Signal_1);
        return 1;
    }
    // With factor 0.1, encode rounds to nearest 0.1; 45.67 becomes ~45.7, allow tiny FP slack
    if (msg_rx.Signal_2 < 45.6 || msg_rx.Signal_2 > 45.71) { // Slightly relaxed upper bound for FP rounding
        printf("Decoded Signal_2: %f, Expected around: 45.7\n", msg_rx.Signal_2);
        return 1;
    }

    printf("Decoded Signal_1: %f\n", msg_rx.Signal_1);
    printf("Decoded Signal_2: %f\n", msg_rx.Signal_2);

    printf("Roundtrip successful!\n");
    return 0;
}

int test_range_check() {
    printf("--- Running test_range_check ---\n");
    // With sample.dbc, Signal_1 range [0,255], Signal_2 range [0,100] (factor 0.1)
    MESSAGE_1_t msg_in_range = { .Signal_1 = 100.0, .Signal_2 = 50.0 };
    uint8_t data_in_range[8];
    uint8_t dlc_in_range;

    if (!MESSAGE_1_encode(data_in_range, &dlc_in_range, &msg_in_range)) {
        printf("Encode failed for in-range values\n");
        return 1;
    }

    MESSAGE_1_t msg_decoded_in_range;
    if (!MESSAGE_1_decode(&msg_decoded_in_range, data_in_range, dlc_in_range)) {
        printf("Decode failed for in-range values, but it should have succeeded.\n");
        return 1;
    }
    printf("Decoded in-range Signal_1: %f, Signal_2: %f\n", msg_decoded_in_range.Signal_1, msg_decoded_in_range.Signal_2);

    // Out-of-range S1 (>255)
    MESSAGE_1_t msg_out_of_range_s1 = { .Signal_1 = 300.0, .Signal_2 = 50.0 };
    uint8_t data_out_of_range_s1[8];
    uint8_t dlc_out_of_range_s1;

    if (!MESSAGE_1_encode(data_out_of_range_s1, &dlc_out_of_range_s1, &msg_out_of_range_s1)) {
        printf("Encode correctly failed for out-of-range Signal_1.\n");
    } else {
        printf("Encode succeeded for out-of-range Signal_1, but it should have failed.\n");
        return 1;
    }

    // Out-of-range S2 (>100)
    MESSAGE_1_t msg_out_of_range_s2 = { .Signal_1 = 100.0, .Signal_2 = 150.0 };
    uint8_t data_out_of_range_s2[8];
    uint8_t dlc_out_of_range_s2;

    if (!MESSAGE_1_encode(data_out_of_range_s2, &dlc_out_of_range_s2, &msg_out_of_range_s2)) {
        printf("Encode correctly failed for out-of-range Signal_2.\n");
    } else {
        printf("Encode succeeded for out-of-range Signal_2, but it should have failed.\n");
        return 1;
    }

    printf("Range check test successful!\n");
    return 0;
}

int test_dispatch() {
    printf("--- Running test_dispatch ---\n");
    uint8_t data[8] = { 0 };
    MESSAGE_1_t msg;
    if (decode_message(100, data, 8, &msg)) {
        printf("Dispatch successful for message ID 100\n");
    } else {
        printf("Dispatch failed for message ID 100\n");
        return 1;
    }

    if (decode_message(99, data, 8, &msg)) {
        printf("Dispatch succeeded for unknown message ID 99, but it should have failed.\n");
        return 1;
    } else {
        printf("Dispatch correctly failed for unknown message ID 99\n");
    }

    return 0;
}
#endif // HAVE_MESSAGE_1

static int assert_equal_u8(const char* name, uint8_t a, uint8_t b) {
    if (a != b) {
        printf("Assertion failed: %s (0x%02X != 0x%02X)\n", name, a, b);
        return 1;
    }
    return 0;
}

static int assert_close_f64(const char* name, double actual, double expected, double tolerance) {
    if (fabs(actual - expected) > tolerance) {
        printf("Assertion failed: %s (got %.6f expected %.6f tol %.6f)\n", name, actual, expected, tolerance);
        return 1;
    }

    printf("PASS: %s (%.6f)\n", name, actual);
    return 0;
}

static int assert_equal_bytes(const char* name, const uint8_t* actual, const uint8_t* expected, size_t len) {
    for (size_t i = 0; i < len; ++i) {
        if (actual[i] != expected[i]) {
            printf("Assertion failed: %s[%zu] (0x%02X != 0x%02X)\n", name, i, actual[i], expected[i]);
            return 1;
        }

        printf("PASS: %s[%zu] = 0x%02X\n", name, i, actual[i]);
    }

    return 0;
}

int test_crc_counter() {
    printf("--- Running test_crc_counter ---\n");
    // CRC/Counter test placeholder - currently no implementation
    printf("CRC/Counter test not implemented yet\n");
    return 0;
}

int test_be_basic() {
    printf("--- Running test_be_basic ---\n");
    uint8_t data[8];
    memset(data, 0, sizeof data);

    // Case 1: start_bit=7, length=8 should map to byte0 in-place
    set_bits_be(data, 7, 8, 0xAB);
    if (assert_equal_u8("byte0 after set_bits_be(7,8,0xAB)", data[0], 0xAB)) return 1;
    uint64_t v1 = get_bits_be(data, 7, 8);
    if ((v1 & 0xFF) != 0xAB) {
        printf("get_bits_be mismatch: got 0x%02llX expected 0xAB\n", (unsigned long long)v1);
        return 1;
    }

    // Case 2: start_bit=15, length=8 should map to byte1
    memset(data, 0, sizeof data);
    set_bits_be(data, 15, 8, 0xCD);
    if (assert_equal_u8("byte1 after set_bits_be(15,8,0xCD)", data[1], 0xCD)) return 1;
    uint64_t v2 = get_bits_be(data, 15, 8);
    if ((v2 & 0xFF) != 0xCD) {
        printf("get_bits_be mismatch: got 0x%02llX expected 0xCD\n", (unsigned long long)v2);
        return 1;
    }

    printf("BE basic test successful!\n");
    return 0;
}

int test_dlc_mapping() {
    printf("--- Running test_dlc_mapping ---\n");

    static const uint8_t expected_len[16] = {
        0, 1, 2, 3, 4, 5, 6, 7,
        8, 12, 16, 20, 24, 32, 48, 64
    };

    for (uint8_t dlc = 0; dlc < 16; ++dlc) {
        uint8_t actual_len = canfd_dlc_to_len(dlc);
        if (actual_len != expected_len[dlc]) {
            printf("FAIL: canfd_dlc_to_len(%u) got %u expected %u\n", (unsigned)dlc, (unsigned)actual_len, (unsigned)expected_len[dlc]);
            return 1;
        }

        printf("PASS: canfd_dlc_to_len(%u) == %u\n", (unsigned)dlc, (unsigned)actual_len);
    }

    if (canfd_dlc_to_len(16) != 64) {
        printf("FAIL: canfd_dlc_to_len(16) expected 64\n");
        return 1;
    }
    printf("PASS: canfd_dlc_to_len(16) == 64\n");

    if (canfd_dlc_to_len(255) != 64) {
        printf("FAIL: canfd_dlc_to_len(255) expected 64\n");
        return 1;
    }
    printf("PASS: canfd_dlc_to_len(255) == 64\n");

    struct len_dlc_case {
        uint8_t len;
        uint8_t expected_dlc;
    };

    static const struct len_dlc_case reverse_cases[] = {
        { 0, 0 }, { 1, 1 }, { 2, 2 }, { 3, 3 }, { 4, 4 }, { 5, 5 }, { 6, 6 }, { 7, 7 }, { 8, 8 },
        { 9, 9 }, { 12, 9 }, { 13, 10 }, { 16, 10 }, { 17, 11 }, { 20, 11 },
        { 21, 12 }, { 24, 12 }, { 25, 13 }, { 32, 13 }, { 33, 14 }, { 48, 14 },
        { 49, 15 }, { 64, 15 }, { 100, 15 }
    };

    for (size_t i = 0; i < sizeof(reverse_cases) / sizeof(reverse_cases[0]); ++i) {
        uint8_t actual_dlc = canfd_len_to_dlc(reverse_cases[i].len);
        if (actual_dlc != reverse_cases[i].expected_dlc) {
            printf("FAIL: canfd_len_to_dlc(%u) got %u expected %u\n", (unsigned)reverse_cases[i].len, (unsigned)actual_dlc, (unsigned)reverse_cases[i].expected_dlc);
            return 1;
        }

        printf("PASS: canfd_len_to_dlc(%u) == %u\n", (unsigned)reverse_cases[i].len, (unsigned)actual_dlc);
    }

    printf("DLC mapping test successful!\n");
    return 0;
}

#ifdef HAVE_MSG_COMP_LE
static int test_comprehensive_le() {
    printf("--- Running test_comprehensive_le ---\n");
    const double tol_int = 0.5;
    const uint8_t known_data[8] = { 0xC0, 0xAB, 0x34, 0x12, 0x4E, 0x61, 0xBC, 0x00 };

    MSG_COMP_LE_t rx = {0};
    if (!MSG_COMP_LE_decode(&rx, known_data, 8)) {
        printf("FAIL: MSG_COMP_LE decode from known bytes failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_LE decode from known bytes\n");

    if (assert_close_f64("MSG_COMP_LE decode LE_12_CROSS", rx.LE_12_CROSS, 2748.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_LE decode LE_16", rx.LE_16, 4660.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_LE decode LE_32", rx.LE_32, 12345678.0, tol_int)) return 1;

    MSG_COMP_LE_t tx = {0};
    tx.LE_12_CROSS = 2748.0f;
    tx.LE_16 = 4660.0f;
    tx.LE_32 = 12345678.0f;

    uint8_t encoded[8] = {0};
    uint8_t out_dlc = 0;
    if (!MSG_COMP_LE_encode(encoded, &out_dlc, &tx)) {
        printf("FAIL: MSG_COMP_LE encode failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_LE encode from physical values\n");

    if (out_dlc != 8) {
        printf("FAIL: MSG_COMP_LE out_dlc got %u expected 8\n", (unsigned)out_dlc);
        return 1;
    }
    printf("PASS: MSG_COMP_LE out_dlc == 8\n");

    if (assert_equal_bytes("MSG_COMP_LE encode byte", encoded, known_data, 8)) return 1;

    printf("Comprehensive LE test successful!\n");
    return 0;
}
#endif

#ifdef HAVE_MSG_COMP_BE
static int test_comprehensive_be() {
    printf("--- Running test_comprehensive_be ---\n");
    const double tol_int = 0.5;
    const uint8_t known_data[8] = { 0xAB, 0xCD, 0x00, 0x00, 0x00, 0xBC, 0x61, 0x4E };

    MSG_COMP_BE_t rx = {0};
    if (!MSG_COMP_BE_decode(&rx, known_data, 8)) {
        printf("FAIL: MSG_COMP_BE decode from known bytes failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_BE decode from known bytes\n");

    if (assert_close_f64("MSG_COMP_BE decode BE_16", rx.BE_16, 43981.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_BE decode BE_32", rx.BE_32, 12345678.0, tol_int)) return 1;

    MSG_COMP_BE_t tx = {0};
    tx.BE_16 = 43981.0f;
    tx.BE_32 = 12345678.0f;

    uint8_t encoded[8] = {0};
    uint8_t out_dlc = 0;
    if (!MSG_COMP_BE_encode(encoded, &out_dlc, &tx)) {
        printf("FAIL: MSG_COMP_BE encode failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_BE encode from physical values\n");

    if (out_dlc != 8) {
        printf("FAIL: MSG_COMP_BE out_dlc got %u expected 8\n", (unsigned)out_dlc);
        return 1;
    }
    printf("PASS: MSG_COMP_BE out_dlc == 8\n");

    if (assert_equal_bytes("MSG_COMP_BE encode byte", encoded, known_data, 8)) return 1;

    printf("Comprehensive BE test successful!\n");
    return 0;
}
#endif

#ifdef HAVE_MSG_COMP_SIGNED
static int test_comprehensive_signed() {
    printf("--- Running test_comprehensive_signed ---\n");
    const double tol_int = 0.5;
    const uint8_t known_neg_data[8] = { 0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00 };
    const uint8_t known_pos_data[8] = { 0x2A, 0xE8, 0x03, 0x01, 0xF4, 0x00, 0x00, 0x00 };

    MSG_COMP_SIGNED_t rx_neg = {0};
    if (!MSG_COMP_SIGNED_decode(&rx_neg, known_neg_data, 8)) {
        printf("FAIL: MSG_COMP_SIGNED negative decode from known bytes failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_SIGNED negative decode from known bytes\n");

    if (assert_close_f64("MSG_COMP_SIGNED decode S_LE_8 negative", rx_neg.S_LE_8, -1.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_SIGNED decode S_LE_16 negative", rx_neg.S_LE_16, -256.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_SIGNED decode S_BE_16 negative", rx_neg.S_BE_16, -1.0, tol_int)) return 1;

    MSG_COMP_SIGNED_t tx_neg = {0};
    tx_neg.S_LE_8 = -1.0f;
    tx_neg.S_LE_16 = -256.0f;
    tx_neg.S_BE_16 = -1.0f;

    uint8_t encoded_neg[8] = {0};
    uint8_t out_dlc_neg = 0;
    if (!MSG_COMP_SIGNED_encode(encoded_neg, &out_dlc_neg, &tx_neg)) {
        printf("FAIL: MSG_COMP_SIGNED negative encode failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_SIGNED negative encode from physical values\n");

    if (out_dlc_neg != 8) {
        printf("FAIL: MSG_COMP_SIGNED negative out_dlc got %u expected 8\n", (unsigned)out_dlc_neg);
        return 1;
    }
    printf("PASS: MSG_COMP_SIGNED negative out_dlc == 8\n");

    if (assert_equal_bytes("MSG_COMP_SIGNED negative encode byte", encoded_neg, known_neg_data, 8)) return 1;

    MSG_COMP_SIGNED_t rx_pos = {0};
    if (!MSG_COMP_SIGNED_decode(&rx_pos, known_pos_data, 8)) {
        printf("FAIL: MSG_COMP_SIGNED positive decode from known bytes failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_SIGNED positive decode from known bytes\n");

    if (assert_close_f64("MSG_COMP_SIGNED decode S_LE_8 positive", rx_pos.S_LE_8, 42.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_SIGNED decode S_LE_16 positive", rx_pos.S_LE_16, 1000.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_SIGNED decode S_BE_16 positive", rx_pos.S_BE_16, 500.0, tol_int)) return 1;

    MSG_COMP_SIGNED_t tx_pos = {0};
    tx_pos.S_LE_8 = 42.0f;
    tx_pos.S_LE_16 = 1000.0f;
    tx_pos.S_BE_16 = 500.0f;

    uint8_t encoded_pos[8] = {0};
    uint8_t out_dlc_pos = 0;
    if (!MSG_COMP_SIGNED_encode(encoded_pos, &out_dlc_pos, &tx_pos)) {
        printf("FAIL: MSG_COMP_SIGNED positive encode failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_SIGNED positive encode from physical values\n");

    if (out_dlc_pos != 8) {
        printf("FAIL: MSG_COMP_SIGNED positive out_dlc got %u expected 8\n", (unsigned)out_dlc_pos);
        return 1;
    }
    printf("PASS: MSG_COMP_SIGNED positive out_dlc == 8\n");

    if (assert_equal_bytes("MSG_COMP_SIGNED positive encode byte", encoded_pos, known_pos_data, 8)) return 1;

    printf("Comprehensive signed test successful!\n");
    return 0;
}
#endif

#ifdef HAVE_MSG_COMP_NONALIGN
static int test_comprehensive_nonalign() {
    printf("--- Running test_comprehensive_nonalign ---\n");
    const double tol_int = 0.5;
    const uint8_t known_data[8] = { 0xF8, 0x9F, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00 };

    MSG_COMP_NONALIGN_t rx = {0};
    if (!MSG_COMP_NONALIGN_decode(&rx, known_data, 8)) {
        printf("FAIL: MSG_COMP_NONALIGN decode from known bytes failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_NONALIGN decode from known bytes\n");

    if (assert_close_f64("MSG_COMP_NONALIGN decode NA_10", rx.NA_10, 1023.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_NONALIGN decode NA_7", rx.NA_7, 100.0, tol_int)) return 1;

    MSG_COMP_NONALIGN_t tx = {0};
    tx.NA_10 = 1023.0f;
    tx.NA_7 = 100.0f;

    uint8_t encoded[8] = {0};
    uint8_t out_dlc = 0;
    if (!MSG_COMP_NONALIGN_encode(encoded, &out_dlc, &tx)) {
        printf("FAIL: MSG_COMP_NONALIGN encode failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_NONALIGN encode from physical values\n");

    if (out_dlc != 8) {
        printf("FAIL: MSG_COMP_NONALIGN out_dlc got %u expected 8\n", (unsigned)out_dlc);
        return 1;
    }
    printf("PASS: MSG_COMP_NONALIGN out_dlc == 8\n");

    if (assert_equal_bytes("MSG_COMP_NONALIGN encode byte", encoded, known_data, 8)) return 1;

    printf("Comprehensive nonalign test successful!\n");
    return 0;
}
#endif

#ifdef HAVE_MSG_COMP_PACKED
static int test_comprehensive_packed() {
    printf("--- Running test_comprehensive_packed ---\n");
    const double tol_int = 0.5;
    const uint8_t known_data[4] = { 0x55, 0xA9, 0xFA, 0xFF };
    const uint8_t zero_data[4] = { 0x00, 0x00, 0x00, 0x00 };
    const uint8_t ones_data[4] = { 0xFF, 0xFF, 0xFF, 0xFF };

    MSG_COMP_PACKED_t rx = {0};
    if (!MSG_COMP_PACKED_decode(&rx, known_data, 4)) {
        printf("FAIL: MSG_COMP_PACKED decode from known bytes failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_PACKED decode from known bytes\n");

    if (assert_close_f64("MSG_COMP_PACKED decode P_A", rx.P_A, 341.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_PACKED decode P_B", rx.P_B, 682.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_PACKED decode P_C", rx.P_C, 4095.0, tol_int)) return 1;

    MSG_COMP_PACKED_t tx = {0};
    tx.P_A = 341.0f;
    tx.P_B = 682.0f;
    tx.P_C = 4095.0f;

    uint8_t encoded[4] = {0};
    uint8_t out_dlc = 0;
    if (!MSG_COMP_PACKED_encode(encoded, &out_dlc, &tx)) {
        printf("FAIL: MSG_COMP_PACKED encode failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_PACKED encode from physical values\n");

    if (out_dlc != 4) {
        printf("FAIL: MSG_COMP_PACKED out_dlc got %u expected 4\n", (unsigned)out_dlc);
        return 1;
    }
    printf("PASS: MSG_COMP_PACKED out_dlc == 4\n");

    if (assert_equal_bytes("MSG_COMP_PACKED encode byte", encoded, known_data, 4)) return 1;

    MSG_COMP_PACKED_t rx_zero = {0};
    if (!MSG_COMP_PACKED_decode(&rx_zero, zero_data, 4)) {
        printf("FAIL: MSG_COMP_PACKED decode zero payload failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_PACKED decode zero payload\n");

    if (assert_close_f64("MSG_COMP_PACKED zero P_A", rx_zero.P_A, 0.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_PACKED zero P_B", rx_zero.P_B, 0.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_PACKED zero P_C", rx_zero.P_C, 0.0, tol_int)) return 1;

    MSG_COMP_PACKED_t rx_ones = {0};
    if (!MSG_COMP_PACKED_decode(&rx_ones, ones_data, 4)) {
        printf("FAIL: MSG_COMP_PACKED decode ones payload failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_PACKED decode ones payload\n");

    if (assert_close_f64("MSG_COMP_PACKED ones P_A", rx_ones.P_A, 1023.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_PACKED ones P_B", rx_ones.P_B, 1023.0, tol_int)) return 1;
    if (assert_close_f64("MSG_COMP_PACKED ones P_C", rx_ones.P_C, 4095.0, tol_int)) return 1;

    printf("Comprehensive packed test successful!\n");
    return 0;
}
#endif

#ifdef HAVE_MSG_COMP_SCALE
static int test_comprehensive_scale() {
    printf("--- Running test_comprehensive_scale ---\n");
    const double tol_scaled = 1e-3;
    const uint8_t known_data[8] = { 0xD2, 0x04, 0x88, 0x13, 0x2E, 0xFB, 0x00, 0x00 };

    MSG_COMP_SCALE_t rx = {0};
    if (!MSG_COMP_SCALE_decode(&rx, known_data, 8)) {
        printf("FAIL: MSG_COMP_SCALE decode from known bytes failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_SCALE decode from known bytes\n");

    if (assert_close_f64("MSG_COMP_SCALE decode SC_NEG_OFF", rx.SC_NEG_OFF, 23.4, tol_scaled)) return 1;
    if (assert_close_f64("MSG_COMP_SCALE decode SC_LARGE", rx.SC_LARGE, 50000.0, tol_scaled)) return 1;
    if (assert_close_f64("MSG_COMP_SCALE decode SC_SMALL", rx.SC_SMALL, -1.234, tol_scaled)) return 1;

    MSG_COMP_SCALE_t tx = {0};
    tx.SC_NEG_OFF = 23.4f;
    tx.SC_LARGE = 50000.0f;
    tx.SC_SMALL = -1.234f;

    uint8_t encoded[8] = {0};
    uint8_t out_dlc = 0;
    if (!MSG_COMP_SCALE_encode(encoded, &out_dlc, &tx)) {
        printf("FAIL: MSG_COMP_SCALE encode failed\n");
        return 1;
    }
    printf("PASS: MSG_COMP_SCALE encode from physical values\n");

    if (out_dlc != 8) {
        printf("FAIL: MSG_COMP_SCALE out_dlc got %u expected 8\n", (unsigned)out_dlc);
        return 1;
    }
    printf("PASS: MSG_COMP_SCALE out_dlc == 8\n");

    if (assert_equal_bytes("MSG_COMP_SCALE encode byte", encoded, known_data, 8)) return 1;

    printf("Comprehensive scale test successful!\n");
    return 0;
}
#endif

#ifdef HAVE_FIXED_TEST
static int test_moto_lsb_basic() {
    printf("--- Running test_moto_lsb_basic ---\n");
    // This test expects BE 8-bit signal at start_bit=0 (LSB convention) to map as byte0 under LSB config
    // Under our generator with motorola_start_bit=lsb, the effective start bit should be translated correctly.
    uint8_t data[8] = {0};
    // We rely on generated LSB_TEST if present, otherwise we just validate the BE utils on 8-bit slot
    set_bits_be(data, 7, 8, 0x5A); // MSB-based start of byte0
    uint64_t v = get_bits_be(data, 7, 8);
    if ((v & 0xFF) != 0x5A) {
        printf("Moto LSB basic mismatch: got 0x%02llX exp 0x5A\n", (unsigned long long)v);
        return 1;
    }
    printf("Moto LSB basic test successful!\n");
    return 0;
}
#endif
#ifdef HAVE_VT_MSG
static int test_value_table() {
    printf("--- Running test_value_table ---\n");
    VT_MSG_t tx = {0};
    tx.Mode = 0.0f; // State active
    tx.Base = 1.0f;
    tx.State = 2.0f; // STOP
    uint8_t data[8] = {0};
    uint8_t dlc = 0;
    if (!VT_MSG_encode(data, &dlc, &tx)) { printf("VT_MSG encode failed\n"); return 1; }
    VT_MSG_t rx = {0};
    if (!VT_MSG_decode(&rx, data, dlc)) { printf("VT_MSG decode failed\n"); return 1; }
    const char* s = VT_MSG_State_to_string((int)rx.State);
    if (strcmp(s, "STOP") != 0) { printf("State_to_string mismatch: %s\n", s); return 1; }
    const char* u = VT_MSG_Mode_to_string(99);
    if (strcmp(u, "UNKNOWN") != 0) { printf("Mode_to_string unknown mismatch: %s\n", u); return 1; }
    printf("Value table test successful!\n");
    return 0;
}
#endif

#ifdef HAVE_LSB_TEST
static int test_moto_lsb_roundtrip() {
    printf("--- Running test_moto_lsb_roundtrip ---\n");
    LSB_TEST_t tx = {0};
    tx.LSB_BE_8 = 90.0f; // expect raw 0x5A
    uint8_t data[8] = {0};
    uint8_t dlc = 0;
    if (!LSB_TEST_encode(data, &dlc, &tx)) { printf("LSB_TEST encode failed\n"); return 1; }
    if (data[0] != 0x5A) { printf("Byte0 mismatch after encode: 0x%02X exp 0x5A\n", data[0]); return 1; }
    LSB_TEST_t rx = {0};
    if (!LSB_TEST_decode(&rx, data, dlc)) { printf("LSB_TEST decode failed\n"); return 1; }
    if (fabs(rx.LSB_BE_8 - 90.0) > 1e-6) { printf("LSB_TEST value mismatch: got %f exp 90.0\n", rx.LSB_BE_8); return 1; }
    printf("Moto LSB roundtrip successful!\n");
    return 0;
}
#endif

#ifdef HAVE_MUX_MSG
static int test_multiplex_roundtrip() {
    printf("--- Running test_multiplex_roundtrip ---\n");
    MUX_MSG_t tx = {0};
    tx.MuxSwitch = 1.0f; // branch m1
    tx.Base_8 = 0xAA;
    tx.Sig_m1 = 0x55;
    uint8_t data[8] = {0};
    uint8_t dlc = 0;
    if (!MUX_MSG_encode(data, &dlc, &tx)) { printf("MUX_MSG encode failed\n"); return 1; }
    MUX_MSG_t rx = {0};
    if (!MUX_MSG_decode(&rx, data, dlc)) { printf("MUX_MSG decode failed\n"); return 1; }
    if ((int)rx.MuxSwitch != 1) { printf("Switch mismatch: %d\n", (int)rx.MuxSwitch); return 1; }
    if ((int)rx.Base_8 != 0xAA) { printf("Base mismatch: %d\n", (int)rx.Base_8); return 1; }
    if ((int)rx.Sig_m1 != 0x55) { printf("m1 mismatch: %d\n", (int)rx.Sig_m1); return 1; }
    // Switch to branch m2
    memset(&tx, 0, sizeof tx);
    tx.MuxSwitch = 2.0f;
    tx.Base_8 = 0x11;
    tx.Sig_m2 = 0xBEEF;
    memset(data, 0, sizeof data);
    if (!MUX_MSG_encode(data, &dlc, &tx)) { printf("MUX_MSG encode2 failed\n"); return 1; }
    memset(&rx, 0, sizeof rx);
    if (!MUX_MSG_decode(&rx, data, dlc)) { printf("MUX_MSG decode2 failed\n"); return 1; }
    if ((int)rx.MuxSwitch != 2) { printf("Switch2 mismatch: %d\n", (int)rx.MuxSwitch); return 1; }
    if ((int)rx.Base_8 != 0x11) { printf("Base2 mismatch: %d\n", (int)rx.Base_8); return 1; }
    if ((int)rx.Sig_m2 != 0xBEEF) { printf("m2 mismatch: %d\n", (int)rx.Sig_m2); return 1; }
    printf("Multiplex roundtrip successful!\n");
    return 0;
}
#endif
#ifdef HAVE_MESSAGE_1
static int test_fixed_roundtrip() {
    printf("--- Running test_fixed_roundtrip ---\n");
    MESSAGE_1_t msg_tx = { .Signal_1 = 123.0, .Signal_2 = 45.67 };
    uint8_t data[8];
    uint8_t dlc = 0;

    if (!MESSAGE_1_encode(data, &dlc, &msg_tx)) {
        printf("Encode failed in fixed test\n");
        return 1;
    }

    MESSAGE_1_t msg_rx = {0};
    if (!MESSAGE_1_decode(&msg_rx, data, dlc)) {
        printf("Decode failed in fixed test\n");
        return 1;
    }

    // Signal_1 factor=1
    if (fabs(msg_rx.Signal_1 - 123.0) > 1e-6) {
        printf("Fixed S1 mismatch: %f vs 123.0\n", msg_rx.Signal_1);
        return 1;
    }

    // Signal_2 factor=0.1 -> quantized to nearest 0.1
    double expected_s2 = floor(45.67 * 10.0 + 0.5) / 10.0;
    if (fabs(msg_rx.Signal_2 - expected_s2) > 1e-6) {
        printf("Fixed S2 mismatch: got %f exp %f\n", msg_rx.Signal_2, expected_s2);
        return 1;
    }

    printf("Fixed roundtrip successful!\n");
    return 0;
}
#endif

#ifdef HAVE_FIXED_TEST
static int test_fixed_suite_roundtrip() {
    printf("--- Running test_fixed_suite_roundtrip ---\n");
    FIXED_TEST_t tx = {0};
    // Sig01_LE_001: 0.01 scaling, try 123.45
    // Sig02_LE_0001_S: signed 0.001 scaling with offset -1, try -0.123
    // Sig03_BE_001: BE 0.01 scaling, try 12.34
    tx.Sig01_LE_001 = 123.45f;
    tx.Sig02_LE_0001_S = -0.123f;
    tx.Sig03_BE_001 = 12.34f;

    uint8_t data[8] = {0};
    uint8_t dlc = 0;

    if (!FIXED_TEST_encode(data, &dlc, &tx)) {
        printf("FIXED_TEST encode failed\n");
        return 1;
    }

    FIXED_TEST_t rx = {0};
    if (!FIXED_TEST_decode(&rx, data, dlc)) {
        printf("FIXED_TEST decode failed\n");
        return 1;
    }

    double exp1 = floor(123.45 * 100.0 + 0.5) / 100.0;
    double exp2 = floor((-0.123 - (-1.0)) * 1000.0 + 0.5) / 1000.0 + (-1.0);
    double exp3 = floor(12.34 * 100.0 + 0.5) / 100.0;

    const double tol = 1e-5;
    if (fabs(rx.Sig01_LE_001 - exp1) > tol) { printf("Sig01 mismatch: got %f exp %f\n", rx.Sig01_LE_001, exp1); return 1; }
    if (fabs(rx.Sig02_LE_0001_S - exp2) > tol) { printf("Sig02 mismatch: got %f exp %f\n", rx.Sig02_LE_0001_S, exp2); return 1; }
    if (fabs(rx.Sig03_BE_001 - exp3) > tol) { printf("Sig03 mismatch: got %f exp %f\n", rx.Sig03_BE_001, exp3); return 1; }

    printf("FIXED_TEST roundtrip successful!\n");
    return 0;
}
#endif

#ifdef HAVE_EXT_DISPATCH
static int test_dispatch_external() {
    printf("--- Running test_dispatch_external ---\n");
    uint8_t data[8] = {0};
    C2_MSG0280A1_BMS2VCU_Sts1_t out = {0};
    if (!decode_message(164001u, data, 8, &out)) {
        printf("Dispatch external failed for ID 164001\n");
        return 1;
    }
    printf("Dispatch external successful for ID 164001\n");
    return 0;
}

static int test_dispatch_external_multi() {
    printf("--- Running test_dispatch_external_multi ---\n");
    uint8_t data[8] = {0};

    C2_MSG0280A1_BMS2VCU_Sts1_t m1 = {0};
    if (!decode_message(164001u, data, 8, &m1)) { printf("Failed ID 164001\n"); return 1; }

    C2_MSG0580A1_BMS2VCU_Sts6_t m2 = {0};
    if (!decode_message(360609u, data, 8, &m2)) { printf("Failed ID 360609\n"); return 1; }

    C2_MSG1280A1_BMS2VCU2_t m3 = {0};
    if (!decode_message(1212577u, data, 8, &m3)) { printf("Failed ID 1212577\n"); return 1; }

    printf("Multi external dispatch successful (3 IDs)\n");
    return 0;
}
#endif

#ifdef HAVE_FD_MSG
static int test_fd_roundtrip() {
    printf("--- Running test_fd_roundtrip ---\n");
    FD_MSG_t tx = {0};
    tx.FD_Sig_Low = 0xAB;
    tx.FD_Sig_High = 1234.5f;
    tx.FD_Sig_Mid_BE = 0xCD;

    uint8_t data[64] = {0};
    uint8_t dlc = 0;

    if (!FD_MSG_encode(data, &dlc, &tx)) {
        printf("FD_MSG encode failed\n");
        return 1;
    }

    if (dlc != 64) {
        printf("FD DLC mismatch: got %d expected 64\n", dlc);
        return 1;
    }

    // Verify low byte is at position 0
    if (data[0] != 0xAB) {
        printf("FD_Sig_Low byte mismatch: got 0x%02X expected 0xAB\n", data[0]);
        return 1;
    }

    FD_MSG_t rx = {0};
    if (!FD_MSG_decode(&rx, data, dlc)) {
        printf("FD_MSG decode failed\n");
        return 1;
    }

    if (fabs(rx.FD_Sig_Low - 0xAB) > 1e-6) {
        printf("FD_Sig_Low mismatch: got %f expected 171\n", rx.FD_Sig_Low);
        return 1;
    }
    // FD_Sig_High factor=0.1 → quantized to nearest 0.1
    double expected_high = floor(1234.5 * 10.0 + 0.5) / 10.0;
    if (fabs(rx.FD_Sig_High - expected_high) > 0.05) {
        printf("FD_Sig_High mismatch: got %f expected %f\n", rx.FD_Sig_High, expected_high);
        return 1;
    }
    if (fabs(rx.FD_Sig_Mid_BE - 0xCD) > 1e-6) {
        printf("FD_Sig_Mid_BE mismatch: got %f expected 205\n", rx.FD_Sig_Mid_BE);
        return 1;
    }

    printf("CAN FD roundtrip successful! (DLC=%d, data[0]=0x%02X)\n", dlc, data[0]);
    return 0;
}
#endif

int main(int argc, char *argv[]) {
    if (argc < 2) {
        printf("Usage: %s <test_name>\n", argv[0]);
        return 1;
    }
#ifdef HAVE_MESSAGE_1
    if (strcmp(argv[1], "test_roundtrip") == 0) {
        return test_roundtrip();
    } else if (strcmp(argv[1], "test_range_check") == 0) {
        return test_range_check();
    } else if (strcmp(argv[1], "test_dispatch") == 0) {
        return test_dispatch();
    }
#endif
    if (strcmp(argv[1], "test_crc_counter") == 0) {
        return test_crc_counter();
    } else if (strcmp(argv[1], "test_be_basic") == 0) {
        return test_be_basic();
    } else if (strcmp(argv[1], "test_dlc_mapping") == 0) {
        return test_dlc_mapping();
    }
#ifdef HAVE_MSG_COMP_LE
    else if (strcmp(argv[1], "test_comprehensive_le") == 0) {
        return test_comprehensive_le();
    }
#endif
#ifdef HAVE_MSG_COMP_BE
    else if (strcmp(argv[1], "test_comprehensive_be") == 0) {
        return test_comprehensive_be();
    }
#endif
#ifdef HAVE_MSG_COMP_SIGNED
    else if (strcmp(argv[1], "test_comprehensive_signed") == 0) {
        return test_comprehensive_signed();
    }
#endif
#ifdef HAVE_MSG_COMP_NONALIGN
    else if (strcmp(argv[1], "test_comprehensive_nonalign") == 0) {
        return test_comprehensive_nonalign();
    }
#endif
#ifdef HAVE_MSG_COMP_PACKED
    else if (strcmp(argv[1], "test_comprehensive_packed") == 0) {
        return test_comprehensive_packed();
    }
#endif
#ifdef HAVE_MSG_COMP_SCALE
    else if (strcmp(argv[1], "test_comprehensive_scale") == 0) {
        return test_comprehensive_scale();
    }
#endif
#ifdef HAVE_FIXED_TEST
    else if (strcmp(argv[1], "test_moto_lsb_basic") == 0) {
        return test_moto_lsb_basic();
    }
#endif
    #ifdef HAVE_LSB_TEST
        else if (strcmp(argv[1], "test_moto_lsb_roundtrip") == 0) {
            return test_moto_lsb_roundtrip();
        }
    #endif
#ifdef HAVE_MUX_MSG
    else if (strcmp(argv[1], "test_multiplex_roundtrip") == 0) {
        return test_multiplex_roundtrip();
    }
#endif
#ifdef HAVE_VT_MSG
    else if (strcmp(argv[1], "test_value_table") == 0) {
        return test_value_table();
    }
#endif
#ifdef HAVE_FD_MSG
    else if (strcmp(argv[1], "test_fd_roundtrip") == 0) {
        return test_fd_roundtrip();
    }
#endif
#ifdef HAVE_MESSAGE_1
    else if (strcmp(argv[1], "test_fixed_roundtrip") == 0) {
        return test_fixed_roundtrip();
    }
#endif
#ifdef HAVE_FIXED_TEST
    else if (strcmp(argv[1], "test_fixed_suite_roundtrip") == 0) {
        return test_fixed_suite_roundtrip();
    }
#endif
#ifdef HAVE_EXT_DISPATCH
    else if (strcmp(argv[1], "test_dispatch_external") == 0) {
        return test_dispatch_external();
    } else if (strcmp(argv[1], "test_dispatch_external_multi") == 0) {
        return test_dispatch_external_multi();
    }
#endif
    #ifdef HAVE_STRESS
    else if (strcmp(argv[1], "test_stress_suite") == 0) {
        return test_stress_suite();
    }
    #endif
    else {
        printf("Unknown or unavailable test: %s\n", argv[1]);
        return 1;
    }
}
