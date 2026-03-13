## 📝 작업 요약
RUN_ID: 20260312-1235

- `src/Signal.CANdy.Core/Dbc.fs`의 `metaMap` 미스 폴백에서 ByteOrder를 하드코딩 `ByteOrder.Little`로 두던 버그를 TDD(RED→GREEN)로 수정했다.
- `DbcParserLib.Signal.ByteOrder`(`0uy=Big`, `1uy=Little`)를 직접 사용하도록 변경해 BE 신호 폴백 경로를 바로잡았다.

## 🛠 변경 상세
- 수정: `tests/Signal.CANdy.Core.Tests/DbcTests.fs`
  - 신규 RED/GREEN 회귀 테스트 추가: ``BE signal gets ByteOrder.Big even when not in metaMap``
  - `SG_ BE_16: ...` 형태(콜론 인접) 입력으로 `metaMap` 키 미스 경로를 유도하고, 결과 IR의 `ByteOrder.Big`를 검증.
- 수정: `src/Signal.CANdy.Core/Dbc.fs`
  - 기존: `| None -> (s.Minimum < 0.0), ByteOrder.Little`
  - 변경: `| None -> let byteOrder = if s.ByteOrder = 0uy then ByteOrder.Big else ByteOrder.Little; (s.Minimum < 0.0), byteOrder`
  - signedness 로직(`s.Minimum < 0.0`)은 유지.
- 수정: `.sisyphus/notepads/oracle-failure-resolution/wave1_findings.md`
  - Task 2 결과(수정 위치/코드/Ford canary/전체 테스트 결과) append.

## ✅ 테스트 결과
- RED 확인:
  - `dotnet test --configuration Release --filter "DisplayName~BE signal gets ByteOrder.Big" -v normal`
  - 결과: **FAIL** (`Expected Big`, `Actual Little`)
- GREEN 확인:
  - 동일 필터 재실행 결과: **PASS** (1/1)
- 회귀 확인:
  - `dotnet test --configuration Release -v minimal --nologo`
  - 결과: **87 passed, 0 failed** (`Signal.CANdy.Core.Tests` 60 + `Generator.Tests` 27)
- Ford canary:
  - `python tests/oracle/run_oracle.py --dbc tests/oracle/vendor_dbc/ford_fusion_2018_pt.dbc --config examples/config.yaml --out-dir tmp/oracle_ford_canary --verbose`
  - 결과: `passed=240`, `failed=1188`, `pass_rate=16.81%`
  - 산출물: `tmp/oracle_ford_canary/report.json`

## ⏭ 다음 계획
- Ford canary 실패 상위 원인(현재 다수 `decode/encode failed`)을 메시지/신호 패턴별로 분류해 다음 핵심 버그(비트 인덱싱, mux/value-table, 범위/스케일 경로) 우선순위를 수립한다.
- `metaMap` 파싱 강건성(`SG_` 토큰화/콜론 인접 케이스)을 별도 회귀 세트로 확장해 동일 유형 재발을 방지한다.
