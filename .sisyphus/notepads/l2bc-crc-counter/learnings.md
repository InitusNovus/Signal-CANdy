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
