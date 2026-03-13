## 📝 작업 요약
RUN_ID: 20260312-1235

- Bug #4 수정: DBC `[0|0]` no-range sentinel(`Minimum=Some 0.0`, `Maximum=Some 0.0`)인 신호에서 encode/decode range check 가드를 생성하지 않도록 Codegen 로직을 보완했다.
- TDD 순서(RED -> GREEN -> REFACTOR)로 진행했으며, 신규 테스트로 회귀를 고정했다.

## 🛠 변경 상세
- `tests/Signal.CANdy.Core.Tests/CodegenTests.fs`
  - 신규 테스트 `Range check skipped for DBC no-range sentinel 0 0` 추가.
  - `[0|0]` sentinel 신호를 포함한 IR 생성 후 `test_msg.c`를 검사해 `TestSig < 0` / `TestSig > 0` 가드 미생성을 검증.
- `src/Signal.CANdy.Core/Codegen.fs`
  - decode range check 분기(`Some minV, Some maxV`)에 sentinel guard 추가: `if minV = 0.0 && maxV = 0.0 then None`.
  - encode range check 분기(`Some minV, Some maxV`)에 동일 guard 추가.
  - mux switch encode range check 분기(`Some minV, Some maxV`)에 동일 guard 추가.

## ✅ 테스트 결과
- RED 확인:
  - 신규 테스트 추가 직후 `dotnet test --configuration Release -v minimal --nologo` 실행 결과, 신규 테스트가 의도대로 실패(생성 코드에 `if (msg->TestSig < 0 || msg->TestSig > 0)` 존재)함을 확인.
- GREEN 확인:
  - `dotnet test --configuration Release -v minimal --nologo` 통과 (총 90 passed, 0 failed).
  - `dotnet build --configuration Release --nologo` 통과 (0 warnings, 0 errors).
  - changed files LSP diagnostics clean 확인:
    - `src/Signal.CANdy.Core/Codegen.fs`
    - `tests/Signal.CANdy.Core.Tests/CodegenTests.fs`

## ⏭ 다음 계획
- oracle failure-resolution 플랜의 잔여 bucket(특히 message-level encode/decode failure) 우선순위 분석을 이어간다.
- 선행 조건:
  - 본 커밋(`5915b8f`) 기준으로 oracle 재실행 시 pass-rate 변화를 수집하고, Bug #4 영향 범위를 벤더 DBC별로 재집계한다.
  - ROADMAP 체크박스 갱신 대상 항목은 본 세션 범위에서 별도 명시된 항목이 없어 변경하지 않음.
