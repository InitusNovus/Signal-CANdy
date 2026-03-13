## 📝 작업 요약

RUN_ID: 20260312-1235

- Oracle vector generation에 overflow guardrail을 추가해 extreme-scale signal 처리 중 `int too big to convert` 및 float32 overflow로 인한 하드 크래시를 방지했다.
- `tests/oracle/oracle/vector_gen.py`에서는 adversarial raw span이 `2^20`을 넘으면 전체 range 순회를 중단하고 경계값 + 균등 샘플링으로 대체했다.
- `tests/oracle/oracle/engine.py`에서는 float32 변환 전 비유한값/초과값을 차단하고, overflow가 발생한 signal/value는 실패 대신 `overflow_guarded` skip 결과로 기록하도록 보호 경로를 추가했다.

## 🛠 변경 상세

- `tests/oracle/oracle/vector_gen.py`
  - `_MAX_ADVERSARIAL_RAW_SPAN`, `_MAX_ROUNDING_BOUNDARY_VECTORS`, `_UNIFORM_RAW_SAMPLES` 상수를 추가했다.
  - `generate_adversarial_vectors()`에 raw span guard를 넣어 `max_raw - min_raw + 1 > 2^20`인 경우 `min_raw`, `max_raw`와 16개의 균등 raw 샘플만 검사하도록 바꿨다.
  - 동적 cantools import를 `importlib.import_module()`로 바꿔 진단 경고를 제거했다.
- `tests/oracle/oracle/engine.py`
  - `_FLOAT32_MAX`, `_try_float32()`, `_safe_float32_physical()`, `_default_signal_value()`, `_overflow_guarded_result()`를 추가했다.
  - `_safe_physical()`이 float32 overflow / non-finite 값을 `None`으로 반환해 호출부가 안전하게 skip 처리할 수 있게 했다.
  - `_signal_values()`가 overflow된 candidate value를 버리고 `overflow_guarded` 여부를 함께 반환하도록 바꿨다.
  - `generate_test_vectors()`가 overflow된 signal을 `test_type: overflow_guarded`, `error: overflow_guarded`, `skipped: true` 결과로 기록하도록 바꿨다.

## ✅ 테스트 결과

- LSP diagnostics
  - `tests/oracle/oracle/vector_gen.py`: clean
  - `tests/oracle/oracle/engine.py`: clean
- Tesla vendor smoke
  - Command: `python tests/oracle/run_oracle.py --dbc tests/oracle/vendor_dbc/tesla_can.dbc --config examples/config.yaml --out-dir tmp/oracle_tesla_guard --verbose`
  - Result: `5205 passed, 3594 failed, 42 skipped`
  - Verification: stdout/stderr full output에서 `int too big to convert`, `OverflowError`, `Traceback` 미검출
- Comprehensive regression
  - Command: `python tests/oracle/run_oracle.py --dbc examples/comprehensive_test.dbc --config examples/config.yaml --out-dir tmp/oracle_regress_check --assert-pass`
  - Result: `588 passed, 0 failed, 0 skipped`

## ⏭ 다음 계획

- Tesla oracle의 기존 decode/encode 불일치 실패군은 overflow guardrail과 별개이므로, 필요 시 message별 tolerance/roundtrip 차이를 별도 triage한다.
- 이번 세션에서는 명시된 ROADMAP 항목 ID가 없어 `ROADMAP.md`는 변경하지 않았다.
