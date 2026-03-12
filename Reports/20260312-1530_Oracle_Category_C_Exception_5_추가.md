# 작업 보고서: 20260312-1530_Oracle_Category_C_Exception_5_추가

RUN_ID: 20260312-1530

📝 **작업 요약**
`tests/oracle/CATEGORY_C_EXCEPTIONS.md` 문서에 `dbc_raw_range_sentinel` (Exception 5) 사례를 추가하여, DBC 작성 오류로 인한 물리적 범위 체크 실패 건을 공식적인 기술적 한계(Category C)로 문서화함.

🛠 **변경 상세**
- `tests/oracle/CATEGORY_C_EXCEPTIONS.md`: 
    - `### Exception 5 — DBC raw range sentinel` 섹션 추가
    - Chrysler Pacifica 및 Mercedes E350 DBC의 구체적인 오류 사례(raw count 기반 min/max 설정) 명시
    - 4가지 기준(Technical Limitation, Scoped Impact, No Feasible Alternative, ROADMAP Entry)에 따른 정당성 부여
    - `dbc_raw_range_sentinel` 카테고리 태그 추가

✅ **테스트 결과**
- 문서 구조 검증: 기존 4개 예외 사항과 "Ineligible Reasons" 섹션 사이의 삽입 위치 및 마크다운 형식 준수 확인
- Git Commit 완료: `docs(oracle): add Category C exception 5 — dbc_raw_range_sentinel` (Hash: `b5947c2`)

⏭ **다음 계획**
- `ROADMAP.md`의 "DBC raw-range detection heuristic" 항목 추적 및 구현 검토
- 추가적인 Oracle 실패 사례 발생 시 동일 절차에 따라 Category C 문서 업데이트
