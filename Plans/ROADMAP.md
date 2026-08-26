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

### 2. Runtime-loadable schema architecture RFC

- 추적 이슈: #17 — `[RFC] Shared semantic IR and pool-bound runtime schema images`
- 설계 원자료: `Plans/Runtime_Schema_Architecture.md`
- 현 상태: 대화에서 도출된 배경, staged IR, Pool/Application Contract, Wire Model, explicit Binding, Host compiler, runtime image, allocation-free C99 runtime, validation, hot-swap, testing, MVP 및 미결정점을 손실 최소화 우선으로 기록함
- 문서 성격: accepted specification이 아니라 후속 구현 에이전트가 정제할 exhaustive design handoff
- 다음 판단 과제:
  - terminology와 staged IR 경계 확정
  - 기존 `Ir.fs` 및 AOT C backend migration/compatibility 정책 확정
  - 최소 Pool/Wire/Binding/Target input contract 확정
  - runtime image v1 invariants와 Pool ABI 전략 확정
  - vertical slice와 child issue 순서 확정
- 운영 원칙: 설계 문서 PR merge만으로 #17을 자동 close하지 않고, accepted architecture와 child issue 링크를 이슈에 기록한 뒤 close

## 다음 세션 진입점

1. 최신 close-out / verification 보고서를 먼저 확인한다.
2. Runtime schema 후속 작업은 #17과 `Plans/Runtime_Schema_Architecture.md`를 함께 읽고, 정보 손실 없이 정제하는 단계부터 시작한다.
3. 구현 전 terminology, IR boundary, Pool ABI, runtime-image v1 scope를 명시적으로 결정하거나 defer한다.
4. 구현 항목은 RFC가 안정된 뒤 child issue로 분할한다. 초기 대화 원자료를 곧바로 세부 issue 여러 개로 흩뜨리지 않는다.
5. Oracle reference decoder 항목은 실제 제품 우선순위가 생기면 별도 successor plan으로 승격한다.
6. 새 계획을 시작할 때는 archive 문서를 수정하지 않고, 이 문서 또는 `Plans/` 하위 successor roadmap에서 이어간다.
