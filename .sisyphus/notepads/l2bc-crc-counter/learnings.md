20260313_0000_CRc_fix

📝 작업 요약
- Fixed tryAsMap to accept IDictionary<obj,obj> produced by YamlDotNet for nested maps.

🛠 변경 상세
- Modified src/Signal.CANdy.Core/Config.fs:
  - tryAsMap now converts IDictionary<obj,obj> -> IDictionary<string,obj> by extracting string keys
  - byte_range parsing now handles IDictionary<obj,obj> similarly

✅ 테스트 결과
- dotnet test (Release): Core 112 passed, Generator 27 passed (total 139/139)
- Verified CRC-related tests listed in task all pass.

⏭ 다음 계획
- No further changes required for this issue.

20260313_2230_Dbc_T12_blankline_fix: Removed blank lines after triple-quoted DBC literals in T12 tests (IsCrc/IsCounter/Non-CRC/CRC_OFF). Build+tests: OK.

20260313_2348_T14_codegen_crc_counter_tests: Added CodegenTests coverage for CRC/counter modes (validate/passthrough/fail_fast), CRC mismatch/encode paths, counter rollover helper emission, and utils CRC helper parity/no-parity checks.

20260313_2018_T15_api_facade_crc_tests: `Api.generateFromPaths` and `GeneratorFacade.GenerateFromPathsAsync` currently surface CRC/Counter validation failures from YAML/config validation (`UnknownAlgorithm`, `ConfigConflict`) before codegen; missing-message/signal references in `crc_counter.mode=validate` are not rejected by the current pipeline because `applyConfigMetadata` ignores unmatched message/signal names.

20260313_2025_T16_c_build_verification: CRC validate codegen emits the expected helper surface (`sc_crc8_sae_j1850`, CRC table, `MESSAGE_1_check_counter`), so the validate path reaches template emission; the current blocker is C compile correctness in generated encode code, not missing CRC symbol generation.
