## 📝 작업 요약

- Signal-CANdy의 다음 architecture 후보인 **Pool-bound runtime-loadable schema image** 방향을 추적하기 위해 GitHub RFC 이슈 #17을 생성했다.
- 대화에서 나온 결론만 압축하지 않고, 배경·중간 논리·대안·위험·미결정점을 보존하는 것을 우선하여 `Plans/Runtime_Schema_Architecture.md` 장문 설계 원자료를 작성했다.
- 기존 DBC→IR→AOT C 흐름을 폐기하지 않고, staged IR와 runtime-image backend를 추가하는 방향으로 기록했다.
- 이전 IR/F# DSL/FP-first compiler 논의의 핵심 제약인 heap-free, bounded execution, explicit state/effect, compile-time resource validation, thin deterministic target executor도 별도 섹션으로 보존했다.
- `Plans/ROADMAP.md`에 #17과 설계 문서를 현재 활성 future-facing architecture 항목으로 연결했다.

## 🛠 변경 상세

### GitHub tracking

- 생성 이슈: #17 — `[RFC] Shared semantic IR and pool-bound runtime schema images`
- 작업 브랜치: `docs/runtime-schema-architecture-rfc`
- 기준 브랜치: `dev`
- 이슈는 문서 PR merge만으로 자동 close하지 않고, accepted architecture와 child issue 구조가 기록된 뒤 close하도록 명시했다.

### 생성 파일

- `Plans/Runtime_Schema_Architecture.md`
  - 문서 상태와 정보 보존 원칙
  - 현행 Signal-CANdy와 방향 전환의 배경
  - Process Image / Pool mental model
  - Semantic Signal ID / Logical Message ID / Binding Index / C Offset / Wire ID 분리
  - Pool Definition / Application Contract 및 generated C + Host Manifest
  - DBC / canonical Wire Model / Wire Overlay
  - explicit Pool–Wire Binding과 suggest→review→lock→build 흐름
  - Target Capabilities / Project Manifest
  - Source AST → Wire IR / Pool Contract IR → Binding IR → Linked Schema IR → Runtime Image IR staged architecture
  - 기존 `Ir.fs`와 AOT backend의 migration/compatibility 고려
  - F# shallow/deep DSL 및 FP-first bounded language 구상과의 관계
  - Host compiler library / CLI / GUI / artifact 구조
  - `.scimg` section 후보, pointer-free/indexed representation, deterministic build, structural compression
  - allocation-free C99 runtime API 후보
  - decode atomicity, validity/updated/changed/quality, freshness, multiple writers, torn-read 문제
  - numeric/unit/inverse conversion semantics
  - TX payload template와 stateful counter commit timing
  - schema A/B activation 및 runtime-state reset
  - capability negotiation, validation, LLM trust boundary
  - differential/property/fuzz/resource testing
  - MVP, later extensions, non-goals, decisions, open questions, repository shape, child issue 후보

- `Reports/20260826-2248_Runtime_Schema_Architecture_RFC_전달.md`
  - 이번 handoff 세션의 작업 기록

### 수정 파일

- `Plans/ROADMAP.md`
  - 현재 truth 확인 순서에 활성 RFC/설계 문서를 추가
  - `Runtime-loadable schema architecture RFC` 항목 추가
  - #17, 설계 원자료, 다음 판단 과제, close 조건, 다음 세션 진입점을 기록

### 의도적으로 변경하지 않은 항목

- 기존 `src/Signal.CANdy.Core/Ir.fs`
- 기존 AOT C codegen/API
- binary format 또는 public API
- 테스트 코드와 CI
- 과거 `Reports/` 및 archive roadmap

이번 PR은 구현 PR이 아니라 **정제 전 설계 정보 전달용 documentation PR**이다.

## ✅ 테스트 결과

### 자동 테스트

- 코드 변경이 없으므로 `dotnet build`, `dotnet test`, generated C build는 실행하지 않았다.

### 수동 검증

- 작업 브랜치가 `dev`에서 생성되었음을 확인했다.
- RFC 이슈 #17이 open 상태로 생성되었음을 확인했다.
- `Plans/Runtime_Schema_Architecture.md`의 문서 시작부, 중간 staged-IR/F# DSL 구간, 후반 MVP/후속 issue/중심 통찰 구간이 branch에서 정상 조회됨을 확인했다.
- `Plans/ROADMAP.md`가 기존 Oracle 항목을 보존하면서 #17과 새 설계 문서를 연결함을 확인했다.
- 문서가 확정 사양으로 오해되지 않도록 `exhaustive design handoff`, `정제 전`, `미결정` 상태를 명시했다.
- Transport/flash updater와 runtime compiler/format 책임 경계, AOT backend 유지, LLM authoring과 deterministic trust boundary가 누락되지 않았음을 확인했다.

## ⏭ 다음 계획

1. 문서 전용 Draft PR에서 #17을 `Refs`로 연결한다.
2. 후속 작업 에이전트는 `Plans/Runtime_Schema_Architecture.md`를 정보 원본으로 읽고, 중복을 줄이되 배경·대안·Open Questions를 소실하지 않도록 정제한다.
3. 먼저 terminology와 staged IR boundary를 확정하거나 명시적으로 defer한다.
4. `Pool/Application Contract`, `Wire IR`, `Binding`, `Linked Schema IR`, `Runtime Image IR`, Target Capability의 최소 contract를 ADR/RFC 수준으로 구체화한다.
5. 기존 `Ir.fs`와 AOT C backend의 compatibility/migration 전략을 결정한다.
6. runtime-image v1의 최소 vertical slice와 Pool ABI 전략을 확정한 뒤에만 구현 child issue를 분할한다.
7. Accepted architecture와 child issue 링크가 #17에 반영된 뒤 RFC close 여부를 판단한다.
