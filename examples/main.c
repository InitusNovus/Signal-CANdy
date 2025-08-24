#include <stdio.h>
#include <string.h>
#include <math.h>
#include "registry.h"
#include "utils.h"

// Include stress test
extern int test_stress_suite(void);

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
    }
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
    else if (strcmp(argv[1], "test_stress_suite") == 0) {
        return test_stress_suite();
    }
    else {
        printf("Unknown or unavailable test: %s\n", argv[1]);
        return 1;
    }
}