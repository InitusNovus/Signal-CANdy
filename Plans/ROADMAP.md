# ROADMAP — Signal-CANdy Active Plan

> 이 문서는 현재 활성 계획 문서입니다.
> 2026-02~2026-03 세대 종결본은 `Plans/Archive/ROADMAP_202602_202603_Closed.md`에 원본 그대로 보관합니다.
> 현행 truth 확인 순서: 최신 `Reports/` → `Plans/ROADMAP.md` → `tests/oracle/CATEGORY_C_EXCEPTIONS.md` / `tests/oracle/ORACLE_RESULTS.md`.

## 포함 원칙

- 닫힌 snapshot(`Plans/Archive/ROADMAP_202602_202603_Closed.md`)을 다시 흔들게 되는 항목만 이 문서에서 관리한다.
- 구현 완료로 닫힌 항목은 여기로 되돌려 적지 않는다.
- 설계/정책이 더 필요한 future-facing 항목만 유지한다.

## 현재 이관된 후속 항목

### 1. valid 비트마스크 `>64` 신호 fallback 방식

- 현 상태: `≤32 -> uint32_t`, `33–64 -> uint64_t`, `>64 -> UnsupportedFeature`
- 근거: `Plans/Archive/ROADMAP_202602_202603_Closed.md` L-3a close-out 문구, `tests/oracle/CATEGORY_C_EXCEPTIONS.md` Exception 3
- 후속 판단 과제: 배열 기반 valid 표현을 실제로 도입할지, 아니면 `>64` 미지원을 장기 정책으로 유지할지 결정

### 2. Oracle reference decoder 비호환 DBC 대응 전략

- 현 상태: 일부 벤더 DBC는 `cantools` 파서 비호환으로 Category C 처리
- 근거: `tests/oracle/CATEGORY_C_EXCEPTIONS.md` Exception 4, `tests/oracle/ORACLE_RESULTS.md`
- 후속 판단 과제: 다른 reference decoder를 도입할지, 현행 Category C 정책을 유지할지 결정

## 다음 세션 진입점

1. 최신 close-out / verification 보고서를 먼저 확인한다.
2. 위 두 항목 중 실제 제품 우선순위가 생긴 것만 새 세대 계획으로 승격한다.
3. 새 계획을 시작할 때는 archive 문서를 수정하지 않고, 이 문서 또는 `Plans/` 하위 successor roadmap에서 이어간다.
