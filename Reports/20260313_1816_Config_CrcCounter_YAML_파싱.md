## 📝 작업 요약
- `src/Signal.CANdy.Core/Config.fs`에 CRC/Counter 설정 타입 4종(`CrcSignalConfig`, `CounterSignalConfig`, `CrcCounterMessageConfig`, `CrcCounterConfig`)을 추가하고, `Config` 레코드에 `CrcCounter: CrcCounterConfig option` 필드를 확장했다.
- `loadFromYaml`에서 `crc_counter` 블록(`mode`, `algorithms`, `messages.crc`, `messages.counter`)을 파싱하도록 구현했으며, 블록 미존재 시 `CrcCounter = None`이 되도록 처리했다.

## 🛠 변경 상세
- 수정: `src/Signal.CANdy.Core/Config.fs`
  - 새 타입 4종 추가.
  - `Config` 레코드에 `CrcCounter` 필드 추가.
  - 정수/16진수 파싱 보조 로직 추가(`0x..` 문자열 및 숫자 타입 모두 처리).
  - `byte_range` 파싱 시 맵 형태(`{start,end}`)와 배열 형태(`[start,end]`) 모두 지원.
  - `loadFromYaml`의 최종 레코드 생성에 `CrcCounter = crcCounter` 연결.
- 수정(연쇄 반영): `tests/Signal.CANdy.Core.Tests/ConfigTests.fs`
  - `validConfig` 기본값에 `CrcCounter = None` 추가.
- 수정(연쇄 반영): `tests/Signal.CANdy.Core.Tests/CodegenTests.fs`
  - `defaultConfig`에 `CrcCounter = None` 추가.
- 수정(연쇄 반영): `tests/Signal.CANdy.Core.Tests/EdgeCaseTests.fs`
  - `defaultConfig`에 `CrcCounter = None` 추가.
- 수정(연쇄 반영): `tests/Signal.CANdy.Core.Tests/FacadeTests.fs`
  - `defaultConfig`, `badConfig`에 `CrcCounter = None` 추가.
- 수정(연쇄 반영): `src/Signal.CANdy.Core/Api.fs`
  - `generateFromPaths` 기본 config에 `CrcCounter = None` 추가.
- 수정(연쇄 반영): `src/Generator/Program.fs`
  - CLI 기본 config에 `CrcCounter = None` 추가.

## ✅ 테스트 결과
- LSP diagnostics: 변경 파일 기준 오류/경고 없음.
- `dotnet build -c Release --nologo`
  - 결과: 성공, 오류 0.
  - 경고: 4건(FS0025, 기존 Generator/CLI/Facade 패턴 매치 경고, 기대 범위).
- `dotnet test -c Release -v minimal --nologo`
  - 결과: 전체 통과.
  - `Signal.CANdy.Core.Tests`: 89/89 통과
  - `Generator.Tests`: 27/27 통과
  - 합계: 116/116 통과

## ⏭ 다음 계획
- T6에서 `CrcCounterCheck`와 `CrcCounter` 간 충돌/정합성 검증(`ConfigConflict`)을 `validate` 단계에 추가한다.
- `CrcCounter` 파싱 결과를 IR/검증 파이프라인으로 연결하는 후속 단계에서 `UnknownAlgorithm`, `SignalNotFound`, `ByteRangeExceedsDlc` 등 도메인 오류와 연동한다.
