RUN_ID: 20260312-1447

# Oracle tracking source-of-truth sync

## 📝 작업 요약
- `oracle-failure-resolution` wave의 실제 완료 상태와 `ROADMAP.md`, `Reports/`, `.sisyphus` 사이의 추적 불일치를 사실 기반으로 재정렬했다.
- 이번 작업의 목적은 기능 추가가 아니라, 다음 세션이 **무엇이 완료되었고 / 무엇이 backlog이며 / 어떤 파일을 먼저 믿어야 하는지**를 한 번에 이해할 수 있게 만드는 것이다.

## 🛠 변경 상세
- 수정: `ROADMAP.md`
  - `Report/` → `Reports/` 교정
  - Oracle tracking O-1~O-10을 최신 근거에 맞게 정렬
  - Oracle 후속 backlog를 별도 섹션으로 분리
  - source-of-truth 우선순위 명시
- 수정: `.sisyphus/plans/oracle-failure-resolution.md`
  - stale `Report/` 표기만 `Reports/`로 교정
- 생성: `Reports/20260312-1447_Oracle_tracking_source_of_truth_sync.md`

### 완료로 판정한 것
- `oracle-failure-resolution` wave 종료
  - 근거: `Reports/20260312-1530_Oracle_실패해결_완료.md`
  - 근거: `tests/oracle/ORACLE_RESULTS.md` (`839e777`, adjusted pass rate 99.25%)
- Oracle tracking O-1~O-10
  - O-1: stale plans archived + boulder lifecycle 정리
  - O-2: DbcParserLib type probe 완료
  - O-3~O-5: 기존 oracle pipeline 기반 구성 완료
  - O-6: overflow guardrails 완료
  - O-7: cantools parse incompatibility triage 완료
  - O-8: Category C criteria + exception docs 완료
  - O-9: pytest suite 구축 완료
  - O-10: 최종 결과 문서화 완료

### Category C로 남긴 것
- `dbc_raw_range_sentinel`
- `cantools_oracle_limitation` (multiplex skip)
- `valid_mask_width`
- `reference_decoder_incompatible`
- `float32_rounding`

### 아직 backlog로 남긴 것
- **B-O1. DBC raw-range detection heuristic**
  - 이유: 1,101 failure에 영향, 해결 가치 높음
- **B-O2. Oracle multiplex mode**
  - 이유: 현재 skip 영역 제거 가능
- **B-O3. Valid bitmask auto-widening**
  - 이유: >32 signal 메시지 구조 개선 필요

### Source-of-Truth hierarchy
1. `Reports/20260312-1530_Oracle_실패해결_완료.md`
2. `tests/oracle/ORACLE_RESULTS.md`
3. `tests/oracle/CATEGORY_C_EXCEPTIONS.md`
4. `ROADMAP.md`
5. `.sisyphus/*` (보조 작업 상태; canonical source 아님)

### `.sisyphus` 상태 판단
- `.sisyphus/boulder.json`은 현재 `active_plan: null` 상태다.
- 이는 “현재 active boulder 없음”을 뜻하는 보조 상태로 해석한다.
- canonical completion evidence는 `.sisyphus`가 아니라 `Reports/`와 `tests/oracle/ORACLE_RESULTS.md`에 둔다.

## ✅ 테스트 결과
- 코드 기능 변경 없음.
- 따라서 `dotnet build` / `dotnet test`는 이번 세션에서 재실행하지 않았다.
- 생략 사유: 이번 변경은 추적 문서/상태 정합성 복구에 한정되며, `Dbc.fs`, `Codegen.fs`, templates, tests 로직을 수정하지 않았다.
- 검증 수행:
  - `git diff --stat` / 핵심 diff로 문서 범위 내 변경만 있는지 확인
  - `grep`으로 `Report/` 잔존 여부 확인
  - `ROADMAP.md`, `Reports/20260312-*`, `.sisyphus/boulder.json`, `.sisyphus/plans/oracle-failure-resolution.md`, `tests/oracle/ORACLE_RESULTS.md` 교차 확인

## ⏭ 다음 계획
- 1) `B-O1` (raw-range heuristic) 착수 여부 결정
- 2) `B-O2` (Oracle multiplex mode) 착수 여부 결정
- 3) `B-O3` (valid bitmask auto-widening) 범위/우선순위 확정
- 4) 다음 세션에서는 위 3개 중 하나를 backlog item으로 선택해 진행하고, 추적 문서 정합성 작업은 재반복하지 않도록 한다.

## Handoff
- 먼저 읽을 파일: `Reports/20260312-1447_Oracle_tracking_source_of_truth_sync.md`
- 그다음: `Reports/20260312-1530_Oracle_실패해결_완료.md`
- 수치/분류 확인: `tests/oracle/ORACLE_RESULTS.md`, `tests/oracle/CATEGORY_C_EXCEPTIONS.md`
- backlog 판단 기준: `ROADMAP.md`의 `Oracle 후속 backlog` 섹션
