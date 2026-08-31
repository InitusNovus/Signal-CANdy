# ROADMAP — Signal-CANdy Active Plan

> 이 문서는 현재 활성 계획 문서입니다.
> 2026-02~2026-03 세대 종결본은 `Plans/Archive/ROADMAP_202602_202603_Closed.md`에 원본 그대로 보관합니다.
> 현행 truth 확인 순서: 최신 `Reports/` → `Plans/ROADMAP.md` → 활성 RFC/설계 문서 → `tests/oracle/CATEGORY_C_EXCEPTIONS.md` / `tests/oracle/ORACLE_RESULTS.md`.

## 포함 원칙

- 닫힌 snapshot(`Plans/Archive/ROADMAP_202602_202603_Closed.md`)을 다시 흔들게 되는 항목만 이 문서에서 관리한다.
- 구현 완료로 닫힌 항목은 여기로 되돌려 적지 않는다.
- 설계/정책이 더 필요한 future-facing 항목만 유지한다.
- 새 architecture 세대는 과거 archive를 수정하지 않고 새 RFC와 successor plan으로 이어간다.

## 현재 이관된 후속 항목

### 1. Oracle reference decoder 비호환 DBC 대응 전략

- 현 상태: 일부 벤더 DBC는 `cantools` 파서 비호환으로 Category C 처리
- 근거: `tests/oracle/CATEGORY_C_EXCEPTIONS.md` Exception 4, `tests/oracle/ORACLE_RESULTS.md`
- 후속 판단 과제: 다른 reference decoder를 도입할지, 현행 Category C 정책을 유지할지 결정

### 2. Runtime-schema v1 종결 및 다음 staged IR/AST 정책

- GitHub 상태: #17과 child #19-#25는 모두 CLOSED (2026-08-27). #17은 더 이상 활성 RFC/분할 작업의 추적 항목이 아니다.
- 완료된 7개 runtime-schema capability:
  1. #19: logical message ID 기반 allocation-free TX encode와 성공 시에만 counter를 commit하는 semantics
  2. #20: bounded nested mux RX와 caller-clock 기반 freshness/quality state
  3. #21: precomputed CRC/counter protection plan의 RX 검증 및 TX 생성
  4. #22: strict Target Capability 및 Project Manifest 기반의 deterministic runtime-image build
  5. #23: caller-owned transactional activation hot-swap과 deterministic state reset
  6. #24: canonical `sc.inspect/v1`, `sc.map/v1`, `sc.diff/v1` 및 activation compatibility 설명
  7. #25: deterministic malformed-image cross-oracle/sanitizer hardening과 resource gates
- canonical close-out evidence: `Reports/20260827-1645_CC1A_TX_HIL_검증.md`, `Reports/20260827-1756_CC1A_RXQ_HIL_검증.md`, `Reports/20260827-1904_CC1A_Protection_HIL_검증.md`, `Reports/20260827-2005_CC1A_Project_Manifest_HIL_검증.md`, `Reports/20260827-2120_CC1A_Activation_HIL_검증.md`, `Reports/20260827-2229_Inspect_Map_Diff_HIL_증거.md`; 최신 종결 근거는 `Reports/20260828-0000_Hardening_CC1A_HIL_검증.md`이다.
- v1 설계/결정 기록: `Plans/Runtime_Schema_Architecture.md`, `Plans/Runtime_Schema_Decisions.md` (D-01~D-25). 이 기록은 완료된 v1의 근거이며 새 작업 지시는 아니다.
- 현재 활성 설계 질문: legacy `Ir.fs`와 string+Scriban AOT C backend, typed `WireIr`/`LinkedSchema`와 runtime-image plan이 함께 존재하는 현재 구조를 어떤 명시적이고 일관된 staged IR/AST policy로 정렬할 것인가.
- 이 refactor의 구체 설계는 아직 승인되거나 작성되지 않았다. 다음 작업은 기존 representation, public/API/backend compatibility contract, 각 stage의 ownership을 대조해 successor design을 세우는 것이며, 그 결론을 미리 가정한 migration 구현은 범위가 아니다.

## 다음 세션 진입점

1. `Reports/20260828-0000_Hardening_CC1A_HIL_검증.md`와 위의 capability별 canonical evidence를 먼저 확인한다.
2. staged IR/AST policy를 검토할 때 `src/Signal.CANdy.Core/Ir.fs`, `Wire.fs`, `Linked.fs`, `Codegen.fs`와 `Plans/Runtime_Schema_Architecture.md`를 함께 읽어 legacy AOT 및 runtime-schema representation의 실제 경계와 compatibility contract를 inventory한다.
3. inventory를 근거로 explicit staged IR/AST policy의 successor design을 제안한다. 구현/migration의 shape나 일정은 그 설계가 합의되기 전에는 확정하지 않는다.
4. Oracle reference decoder 항목은 실제 제품 우선순위가 생기면 별도 successor plan으로 승격한다.
5. 새 계획을 시작할 때는 archive 문서를 수정하지 않고, 이 문서 또는 `Plans/` 하위 successor roadmap에서 이어간다.
