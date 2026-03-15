# 📝 작업 요약

- `tests/Signal.CANdy.Core.Tests/CodegenTests.fs`에 `>64` valid bitmask byte-array 기능용 RED 단계 테스트(T1–T6)를 추가/보강했다.
- 기존 65-signal UnsupportedFeature 테스트를 성공 기대 테스트로 전환했고, 128/72/1024/1025/조건부 utils emission/비-mux 100-signal 시나리오를 추가해 현재 구현 한계를 명시적으로 깨뜨리도록 만들었다.
- 요청된 로컬 커밋 `e0e788a` (`test(codegen): add failing tests for >64 valid byte-array`)를 생성했다.

# 🛠 변경 상세

- 수정 파일: `tests/Signal.CANdy.Core.Tests/CodegenTests.fs`
  - 기존 `valid bitmask uses uint64_t for 64-signal mux message` 테스트에 `uint8_t` 배열이 나오지 않아야 함 / `1ULL` shift 사용 확인 assertion을 추가했다.
  - 기존 65-signal mux valid bitmask 테스트를 `valid bitmask uses uint8_t byte array for 65-signal mux message`로 리팩터링했다.
    - 실제 temp output directory 생성/정리 패턴으로 변경.
    - header/source에 대해 `uint8_t valid[9]`, `VALID_BYTES`, plain index macro, `sc_utils.h`, `memset`, `sc_valid_set` 기반 동작을 기대하도록 작성.
  - 신규 테스트 추가:
    - `valid bitmask uses uint8_t byte array for 128-signal mux message`
    - `utils header emits sc_valid helpers only when a message has more than 64 mux signals`
    - `codegen fails with UnsupportedFeature for 1025-signal mux message`
    - `valid bitmask uses uint8_t byte array for 72-signal mux message`
    - `valid bitmask uses uint8_t byte array for 1024-signal mux message`
  - 기존 non-mux 다신호 테스트를 `non-mux message with 100 signals has no valid field`로 확장했다.
- 추가 확인:
  - `lsp_diagnostics` 기준 변경 파일 컴파일 진단 오류 없음.

# ✅ 테스트 결과

- 실행 명령: `dotnet test --configuration Release -v minimal --nologo`
- 결과:
  - 빌드/테스트 실행 자체는 성공적으로 시작되었고 테스트 프로젝트 컴파일 오류는 없었다.
  - `Signal.CANdy.Core.Tests`에서 총 6개 실패, 124개 통과.
  - 실패한 신규/변경 테스트:
    - `valid bitmask uses uint8_t byte array for 65-signal mux message`
    - `valid bitmask uses uint8_t byte array for 128-signal mux message`
    - `utils header emits sc_valid helpers only when a message has more than 64 mux signals`
    - `codegen fails with UnsupportedFeature for 1025-signal mux message`
    - `valid bitmask uses uint8_t byte array for 72-signal mux message`
    - `valid bitmask uses uint8_t byte array for 1024-signal mux message`
  - 실패 원인 요약:
    - 현재 구현이 `>64` mux valid bitmask를 여전히 `UnsupportedFeature`로 처리한다.
    - 1025-signal guard 메시지도 새 기대치(`1024`)와 불일치한다.
  - 이는 RED phase 기대 결과와 일치한다.
- `Generator.Tests`: 27개 통과.

# ⏭ 다음 계획

- 활성 ROADMAP 항목: `Plans/ROADMAP.md`의 `1. valid 비트마스크 >64 신호 fallback 방식`.
- 다음 세션에서는 byte-array 기반 valid 표현(`uint8_t valid[N]`, helper emission, set/clear/test 호출, upper bound 정책)을 `src/Signal.CANdy.Core/Codegen.fs` 및 관련 템플릿에 구현하는 GREEN phase 작업이 필요하다.
- 선행 조건:
  - 이번 RED 테스트 기대치를 구현 설계와 정렬할 것.
  - 현재 작업에서 완료된 ROADMAP 체크박스는 없음.
