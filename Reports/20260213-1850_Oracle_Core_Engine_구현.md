# Oracle Core Engine 구현 보고

## 📝 작업 요약
- `tests/oracle/oracle/engine.py`에 단일 DBC + 단일 config 기준 오라클 엔드투엔드 파이프라인을 구현했습니다.
- `tests/oracle/run_oracle.py` CLI를 완성해 codegen 실행, 하네스 빌드, 양방향 검증, JSON 리포트 생성, `--assert-pass` 종료 코드 제어를 연결했습니다.

## 🛠 변경 상세
- 수정: `tests/oracle/oracle/engine.py`
  - `load_dbc_cantools`, `run_codegen`, `extract_message_info`, `build_oracle_binary`, `run_oracle_test`, `oracle_pipeline` 구현.
  - 생성 헤더 파싱 기반 메시지/신호 메타데이터 추출 추가.
  - 경계값 + 랜덤(raw 범위 기반) 테스트 벡터 생성 및 물리값 안전 변환 로직 추가.
  - decode/encode/byte 3종 비교와 structured `OracleReport`/`TestResult` 모델 추가.
  - unsupported 신호(부동소수형 SIG_VALTYPE, 확장 mux) skip 처리 및 리포트 반영.
- 수정: `tests/oracle/run_oracle.py`
  - argparse 옵션(`--dbc`, `--config`, `--out-dir`, `--assert-pass`, `--vectors-per-signal`, `--verbose`) 동작 완성.
  - `report.json` 출력 및 verbose 결과 출력, 실패 시 exit code 1 처리.
- 수정: `.sisyphus/notepads/oracle-test-pipeline/learnings.md`
  - Task 3 구현 학습 내용을 append 방식으로 기록.

## ✅ 테스트 결과
- `python -m py_compile tests/oracle/oracle/engine.py tests/oracle/run_oracle.py` 통과.
- LSP 진단
  - `tests/oracle/oracle/engine.py`: No diagnostics found.
  - `tests/oracle/run_oracle.py`: No diagnostics found.
- 필수 검증
  - `python tests/oracle/run_oracle.py --dbc examples/sample.dbc --config examples/config.yaml --out-dir tmp/oracle_test` 실행 성공.
  - `tmp/oracle_test/report.json` 생성 및 스키마/결과 개수 검증 통과 (decode/encode/byte 포함, failed=0).
- 추가 검증
  - `python tests/oracle/run_oracle.py --dbc examples/comprehensive_test.dbc --config examples/config.yaml --out-dir tmp/oracle_smoke_comprehensive --vectors-per-signal 2` 통과 (failed=0).
  - `python tests/oracle/run_oracle.py --dbc examples/fixed_suite.dbc --config examples/config.yaml --out-dir tmp/oracle_smoke_fixed --vectors-per-signal 2` 통과 (failed=0).
  - `--assert-pass` 실패 경로 검증: nonexistent DBC 입력 시 종료코드 1 확인.
- 전체 빌드
  - `dotnet build --configuration Release --nologo` 성공 (warning 0, error 0).

## ⏭ 다음 계획
- Task 4에서 `tolerance.py` 정식 수식/비교 로직을 구현해 현재 엔진의 placeholder fallback 경로를 대체합니다.
- 이후 Task 6/7에서 config matrix 및 corpus 확장 시, 본 Task 3 엔진 함수를 재사용하도록 연결합니다.
- ROADMAP 체크박스 갱신 대상 항목은 이번 세션에서 명시적으로 완료 처리하지 않았습니다.
