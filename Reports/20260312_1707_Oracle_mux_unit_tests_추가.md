## 📝 작업 요약
- `tests/oracle/tests/test_engine.py`에 mux 전용 회귀 테스트 4개를 추가했다. 현재 `engine.py` Task 2 구현 전이라 실행 시 실패 가능성이 있지만, collect-only 기준으로 신규 테스트가 정상 수집되는 상태를 확보했다.

## 🛠 변경 상세
- 수정: `tests/oracle/tests/test_engine.py`
  - `test_oracle_pipeline_multiplex_dbc`: `oracle_pipeline()`로 multiplex DBC가 skip 없이 통과해야 함을 고정.
  - `test_generate_vectors_mux_branches`: 실제 `dotnet run --project src/Generator` 산출물 + `extract_message_info()` + `generate_test_vectors()` 조합으로 `MUX_MSG`의 branch 1/2 벡터 생성을 검증.
  - `test_sample_dbc_has_no_mux_skips`: `sample.dbc`에서 multiplex 관련 skip 회귀가 없는지 검증.
  - `test_mux_switch_not_target`: `MuxSwitch`가 테스트 타깃 signal 집합에 포함되지 않아야 함을 검증.
- 생성: `.sisyphus/evidence/task-4-mux-unit-tests.txt`
  - `python -m pytest tests/test_engine.py -v --collect-only` 출력 저장.
- 갱신(append): `.sisyphus/notepads/b-o2-oracle-multiplex-mode/learnings.md`
  - Task 4 테스트 전략과 evidence 경로 기록.

## ✅ 테스트 결과
- 실행 경로: `tests/oracle/`
- 명령: `python -m pytest tests/test_engine.py -v --collect-only`
- 결과: `8 tests collected`, exit code 0
- 증빙: `.sisyphus/evidence/task-4-mux-unit-tests.txt`
- LSP 진단: `tests/oracle/tests/test_engine.py` diagnostics 없음
- 비고: 사용자가 지정한 대로 collect-only만 수행했다. 신규 mux 테스트는 Task 2 전까지 실패 가능성이 있다.

## ⏭ 다음 계획
- Task 2에서 `tests/oracle/oracle/engine.py`의 mux 처리 로직을 구현해 이번에 추가한 4개 테스트를 실제 통과 상태로 전환한다.
- 구현 후 `python -m pytest tests/test_engine.py -v` 또는 mux 관련 선택 실행으로 branch/vector 동작을 재검증한다.
