## 📝 작업 요약
- T10(Codegen.fs) 작업으로 CRC/Counter 가드 로직을 `mode` 기반으로 재작성하고, 템플릿 모델(`message.*`, `utils.*`)에 IR 기반 CRC/Counter 데이터를 연결했다.
- `validate` 모드에서 CRC 검증/삽입 코드가 생성되도록 하고, `fail_fast`/레거시 `crc_counter_check` 경로는 `UnsupportedFeature`로 명확히 차단되도록 정리했다.

## 🛠 변경 상세
- 수정: `src/Signal.CANdy.Core/Codegen.fs`
  - `Utils.utilsHContent`/`Utils.utilsCContent` 시그니처를 `(config, ir)`로 변경하고 `CrcMeta.Algorithm` 스캔으로 `has_crc_j1850`/`has_crc_8h2f`를 동적 계산.
  - `Message.generateMessageFiles`에서 `has_counter`, `counter_state_type_decl`, `counter_check_func_decl`, `crc_decode_check`, `crc_encode_insert`, `counter_check_func_impl`를 IR(`CrcMeta`, `CounterMeta`, `CrcCounterMode`) 기반으로 생성.
  - `generate` 내부 `crcCounterGuard`를 `config.CrcCounter.Mode` 분기(`fail_fast`/`validate|passthrough`/legacy fallback)로 교체.
  - `generate`에서 파일 생성 전 CRC 폭 검사(`meta.Params.Width <> 8`) 추가, 비지원 폭은 `CodeGenError.UnsupportedFeature` 반환.
  - utils 파일 생성 호출부를 `Utils.utilsHContent config ir` / `Utils.utilsCContent config ir`로 갱신.
- 추가 기록(작업 노트): `.sisyphus/notepads/l2bc-crc-counter/learnings.md`
  - T10 구현 시 패턴/주의점 append.

## ✅ 테스트 결과
- `dotnet build --configuration Release --nologo`
  - 결과: 성공(오류 0), 경고 4
  - 경고 위치: `src/Signal.CANdy/Library.fs`, `src/Signal.CANdy.CLI/Program.fs`, `src/Generator/Program.fs` (기존 패턴 매치 경고)
- `dotnet test --configuration Release -v minimal --nologo`
  - 결과: 전체 통과
  - `Signal.CANdy.Core.Tests`: 89/89 PASS
  - `Generator.Tests`: 27/27 PASS
  - 총 116 테스트 PASS

## ⏭ 다음 계획
- Orchestrator 플랜 기준 T10 완료 상태로 후속 항목 진행.
- 권장 후속 검증:
  1. `mode=validate` 샘플 YAML로 실제 생성 C 파일에서 CRC check/insert 코드 스팟 확인
  2. `mode=passthrough`/`mode=fail_fast` 조합 샘플로 에러/패스 분기 수동 스모크
