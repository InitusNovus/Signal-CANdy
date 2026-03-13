## 작업 요약

- T16 C-level verification only 수행.
- `examples/crc_test.dbc`와 `examples/config_crc_t16.yaml`을 새로 만들어 CRC validate 경로로 C 코드를 생성했고, `make -C gen build`까지 검증했다.
- 생성은 성공했지만 C 컴파일은 실패했다. 실패 원인은 생성된 `gen/src/message_1.c`에서 `const MESSAGE_1_t* msg`에 대해 `msg->CHECKSUM`를 대입한 코드다.

## 변경 상세

- 생성/추가 파일:
  - `examples/crc_test.dbc`
  - `examples/config_crc_t16.yaml`
  - `.sisyphus/evidence/task-16-c-build.txt`
  - `Reports/20260313_2025_T16_crc_c_build_verification.md`
- 생성 산출물 갱신:
  - `gen/include/*`
  - `gen/src/*`
- Notepad append:
  - `.sisyphus/notepads/l2bc-crc-counter/learnings.md`
  - `.sisyphus/notepads/l2bc-crc-counter/issues.md`
  - `.sisyphus/notepads/l2bc-crc-counter/decisions.md`
- F# 소스(`*.fs`, `*.fsproj`)는 수정하지 않았다.

## 테스트 결과

- Code generation:
  - `dotnet run --project src/Signal.CANdy.CLI -- -d examples/crc_test.dbc -o gen -c examples/config_crc_t16.yaml -t`
  - 결과: 성공 (`Headers: 5, Sources: 3, Others: 0`)
- C build:
  - `make -C gen build`
  - 결과: 실패 (exit code 2)
  - 핵심 오류: `gen/src/message_1.c:43:19: error: assignment of member 'CHECKSUM' in read-only object`
- Spot-check:
  - `gen/include/sc_utils.h`에서 `sc_crc8_sae_j1850` 선언 확인
  - `gen/src/sc_utils.c`에서 CRC table/function 생성 확인
  - `gen/src/message_1.c`에서 CRC decode/encode 호출 확인

## 다음 계획

- T16은 완료 처리 불가. `templates/message.c.scriban` 또는 대응 codegen 경로에서 CRC encode가 `msg->CHECKSUM`를 갱신하지 않고 `data` 버퍼에만 CRC 값을 쓰도록 수정해야 한다.
- 수정 후 동일 입력(`examples/crc_test.dbc`, `examples/config_crc_t16.yaml`)으로 다시 생성하고 `make -C gen build`를 재검증한다.
- ROADMAP 체크박스 업데이트는 하지 않았다.
