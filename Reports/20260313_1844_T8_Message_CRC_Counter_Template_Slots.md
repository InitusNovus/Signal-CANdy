## 📝 작업 요약

- T8 수행: `templates/message.h.scriban`, `templates/message.c.scriban`, `src/Signal.CANdy.Core/Codegen.fs`에 CRC verify/insert 및 counter check용 템플릿 슬롯을 추가하고, 모든 새 모델 값을 `false` / `""` 기본값으로 고정했다.
- 첫 구현에서 헤더 golden parity가 추가 blank line 때문에 깨지는 것을 확인했고, Scriban 조건부를 인라인 배치로 조정해 비CRC/비counter 출력이 기존과 byte-identical 하도록 복구했다.

## 🛠 변경 상세

- `templates/message.h.scriban`
  - `has_counter`가 `true`일 때만 `counter_state_type_decl`, `counter_check_func_decl`를 내보내는 블록을 `encode` 선언 뒤에 추가했다.
  - `has_counter = false` 기본 경로에서 불필요한 공백 줄이 생기지 않도록 조건부 시작/종료 위치를 조정했다.
- `templates/message.c.scriban`
  - decode 함수에서 `signal_decode_c` 뒤, `return true;` 앞에 `crc_decode_check` 주입 슬롯을 추가했다.
  - encode 함수에서 `signal_encode_c` 뒤, `return true;` 앞에 `crc_encode_insert` 주입 슬롯을 추가했다.
  - `has_counter`가 `true`일 때만 파일 끝에 `counter_check_func_impl`를 내보내도록 조건부 블록을 추가했다.
  - 빈 문자열 기본값일 때 출력이 변하지 않도록 세 조건부 모두 인라인 Scriban 패턴으로 배치했다.
- `src/Signal.CANdy.Core/Codegen.fs`
  - `Message.generateMessageFiles` 헤더 모델에 `"has_counter"`, `"counter_state_type_decl"`, `"counter_check_func_decl"` 기본 엔트리를 추가했다.
  - 소스 모델에 `"crc_decode_check"`, `"crc_encode_insert"`, `"has_counter"`, `"counter_check_func_impl"` 기본 엔트리를 추가했다.
- `.sisyphus/notepads/l2bc-crc-counter/learnings.md`
  - T8에서 확인한 message 템플릿 parity 유지 패턴과 안전한 모델 wiring 메모를 append 했다.

## ✅ 테스트 결과

- `lsp_diagnostics` on `src/Signal.CANdy.Core/Codegen.fs` -> diagnostics 0.
- `lsp_diagnostics` on `templates/message.h.scriban` / `templates/message.c.scriban` -> `.scriban`용 LSP 서버 미구성으로 실행 불가.
- `dotnet build --configuration Release --nologo` -> 성공, 0 errors / 4 warnings.
  - 경고 위치: `src/Signal.CANdy/Library.fs:50`, `src/Signal.CANdy/Library.fs:122`, `src/Signal.CANdy.CLI/Program.fs:342`, `src/Generator/Program.fs:128`
  - 내용: `ByteRangeExceedsDlc` 패턴 매치 누락 경고(FS0025), 이번 변경과 무관한 기존 경고 유지.
- `dotnet test --configuration Release -v minimal --nologo` -> 116/116 PASS.
  - `Signal.CANdy.Core.Tests`: 89 PASS
  - `Generator.Tests`: 27 PASS
- 중간 검증:
  - 첫 테스트 실행에서 header golden output 3건이 추가 blank line 때문에 실패함을 확인.
  - `message.h.scriban` / `message.c.scriban` 조건부 줄바꿈 배치를 수정한 뒤 전체 테스트 PASS를 재확인했다.

## ⏭ 다음 계획

- 다음 세션은 T10에서 이 슬롯들에 실제 IR 기반 CRC/counter 렌더링 값을 연결하고, 필요 시 함수 시그니처 확장을 진행하면 된다.
- `.sisyphus/plans/l2bc-crc-counter.md`는 오케스트레이터 전용 read-only 파일 규칙에 따라 이번 세션에서 수정하지 않았다.
