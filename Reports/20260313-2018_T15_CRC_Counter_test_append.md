## 📝 작업 요약
- `tests/Signal.CANdy.Core.Tests/ApiTests.fs`와 `tests/Signal.CANdy.Core.Tests/FacadeTests.fs` 끝에 CRC/Counter 관련 신규 `[<Fact>]` 4개를 추가했다.
- Api 레벨에는 validate 모드 성공 경로와 unknown algorithm 검증을, Facade 레벨에는 `SignalCandyValidationException` 매핑(`UnknownAlgorithm`, `ConfigConflict`)을 검증하도록 보강했다.

## 🛠 변경 상세
- 수정: `tests/Signal.CANdy.Core.Tests/ApiTests.fs`
- 추가 테스트 1: CRC signal + `crc_counter.mode=validate` 구성에서 `generateFromPaths`가 `Ok`를 반환하는지 검증.
- 추가 테스트 2: 존재하지 않는 CRC 알고리즘(`CRC8_NOT_REAL`) 구성에서 `GenerateError.Validation(ValidationError.UnknownAlgorithm _)`을 반환하는지 검증.
- 수정: `tests/Signal.CANdy.Core.Tests/FacadeTests.fs`
- 추가 테스트 3: facade `GenerateFromPathsAsync`가 unknown CRC algorithm에 대해 `SignalCandyValidationException`을 던지고 메시지에 `UnknownAlgorithm` 접두가 포함되는지 검증.
- 추가 테스트 4: facade `GenerateFromPathsAsync`가 비지원 16-bit CRC 알고리즘 구성에 대해 `SignalCandyValidationException`을 던지고 메시지에 `ConfigConflict` 접두가 포함되는지 검증.
- 참고: 현재 파이프라인은 `crc_counter.mode=validate`에서 DBC 메시지/시그널 미존재를 `MessageNotFound`/`SignalNotFound`로 노출하지 않으므로, 실제 구현이 노출하는 CRC/Counter validation 경로에 맞춰 테스트를 작성했다.

## ✅ 테스트 결과
- `dotnet build --configuration Release --nologo` → 경고 0, 오류 0.
- `dotnet test --filter "FullyQualifiedName~ApiTests|FullyQualifiedName~FacadeTests" -c Release --nologo` → 15개 통과, 실패 0.
- `dotnet test --configuration Release -v minimal --nologo` → Core 125 + Generator 27 = 총 152개 통과, 실패 0.
- 변경 파일 LSP diagnostics 확인 결과 오류/경고 없음.

## ⏭ 다음 계획
- T15 테스트 보강은 완료됐다.
- 후속 작업에서 `crc_counter.mode=validate`의 message/signal cross-check를 구현하면 `MessageNotFound`/`SignalNotFound` 경로를 별도 테스트로 추가할 수 있다.
