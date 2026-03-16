# 📝 작업 요약

`examples/main.c`에 CAN 신호 파싱 검증용 C E2E 테스트 7종을 추가했다. 핵심은 라운드트립만 보는 방식이 아니라, 각 메시지별로 하드코딩된 바이트 패턴 기반 decode 검증과 physical 값 기반 encode 후 바이트 검증을 모두 수행하도록 확장한 것이다.

# 🛠 변경 상세

- 수정 파일: `examples/main.c`
- 상단 `__has_include` 블록에 아래 6개 헤더 가드 및 `HAVE_*` 매크로 추가
  - `msg_comp_le.h` / `HAVE_MSG_COMP_LE`
  - `msg_comp_be.h` / `HAVE_MSG_COMP_BE`
  - `msg_comp_signed.h` / `HAVE_MSG_COMP_SIGNED`
  - `msg_comp_nonalign.h` / `HAVE_MSG_COMP_NONALIGN`
  - `msg_comp_packed.h` / `HAVE_MSG_COMP_PACKED`
  - `msg_comp_scale.h` / `HAVE_MSG_COMP_SCALE`
- 공통 검증 헬퍼 추가
  - `assert_close_f64(...)`: `fabs` 기반 실수 허용오차 검증
  - `assert_equal_bytes(...)`: 바이트 배열 정확 일치 검증
- 신규 테스트 함수 추가
  - `test_dlc_mapping()` (비가드): CAN FD DLC<->LEN 경계/클램프 매핑 검증
  - `test_comprehensive_le()`
  - `test_comprehensive_be()`
  - `test_comprehensive_signed()` (음수/양수 케이스 모두)
  - `test_comprehensive_nonalign()`
  - `test_comprehensive_packed()` (known/zero/all-ones payload)
  - `test_comprehensive_scale()`
- `main()` 디스패치 엔트리 추가
  - `test_dlc_mapping`
  - `test_comprehensive_le`
  - `test_comprehensive_be`
  - `test_comprehensive_signed`
  - `test_comprehensive_nonalign`
  - `test_comprehensive_packed`
  - `test_comprehensive_scale`

# ✅ 테스트 결과

- LSP 진단
  - `lsp_diagnostics examples/main.c` 실행 시 환경 이슈로 실패
  - 오류: `clangd` 미설치 (`Binary 'clangd' not found on Windows`)
- 코드 생성
  - `dotnet run --project src/Signal.CANdy.CLI -- -d examples/comprehensive_test.dbc -o tmp_gen_comp -t` 성공
- C 빌드
  - `make` 미설치로 `make -C tmp_gen_comp build` 실패
  - 대체로 `gcc` 직접 컴파일 성공:
    - `mkdir -p tmp_gen_comp/build && gcc -std=c99 -Wall -Wextra -I tmp_gen_comp/include tmp_gen_comp/src/*.c -lm -o tmp_gen_comp/build/test_runner`
- 신규 테스트 실행
  - 통과: `test_dlc_mapping`, `test_comprehensive_le`, `test_comprehensive_be`, `test_comprehensive_nonalign`, `test_comprehensive_packed`
  - 실패:
    - `test_comprehensive_signed` (음수 decode 기대값과 불일치)
    - `test_comprehensive_scale` (`SC_SMALL` 음수 decode 기대값과 불일치)
  - 실패 로그 요약: signed decode 경로에서 음수가 매우 큰 양수로 해석되는 증상 재현

# ⏭ 다음 계획

- 우선순위 1: signed decode 경로(sign extension) 원인 분석 및 수정
  - 대상: 생성된 `msg_comp_signed.c`, `msg_comp_scale.c`의 signed raw 처리 로직
  - 선행 조건: 현재 재현 케이스(`test_comprehensive_signed`, `test_comprehensive_scale`) 유지
- 우선순위 2: `make`/`clangd` 개발 환경 보강
  - 선행 조건: Windows 개발 환경에 GNU Make, clangd 설치 및 PATH 반영
- ROADMAP 연계
  - 직접 완료 처리한 ROADMAP 체크박스는 없음 (`ROADMAP.md` 업데이트 없음)
  - 연관 항목: `L-4c`(CAN FD DLC 매핑 검증 강화 관점에서 추가 테스트 기반 확보)
