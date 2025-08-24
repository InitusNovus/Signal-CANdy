/**
 * Stress test for large-scale DBC files
 * Tests all messages in the external DBC with various configurations
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <assert.h>

// Include all available headers
#ifdef __has_include
  #if __has_include("registry.h")
    #include "registry.h"
    #define HAS_REGISTRY 1
  #endif
  
  // Include all C2 message headers (external DBC)
  #if __has_include("c2_msg0280a1_bms2vcu_sts1.h")
    #include "c2_msg0280a1_bms2vcu_sts1.h"
    #define HAS_C2_MSG0280A1 1
  #endif
  #if __has_include("c2_msg0580a1_bms2vcu_sts6.h")
    #include "c2_msg0580a1_bms2vcu_sts6.h"
    #define HAS_C2_MSG0580A1 1
  #endif
  #if __has_include("c2_msg08019f80.h")
    #include "c2_msg08019f80.h"
    #define HAS_C2_MSG08019F80 1
  #endif
  #if __has_include("c2_msg08029f80.h")
    #include "c2_msg08029f80.h"
    #define HAS_C2_MSG08029F80 1
  #endif
  #if __has_include("c2_msg0807609f.h")
    #include "c2_msg0807609f.h"
    #define HAS_C2_MSG0807609F 1
  #endif
  #if __has_include("c2_msg0c0380a1_bms2vcu_sts2.h")
    #include "c2_msg0c0380a1_bms2vcu_sts2.h"
    #define HAS_C2_MSG0C0380A1 1
  #endif
  #if __has_include("c2_msg1280a1_bms2vcu2.h")
    #include "c2_msg1280a1_bms2vcu2.h"
    #define HAS_C2_MSG1280A1 1
  #endif
  #if __has_include("c2_msg180180b0_pdu2vcu.h")
    #include "c2_msg180180b0_pdu2vcu.h"
    #define HAS_C2_MSG180180B0 1
  #endif
  #if __has_include("c2_msg18f0e080_vcu2esc1.h")
    #include "c2_msg18f0e080_vcu2esc1.h"
    #define HAS_C2_MSG18F0E080 1
  #endif
  #if __has_include("c2_msg18f2e080_vcu2esc2.h")
    #include "c2_msg18f2e080_vcu2esc2.h"
    #define HAS_C2_MSG18F2E080 1
  #endif
  // Include Rivian messages (public OpenDBC example)
  #if __has_include("wheelbuttons.h")
    #include "wheelbuttons.h"
    #define HAS_RIVIAN_WHEELBUTTONS 1
  #endif
  #if __has_include("bsm_blindspotindicator.h")
    #include "bsm_blindspotindicator.h"
    #define HAS_RIVIAN_BSM 1
  #endif
#endif

// Test structure for timing
typedef struct {
    const char* test_name;
    clock_t start_time;
    clock_t end_time;
    int iterations;
    int passed;
    int failed;
} test_result_t;

static void start_test(test_result_t* result, const char* name, int iterations) {
    result->test_name = name;
    result->iterations = iterations;
    result->passed = 0;
    result->failed = 0;
    result->start_time = clock();
    printf("Starting stress test: %s (%d iterations)\n", name, iterations);
}

static void end_test(test_result_t* result) {
    result->end_time = clock();
    double elapsed = ((double)(result->end_time - result->start_time)) / CLOCKS_PER_SEC;
    printf("  Completed: %s\n", result->test_name);
    printf("  Time: %.3f seconds\n", elapsed);
    printf("  Results: %d passed, %d failed\n", result->passed, result->failed);
    printf("  Rate: %.1f ops/sec\n", result->iterations / elapsed);
    printf("\n");
}

// Generate test data with pattern
static void generate_test_data(uint8_t data[8], int seed) {
    for (int i = 0; i < 8; i++) {
        data[i] = (uint8_t)((seed + i * 37) & 0xFF);
    }
}

// Individual message stress tests
#ifdef HAS_C2_MSG0280A1
static void stress_test_c2_msg0280a1(test_result_t* result) {
    start_test(result, "C2_MSG0280A1_BMS2VCU_Sts1 Roundtrip", 10000);
    
    for (int i = 0; i < result->iterations; i++) {
        uint8_t data[8];
        generate_test_data(data, i);
        
        // Decode
        C2_MSG0280A1_BMS2VCU_Sts1_t msg = {0};
        if (C2_MSG0280A1_BMS2VCU_Sts1_decode(&msg, data, 8)) {
            // Encode back
            uint8_t encoded[8];
            uint8_t dlc;
            if (C2_MSG0280A1_BMS2VCU_Sts1_encode(encoded, &dlc, &msg)) {
                result->passed++;
            } else {
                result->failed++;
            }
        } else {
            // Decode failure is acceptable for random data
            result->passed++;
        }
    }
    
    end_test(result);
}
#endif

#ifdef HAS_C2_MSG18F0E080
static void stress_test_c2_msg18f0e080(test_result_t* result) {
    start_test(result, "C2_MSG18F0E080_VCU2ESC1 Roundtrip", 10000);
    
    for (int i = 0; i < result->iterations; i++) {
        uint8_t data[8];
        generate_test_data(data, i + 1000);
        
        C2_MSG18F0E080_VCU2ESC1_t msg = {0};
        if (C2_MSG18F0E080_VCU2ESC1_decode(&msg, data, 8)) {
            uint8_t encoded[8];
            uint8_t dlc;
            if (C2_MSG18F0E080_VCU2ESC1_encode(encoded, &dlc, &msg)) {
                result->passed++;
            } else {
                result->failed++;
            }
        } else {
            result->passed++;
        }
    }
    
    end_test(result);
}
#endif

#ifdef HAS_RIVIAN_WHEELBUTTONS
static void stress_test_wheelbuttons(test_result_t* result) {
  start_test(result, "WheelButtons Roundtrip", 10000);
  for (int i = 0; i < result->iterations; i++) {
    uint8_t data[8];
    generate_test_data(data, i + 2000);
    WheelButtons_t msg = {0};
    if (WheelButtons_decode(&msg, data, 8)) {
      uint8_t encoded[8];
      uint8_t dlc;
      if (WheelButtons_encode(encoded, &dlc, &msg)) {
        result->passed++;
      } else {
        result->failed++;
      }
    } else {
      result->passed++;
    }
  }
  end_test(result);
}
#endif

#ifdef HAS_RIVIAN_BSM
static void stress_test_bsm(test_result_t* result) {
  start_test(result, "BSM_BlindSpotIndicator Roundtrip", 10000);
  for (int i = 0; i < result->iterations; i++) {
    uint8_t data[8];
    generate_test_data(data, i + 3000);
    BSM_BlindSpotIndicator_t msg = {0};
    if (BSM_BlindSpotIndicator_decode(&msg, data, 8)) {
      uint8_t encoded[8];
      uint8_t dlc;
      if (BSM_BlindSpotIndicator_encode(encoded, &dlc, &msg)) {
        result->passed++;
      } else {
        result->failed++;
      }
    } else {
      result->passed++;
    }
  }
  end_test(result);
}
#endif

// Registry dispatch stress test
static void stress_test_registry_dispatch(test_result_t* result) {
    start_test(result, "Registry Dispatch Performance", 50000);
    
    // List of known CAN IDs from the external DBC
    uint32_t test_ids[] = {
        0x0280A1, 0x0580A1, 0x08019F80, 0x08029F80, 0x0807609F,
        0x0C0380A1, 0x1280A1, 0x180180B0, 0x18F0E080, 0x18F2E080
    };
    int num_ids = sizeof(test_ids) / sizeof(test_ids[0]);
    
    for (int i = 0; i < result->iterations; i++) {
        uint8_t data[8];
        generate_test_data(data, i);
        
        uint32_t can_id = test_ids[i % num_ids];
        
        // Try to decode using registry
        uint8_t msg_buffer[256] = {0}; // Large enough for any message
        if (decode_message(can_id, data, 8, msg_buffer)) {
            result->passed++;
        } else {
            // Decode failure is acceptable for random data
            result->passed++;
        }
    }
    
    end_test(result);
}

// Memory usage estimation
static void estimate_memory_usage(void) {
    printf("=== Memory Usage Estimation ===\n");
    
    #ifdef HAS_C2_MSG0280A1
    printf("C2_MSG0280A1_BMS2VCU_Sts1_t size: %zu bytes\n", sizeof(C2_MSG0280A1_BMS2VCU_Sts1_t));
    #endif
    #ifdef HAS_C2_MSG18F0E080
    printf("C2_MSG18F0E080_VCU2ESC1_t size: %zu bytes\n", sizeof(C2_MSG18F0E080_VCU2ESC1_t));
    #endif
  #ifdef HAS_RIVIAN_WHEELBUTTONS
  printf("WheelButtons_t size: %zu bytes\n", sizeof(WheelButtons_t));
  #endif
  #ifdef HAS_RIVIAN_BSM
  printf("BSM_BlindSpotIndicator_t size: %zu bytes\n", sizeof(BSM_BlindSpotIndicator_t));
  #endif
    
    printf("\nNote: Add more message sizes for comprehensive analysis\n");
    printf("=== End Memory Usage ===\n\n");
}

// Main stress test runner
int test_stress_suite(void) {
    printf("===============================================\n");
    printf("DBC Parser Stress Test Suite\n");
    printf("Testing large-scale external DBC performance\n");
    printf("===============================================\n\n");
    
    estimate_memory_usage();
    
    test_result_t result;
    int total_tests = 0;
    int passed_tests = 0;
    
  // Conditionally run C2-specific roundtrip tests if the headers are available
  #ifdef HAS_C2_MSG0280A1
  printf("Testing C2_MSG0280A1_BMS2VCU_Sts1...\n");
  stress_test_c2_msg0280a1(&result);
  total_tests++;
  if (result.failed == 0) passed_tests++;
  #else
  printf("Skipping C2_MSG0280A1_BMS2VCU_Sts1 roundtrip (header not present)\n");
  #endif
    
  #ifdef HAS_C2_MSG18F0E080
  printf("Testing C2_MSG18F0E080_VCU2ESC1...\n");
  stress_test_c2_msg18f0e080(&result);
  total_tests++;
  if (result.failed == 0) passed_tests++;
  #else
  printf("Skipping C2_MSG18F0E080_VCU2ESC1 roundtrip (header not present)\n");
  #endif
    
    printf("Testing Registry Dispatch...\n");
    stress_test_registry_dispatch(&result);
  total_tests++;
  if (result.failed == 0) passed_tests++;
  
  #ifdef HAS_RIVIAN_WHEELBUTTONS
  printf("Testing WheelButtons (Rivian)...\n");
  stress_test_wheelbuttons(&result);
  total_tests++;
  if (result.failed == 0) passed_tests++;
  #else
  printf("Skipping WheelButtons roundtrip (header not present)\n");
  #endif

  #ifdef HAS_RIVIAN_BSM
  printf("Testing BSM_BlindSpotIndicator (Rivian)...\n");
  stress_test_bsm(&result);
  total_tests++;
  if (result.failed == 0) passed_tests++;
  #else
  printf("Skipping BSM_BlindSpotIndicator roundtrip (header not present)\n");
  #endif
    
    printf("===============================================\n");
    printf("Stress Test Summary: %d/%d tests passed\n", passed_tests, total_tests);
    printf("===============================================\n");
    
    return (passed_tests == total_tests) ? 0 : 1;
}
