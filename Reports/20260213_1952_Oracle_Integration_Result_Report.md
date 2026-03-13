# Oracle Integration Result Report

## 📝 작업 요약
- Task 9 범위로 oracle 파이프라인 전체 통합 실행을 완료했다.
- 7개 example DBC 단건 실행, comprehensive matrix 8조합 실행, vendor corpus 15개 실행 후 결과를 종합 분석했다.
- 분석 결과를 `tests/oracle/ORACLE_RESULTS.md`에 문서화하고, 재현 명령/known issue/recommendation까지 포함했다.

## 🛠 변경 상세
- 생성: `tests/oracle/ORACLE_RESULTS.md`
  - Executive summary, 예제 7종 표, matrix 8조합 표, corpus 요약, category별 pass/fail/skip, known divergence, investigation 항목, 재현 명령 추가.
- 갱신(append): `.sisyphus/notepads/oracle-test-pipeline/learnings.md`
  - `## [2026-02-13T10:50:56Z] Task 9: Full Integration Test` 섹션 추가.
- 참고 산출물(분석 보조): `tmp/oracle_final_summary.json`
  - 예제/코퍼스/카테고리 집계 및 상위 실패 패턴 자동 집계.

## ✅ 테스트 결과
- Oracle example 7종 실행:
  - `sample`, `comprehensive_test`, `motorola_lsb_suite`, `fixed_suite`, `canfd_test` 통과
  - `value_table`, `multiplex_suite`는 single-config multiplex skip 정책으로 skip-only
- Matrix 실행 (`comprehensive_test.dbc`): 8/8 config 모두 통과 (4704 passed, 0 failed, 0 skipped)
- Vendor corpus 실행 (15개): 5 passed / 10 failed / 0 skipped
- 전체 집계(예제+matrix+corpus):
  - Tests: 97,346
  - Passed: 89,986 (92.44%)
  - Failed: 7,277 (7.48%)
  - Skipped: 83 (0.09%)
- 회귀 검증:
  - `pytest tests/oracle/tests/ -v` -> 30 passed
  - `dotnet test --configuration Release -v minimal --nologo` -> 85 passed, 0 failed

## ⏭ 다음 계획
- ROADMAP Task 9 후속으로 vendor corpus 실패를 유형별로 분리해 개선 우선순위를 정의한다.
  1. cantools parse incompatibility(문법/ID 규칙) 사전 분류
  2. 대형 signed/scaled 신호 벡터 생성 경계 보정
  3. single-config 경로의 multiplex 실행 지원 확장
