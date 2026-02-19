## 📝 작업 요약
- Oracle MVP 코어 엔진을 구현해 DBC 로드 -> 코드 생성 -> 하네스 빌드 -> 디코드 검증 -> 보고서 생성 파이프라인을 연결했다.
- 단일 실행 CLI(`run_oracle.py`)를 완성해 인자 파싱, JSON 리포트 저장, 요약 출력, 실패 시 종료 코드 제어를 지원한다.

## 🛠 변경 상세
- `tests/oracle/oracle/engine.py`
  - `TestVector`, `TestResult`, `OracleReport` 데이터클래스 정의.
  - `load_dbc_cantools`, `run_codegen`, `extract_message_info`, `build_oracle_binary` 구현.
  - 경계값(0/min/max) + 랜덤 기반 `generate_test_vectors` 구현.
  - MVP 범위의 decode 비교 전용 `run_oracle_test` 구현(cantools encode -> C decode -> 허용오차 비교).
  - `compute_simple_tolerance` 임시 허용오차 구현 및 `oracle_pipeline` 엔드투엔드 연결.
- `tests/oracle/run_oracle.py`
  - `argparse` 기반 CLI 구성(`--dbc`, `--config`, `--out-dir`, `--assert-pass`, `--vectors-per-signal`, `--verbose`).
  - `oracle_pipeline` 실행 후 `report.json` 직렬화 저장.
  - 요약/상세 출력 및 `--assert-pass` 조건 종료 코드 처리.
- `.sisyphus/notepads/oracle-test-pipeline/learnings.md`
  - Task 3 학습 내용 항목 추가(파이프라인, 벡터 전략, decode 검증 방식, 임시 tolerance, CLI/리포트 구조).

## ✅ 테스트 결과
- `python tests/oracle/run_oracle.py --help`
  - 정상 출력, 종료 코드 0.
- `python tests/oracle/run_oracle.py --dbc examples/sample.dbc --config examples/config.yaml --out-dir tmp/oracle_test --verbose`
  - 실행 완료, 종료 코드 0.
  - 결과 요약: `6 passed, 14 failed, 0 skipped`.
  - 리포트 생성 확인: `tmp/oracle_test/report.json`.
- LSP 진단
  - `tests/oracle/oracle/engine.py`: diagnostics 없음.
  - `tests/oracle/run_oracle.py`: diagnostics 없음.

## ⏭ 다음 계획
- Task 4에서 signal-aware tolerance 계산 로직을 정교화해 현재 임시 tolerance를 교체한다.
- decode-only MVP를 encode/byte 검증까지 확장하고 실패 케이스 분류를 세분화한다.
- ROADMAP 체크박스 직접 갱신 항목 없음(이번 세션은 오케스트레이터 관리 플랜 파일 미수정 원칙 준수).
