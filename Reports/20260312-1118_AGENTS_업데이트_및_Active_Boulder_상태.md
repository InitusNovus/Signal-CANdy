# AGENTS 업데이트 및 Active Boulder 상태 점검

RUN_ID: 20260312-1118

## 📝 작업 요약
- `AGENTS.md`를 현재 레포 실상과 `General_Guidance_For_AGENTS.md`에 맞춰 최소 범위로 업데이트했다.
- 핵심은 `Report/` 기준을 실제 운영 중인 `Reports/` 기준으로 교정하고, evidence-first / immutable history / patch-forward correction / local-only boundary 규칙을 보강한 것이다.
- 함께 active boulder 상태를 재확인했고, 현재 활성 플랜은 완료로 보기 어려워 `boulder.json`은 유지해야 한다는 판단을 보고에 남긴다.

## 🛠 변경 상세
- 수정: `AGENTS.md`
  - `Report/` 표기를 `Reports/`로 전면 교정
  - `Evidence & Source-of-Truth` 섹션 추가
    - 1차 근거: 실제 소스/워크스페이스/빌드·테스트 출력/`Reports/`
    - 2차 지원: `.sisyphus/`, 보조 도구, 외부 참고자료
  - `Local-Only / Historical Boundaries` 섹션 추가
    - `Reports/` append-only 원칙
    - `.sisyphus/`는 작업 상태이며 canonical source가 아님을 명시
    - `gen/`, `tmp/`, `external_test/`의 경계 명시
  - 작업 보고 규칙 보강
    - 보고서 불변성 / patch-forward 정정 방식 추가
    - 장기 실행 배치용 `RUN_ID` 선택 규칙 추가 (기본 단일 보고서 관행은 유지)
- active boulder 확인 근거
  - `.sisyphus/boulder.json` → active plan은 `oracle-failure-resolution.md`
  - `.sisyphus/plans/oracle-failure-resolution.md` → 다수 DoD / Final Checklist 미완료
  - `ROADMAP.md` → Oracle 추적 항목 `O-1`, `O-2`, `O-6`, `O-7`, `O-8` 미완료
- 결론
  - active boulder는 아직 정리 대상이 아님
  - `boulder.json`은 이번 세션에서 수정하지 않음

## ✅ 테스트 결과
- 문서 변경 작업이므로 코드 빌드/테스트는 실행하지 않았다.
- 검증 수행:
  - `read`로 수정 후 `AGENTS.md` 본문 확인
  - `grep`으로 `Report/` 잔존 여부 및 `Reports/`, `patch-forward`, `RUN_ID` 반영 여부 확인
  - `lsp_diagnostics AGENTS.md` 시도 결과: `.md` LSP 미설정으로 진단 미지원 (환경 제약)
- 판정:
  - `AGENTS.md` 변경 내용은 현재 레포 구조 및 새 guidance와 정합적
  - active boulder는 미완료 상태로 유지 판단

## ⏭ 다음 계획
- 필요 시 다음 세션에서 `ROADMAP.md`, `.sisyphus/plans/oracle-failure-resolution.md`, 실제 보고서/산출물 사이의 완료 기준을 재정렬해 active boulder 종료 가능 조건을 명확히 한다.
- `AGENTS.md` 추가 개정이 필요하다면, 다음 우선순위는 operation mode 명시와 reference-vs-implementation 경계의 세분화다.
