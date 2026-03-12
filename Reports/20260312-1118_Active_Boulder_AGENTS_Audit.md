# Active boulder 상태 점검 및 AGENTS 업그레이드 방향 검토

RUN_ID: 20260312-1118

## 📝 작업 요약
- 현재 레포의 작업 트리, `.sisyphus/boulder.json`, 활성 플랜, `ROADMAP.md`, 기존 보고서, `General_Guidance_For_AGENTS.md`를 교차 확인해 활성 boulder 플랜의 완료 여부를 감사했다.
- 결론적으로 활성 플랜 `oracle-failure-resolution`은 **완료로 판단할 근거가 부족하며**, 플랜 내부 체크리스트와 `ROADMAP.md` 추적 상태도 미완료로 남아 있어 `boulder.json`은 정리하지 않았다.
- 추가로 `AGENTS.md`와 `General_Guidance_For_AGENTS.md`를 비교해, 현재 레포에 맞는 업그레이드 포인트(특히 `Reports/` 마이그레이션, evidence-first 규칙, 보고 체계 보강)를 정리했다.

## 🛠 변경 상세
- 생성: `Reports/20260312-1118_Active_Boulder_AGENTS_Audit.md`
  - 활성 boulder 상태와 플랜 완료 여부 감사 결과 기록
  - `AGENTS.md` 개선 방향 초안 기록
- 확인한 핵심 파일/근거
  - `.sisyphus/boulder.json`
  - `.sisyphus/plans/oracle-failure-resolution.md`
  - `.sisyphus/plans/archived/`
  - `ROADMAP.md`
  - `AGENTS.md`
  - `General_Guidance_For_AGENTS.md`
  - `Reports/20260215_1549_계획현황_분석_업데이트.md`
  - `Reports/20260213_1720_Oracle_실패해결_플랜수립.md`
  - `Reports/20260213_1817_Oracle_하네스_템플릿_구현.md`
  - `Reports/20260213_1838_Oracle_Core_Engine_구현.md`
  - `Reports/20260213_1848_Oracle_Tolerance_Metadata_Comparison.md`
  - `Reports/20260213_1937_Oracle_pytest_스위트_구축.md`
  - `Reports/20260213_1952_Oracle_Integration_Result_Report.md`
  - `tests/oracle/ORACLE_RESULTS.md`
- 감사 요약
  - `boulder.json`은 여전히 `oracle-failure-resolution`을 active plan으로 가리킨다.
  - 해당 플랜 내부 `Definition of Done`, `Final Checklist`, Task 1~9 체크박스는 대부분 `[ ]` 상태다.
  - `ROADMAP.md`의 Oracle 추적 섹션도 `O-1`, `O-2`, `O-6`, `O-7`, `O-8`이 미완료다.
  - 보고서 증빙은 존재하지만, 활성 플랜의 체크리스트/완료 조건과 정확히 닫히지 않으며 일부 태스크 명칭/번호도 보고서와 불일치한다.
  - 따라서 active boulder를 정리할 수준의 완료 증빙은 아직 없다.
- AGENTS 업그레이드 후보 요약
  - `Report/` 기준 규칙을 `Reports/` 기준으로 전면 갱신 필요
  - 일반 운영 규칙과 repo-specific 규칙을 2계층으로 분리 필요
  - source-of-truth hierarchy, immutable reports, patch-forward correction, local-only workspace 정책 추가 가치 높음
  - 다만 현재 레포의 단일 세션 보고 문화와 충돌하지 않도록 `RUN_ID` 다중 보고 세트는 선택적/비정규 대규모 배치 작업에 한정하는 것이 적합

## ✅ 테스트 결과
- 코드/동작 변경은 없어서 `dotnet build` / `dotnet test` 실행 대상은 아니었다.
- 검증 수행:
  - `git status --short --branch`로 작업 트리 확인
  - `read/glob/grep`로 `.sisyphus`, `ROADMAP.md`, `Reports/`, `AGENTS.md` 근거 교차 확인
  - 활성 플랜 파일의 체크리스트와 실제 보고서/산출물의 일치 여부 수동 대조
- 핵심 판정:
  - 활성 boulder 플랜 완료 **아님**
  - `boulder.json` 정리 **미실행**

## ⏭ 다음 계획
- 1) `oracle-failure-resolution.md`의 실제 완료 범위를 현재 보고서/산출물 기준으로 다시 매핑하고, 남은 태스크와 종료 기준을 명시적으로 정리한다.
- 2) 활성 플랜을 정말 종료하려면 최소한 `ROADMAP.md` Oracle 추적 섹션과 플랜 내부 체크리스트가 동일한 기준으로 정리되어야 한다.
- 3) 이후 `AGENTS.md` 개정 시 우선순위는 다음 순서가 적절하다:
  - `Reports/` 마이그레이션 반영
  - evidence/source-of-truth 규칙 추가
  - immutable history + patch-forward correction 추가
  - local-only workspace / reference boundary 명시
  - 필요 시 장기 배치 작업에 한해 RUN_ID 기반 보강 규칙 도입
