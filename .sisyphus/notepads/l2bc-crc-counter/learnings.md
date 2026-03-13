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
