## 📝 작업 요약
- B-O1 범위 체크 보정 작업으로 `Codegen.fs`에 raw-range sentinel 휴리스틱(`isRawRangeSentinel`)을 추가하고, decode/encode/mux encode의 3개 range-check 생성 지점에 동일하게 적용했다.
- `CodegenTests.fs` 말미에 `mkSignalWithRange` 헬퍼와 9개 테스트(단위 8 + 통합 1)를 추가해 sentinel skip/유지 동작을 케이스별로 검증했다.

## 🛠 변경 상세
- 수정: `src/Signal.CANdy.Core/Codegen.fs`
  - `module Utils =` 내부에 `isRawRangeSentinel` 함수 추가.
  - `genDecodeForSignal`의 `| Some minV, Some maxV ->` 분기에서 `elif Utils.isRawRangeSentinel ...` 조건을 추가해 sentinel 패턴이면 range check 생성을 생략.
  - `genEncodeForSignal`의 동일 분기에 동일한 `elif` 추가.
  - mux switch encode(`sw`)의 동일 분기에 동일한 `elif` 추가.
  - 구현 중 전체 테스트 호환성을 위해 sentinel 판별을 raw-like 패턴과 물리 범위 불일치의 결합 조건으로 보강.
- 수정: `tests/Signal.CANdy.Core.Tests/CodegenTests.fs`
  - `mkSignalWithRange` 헬퍼 추가.
  - 신규 테스트 9개 추가:
    - Chrysler LAT_DIST sentinel skip
    - Mercedes offset sentinel skip
    - NORMAL_C range-check 유지
    - IDENTITY_D range-check 유지
    - NARROW_E range-check 유지
    - FORD_F range-check 유지
    - SIGNED_G sentinel skip
    - SIGNED_H range-check 유지
    - 혼합 메시지에서 신호별 selective skip 통합 테스트
- 수정(작업 노트): `.sisyphus/notepads/b-o1-raw-range-heuristic/learnings.md`
  - 구현 중 관찰된 false-positive/회귀 방지 조건을 append 기록.

## ✅ 테스트 결과
- `dotnet test --configuration Release --filter "DisplayName~Raw range" -v minimal --nologo`
  - RED 단계 확인: 신규 sentinel skip 테스트 4건 실패(의도된 실패) 후 구현 진행.
- `dotnet test --configuration Release -v minimal --nologo`
  - 최종 GREEN: 실패 0, 통과 100 (Signal.CANdy.Core.Tests 73 + Generator.Tests 27).
- `dotnet build --configuration Release --nologo`
  - 경고 0, 오류 0.
- LSP 진단
  - `src/Signal.CANdy.Core/Codegen.fs`: No diagnostics found
  - `tests/Signal.CANdy.Core.Tests/CodegenTests.fs`: No diagnostics found

## ⏭ 다음 계획
- B-O1 후속으로 실제 DBC corpus에서 sentinel 오탐/미탐 사례를 추가 수집해 heuristic 경계값을 보강한다.
- 현재 세션에서는 ROADMAP 체크박스를 직접 수정하지 않았다(오케스트레이터 관리 대상).
