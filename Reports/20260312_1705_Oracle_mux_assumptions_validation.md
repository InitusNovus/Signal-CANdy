## 📝 작업 요약
- cantools multiplex API 가정 A1-A7을 검증하기 위해 `tests/oracle/tests/test_mux_assumptions.py`를 신규 작성하고, `examples/multiplex_suite.dbc` 기준으로 각 가정을 독립 테스트로 확인했다.

## 🛠 변경 상세
- 생성: `tests/oracle/tests/test_mux_assumptions.py`
  - A1: `MUX_MSG`의 multiplex 판별(`is_multiplexed` callable/attribute) truthy 검증
  - A2: `is_multiplexer == True` 신호가 `MuxSwitch` 하나뿐인지 검증
  - A3: `multiplexer_ids` 분포 검증 (`MuxSwitch`/`Base_8`: None 또는 빈값, `Sig_m1`: [1], `Sig_m2`: [2])
  - A4: 수동 payload(`MuxSwitch=1`) decode 시 active branch key만 반환되는지 검증 (`Sig_m2` 미포함)
  - A5: float 입력 + `strict=False` encode 성공 검증
  - A6: 일반 mux branch signal의 `len(multiplexer_ids) == 1` 검증
  - A7: branch-1 encode/decode roundtrip 값 보존 검증
- 생성: `.sisyphus/evidence/task-1-mux-assumptions.txt` (pytest -v 전체 출력)
- 생성: `.sisyphus/evidence/task-1-decode-branch-filter.txt` (decode 결과 dict/keys 증빙)
- 갱신(append): `.sisyphus/notepads/b-o2-oracle-multiplex-mode/learnings.md` (검증 결과 및 증빙 경로 기록)

## ✅ 테스트 결과
- 실행 경로: `tests/oracle/`
- 명령: `python -m pytest tests/test_mux_assumptions.py -v`
- 결과: `7 passed`, exit code 0
- 추가 증빙:
  - `.sisyphus/evidence/task-1-mux-assumptions.txt`
  - `.sisyphus/evidence/task-1-decode-branch-filter.txt` (keys: `Base_8`, `MuxSwitch`, `Sig_m1`)
- 변경 파일 LSP 진단: `tests/oracle/tests/test_mux_assumptions.py` diagnostics 없음

## ⏭ 다음 계획
- Task 2 구현을 진행한다 (이번 검증에서 A1-A7 모두 통과).
- Task 2에서 engine 로직 적용 시 이번 증빙 파일을 기준으로 mux API 가정(특히 decode branch filtering, multiplexer_ids 길이 조건)을 그대로 반영한다.
