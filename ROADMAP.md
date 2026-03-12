# ROADMAP — Signal-CANdy 펌웨어 실전 투입 (Production Ready)

> **목표**: 코드 생성기의 **신뢰성, 유지보수성, 테스트 커버리지**를 펌웨어 양산 수준으로 끌어올린다.
> **근거 문서**: `Analysis/Codebase_Analysis.md` (2026-02-12 코드베이스 심층 분석)
> **진행 관리**: 완료 항목은 `[x]`로 표시. 작업 보고서는 `Reports/` 폴더에 기록.

---

## [Critical] — 즉시 수정 (펌웨어 오작동 위험)

> 이 항목들은 **현재 코드가 잘못된 C 코드를 에러 없이 생성할 수 있는** 결함입니다.
> 수정하지 않으면 생성된 펌웨어가 런타임에 오동작할 수 있습니다.

### C-1. 파싱 예외 삼킴(Exception Swallowing) 수정

- [x] **C-1a.** `Dbc.fs` — `tryBuildSignalMetaMap` (라인 135~163): `with _ -> Map.empty`를 제거하고, 실패 시 경고를 수집하거나 `Result`로 전파하도록 변경
- [x] **C-1b.** `Dbc.fs` — `tryBuildSignalMuxMap` (라인 99~133): 동일하게 예외 삼킴 제거
- [x] **C-1c.** `Dbc.fs` — `tryBuildValueTableMap` (라인 182~210) 및 `buildIdNameMap` (라인 165~180): 동일하게 예외 삼킴 제거
- [x] **C-1d.** `Dbc.fs` — `validateDuplicateIdsFromText` (라인 76~97): `with _ -> None` → 파일 읽기 실패를 `ParseError.IoError`로 전파
- [x] **C-1e.** 모든 수정 후 기존 테스트(`dotnet test`) 통과 확인 + 예외 상황 재현 테스트 추가

**위험 시나리오**: BOM 포함 UTF-16 DBC → 텍스트 스캔 실패 → 빈 metaMap → 모든 시그널이 `(isSigned=false, Little)` 폴백 → **잘못된 엔디언/부호로 C 코드 생성** (에러 메시지 없음)

### C-2. `Api.generateFromPaths` 에러 타입 정보 손실 보완

- [x] **C-2a.** `Errors.fs`에 통합 에러 DU 추가: `type GenerateError = Parse of ParseError | Validation of ValidationError | CodeGen of CodeGenError`
- [x] **C-2b.** `Api.fs` — `generateFromPaths` 반환 타입을 `Task<Result<GeneratedFiles, GenerateError>>`로 변경
- [x] **C-2c.** `Library.fs` (Facade) — `GenerateFromPathsAsync`에서 `GenerateError` 패턴 매칭으로 적절한 Exception 타입 매핑
- [x] **C-2d.** CLI (`Program.fs`) — `GenerateError` 대응 에러 메시지 출력 업데이트
- [x] **C-2e.** 기존 테스트 통과 확인 + 에러 타입 보존 테스트 추가

**현재 문제**: `ParseError` → `CodeGenError.Unknown(sprintf ...)` 변환으로 구조화된 에러 정보가 문자열로 직렬화됨. Facade 소비자가 파싱 실패/설정 오류/코드 생성 오류를 구분 불가.

---

## [High] — 아키텍처 개선 및 테스트 확보

> 리팩토링의 **안전망(safety net)**을 먼저 구축하고, 구조적 부채를 해소합니다.

### H-1. Core 테스트 스위트 구축

- [x] **H-1a.** `tests/Signal.CANdy.Core.Tests/` 프로젝트 생성 (xUnit + FsUnit, Core 프로젝트 참조)
- [x] **H-1b.** `Dbc.parseDbcFile` 테스트: 정상 DBC, 중복 ID, 시그널 오버랩, DLC 초과, 멀티플렉서, VAL_ 테이블
- [x] **H-1c.** `Config.loadFromYaml` / `Config.validate` 테스트: 유효/무효 설정, snake_case/PascalCase, 기본값 추론
- [x] **H-1d.** `Codegen.generate` 테스트: IR → C99 코드 생성 검증 (최소한 파일 생성 여부 + 핵심 패턴 포함 여부)
- [x] **H-1e.** `Api.generateFromPaths` 엔드투엔드 테스트: DBC 파일 입력 → 생성 파일 출력
- [x] **H-1f.** 에지 케이스: 빈 DBC(메시지 없음), Motorola LSB, 64비트 시그널, 부호 있는 시그널
- [x] **H-1g.** CI(`ci.yml`)에 Core 테스트 실행 단계 추가

### H-2. Generator → Core 통합 (코드 중복 제거)

> **선행 조건**: H-1 (Core 테스트 스위트) 완료 후 착수

- [x] **H-2a.** Generator.fsproj에 Core 프로젝트 참조 추가
- [x] **H-2b.** Generator/Program.fs가 Core API를 직접 호출하도록 변경 (Config/Dbc/Codegen/Errors open)
- [x] **H-2c.** Generator의 중복 모듈 제거: `Config.fs`, `Ir.fs`, `Dbc.fs`, `Codegen.Utils.fs`, `Codegen.Message.fs`, `Codegen.Registry.fs`, `Codegen.fs` — fsproj에서 제거 + 디스크에서 삭제
- [x] **H-2d.** Generator/Result.fs → `Compat.fs`로 통합 (Result active pattern + Ir/Config/Dbc/Codegen 역호환 브리지)
- [x] **H-2e.** Generator 기존 테스트(`tests/Generator.Tests/`) 16/16 통과 확인 (Compat.fs 브리지 경유)
- [x] **H-2f.** E2E 검증: Generator CLI + Signal.CANdy.CLI 양쪽 모두 코드 생성 + gcc 빌드 + roundtrip 테스트 통과

### H-3. Facade 에러 매핑 정밀화

> **선행 조건**: C-2 (에러 타입 통합 DU) 완료 후 착수

- [x] **H-3a.** `Library.fs` — `GenerateFromPathsAsync`에서 `GenerateError.Parse` → `SignalCandyParseException`, `GenerateError.Validation` → `SignalCandyValidationException` 매핑
- [x] **H-3b.** Exception 메시지에 DU 케이스 정보 포함 (예: `"[InvalidDbc] Duplicate message ID 100"`)
- [x] **H-3c.** Facade 단위 테스트 추가: 각 에러 경로별 올바른 Exception 타입 발생 확인

---

## [Medium] — 코드 품질 및 유지보수

> 기능에는 영향 없으나, 프로젝트 건강도와 개발자 경험을 개선합니다.

### M-1. 미사용 의존성 제거

- [x] **M-1a.** `Generator.fsproj`에서 `Scriban 6.2.1` 패키지 참조 제거 (H-2c에서 완료)
- [x] **M-1b.** `Generator.fsproj`에서 `Argu 6.1.1` 패키지 참조 제거 (H-2c에서 완료)
- [x] **M-1c.** `Generator.fsproj`에서 `FSharp.SystemTextJson 1.4.36` 패키지 참조 제거 (H-2c에서 완료)
- [x] **M-1d.** `Generator.fsproj`에서 `FsToolkit.ErrorHandling 5.0.1` 패키지 참조 제거 (H-2c에서 완료)
- [x] **M-1e.** `dotnet restore && dotnet build && dotnet test` 통과 확인 (63/63 pass)

### M-2. Dead Code 삭제

- [x] **M-2a.** `Ir.fs` — `SignalType` DU 제거 (Signed | Unsigned | Float — 어디에서도 사용되지 않음)
- [x] **M-2b.** `Core/Library.fs` — 플레이스홀더 파일 내용 점검 (실제 112줄 Facade — 유지)
- [x] **M-2c.** `templates/` 디렉토리 점검 — L-1 Scriban 도입 시 사용 예정, 유지
- [x] **M-2d.** 빌드 및 테스트 통과 확인 (63/63 pass)

### M-3. 코드 생성 문자열 가독성 개선

- [x] **M-3a.** `Codegen.fs` — `utilsHContent`, `utilsCContent`의 매우 긴 단일 문자열을 여러 줄로 분리 (기능 변경 없이 포맷만 개선)
- [x] **M-3b.** `Codegen.fs` — `registryHContent`/`registryCContent`의 헤더 가드/extern C/함수 선언을 가독성 있게 재구성
- [x] **M-3c.** 생성된 C 코드의 바이트 일치(byte-identical) 검증 완료

### M-4. `AGENTS.md` Key Dependencies 테이블 정확도 보정

- [x] **M-4a.** Scriban, Argu, FsToolkit.ErrorHandling, FSharp.SystemTextJson 항목 제거 (M-1에서 패키지 삭제 완료)
- [x] **M-4b.** Generator 통합(H-2) 후 의존성 테이블을 Core 기준으로 갱신 완료

---

## [Low] — 미래 기능 및 편의성

> 현재 기능에는 불필요하나, 장기적 확장성을 위한 항목입니다.

### L-1. 템플릿 엔진 도입 (Scriban)

- [ ] **L-1a.** `Codegen.fs`의 Utils 헤더/소스를 Scriban 템플릿(`.sbn`)으로 마이그레이션 (파일럿)
- [ ] **L-1b.** Message 헤더/소스를 Scriban 템플릿으로 마이그레이션
- [ ] **L-1c.** Registry 헤더/소스를 Scriban 템플릿으로 마이그레이션
- [ ] **L-1d.** Core.fsproj에 Scriban 의존성 추가, `templates/` 디렉토리에 `.sbn` 파일 배치
- [ ] **L-1e.** 생성 결과 동일성 검증 (기존 문자열 연결 대비)

### L-2. CRC/Counter 자동 검증 구현

- [ ] **L-2a.** `Config.CrcCounterCheck` 플래그 활성화 설계
- [ ] **L-2b.** CRC/Counter 시그널에 대한 코드 생성 로직 구현
- [ ] **L-2c.** 검증 테스트 추가

### L-3. valid 비트마스크 자동 확장

- [ ] **L-3a.** 시그널 수 > 32인 메시지에서 `uint64_t` 또는 배열 기반 valid 필드 자동 선택
- [ ] **L-3b.** 매크로 생성 로직 업데이트
- [ ] **L-3c.** 대규모 메시지 테스트 DBC 추가

### L-4. CAN FD 지원

- [x] **L-4a.** 8바이트 초과 페이로드(최대 64바이트) 지원을 위한 IR 확장 (불필요 — `Message.Length: uint16`이 이미 64 지원)
- [x] **L-4b.** `get_bits_le/be`, `set_bits_le/be`의 8바이트 제한 제거 및 `memset` 동적 길이 적용
- [x] **L-4c.** DLC 매핑 테이블 (CAN FD DLC ↔ 실제 바이트 수) 코드 생성

---

## [Tracking] — Oracle 실패해결 플랜 (2026-02-13 수립본)

> 본 섹션은 `Reports/`, `tests/oracle/ORACLE_RESULTS.md`, `tests/oracle/CATEGORY_C_EXCEPTIONS.md` 기준의 **진행 추적용 상태판**입니다.
> 기존 C/H/M/L 항목과 별개로, 오라클 검증 파이프라인 플랜(Task 0~9)의 현재 도달 지점을 기록합니다.

> **Source-of-Truth 우선순위**:
> 1. `Reports/20260312-1530_Oracle_실패해결_완료.md`
> 2. `tests/oracle/ORACLE_RESULTS.md`
> 3. `tests/oracle/CATEGORY_C_EXCEPTIONS.md`
> 4. `.sisyphus/*` (보조 작업 상태; canonical source 아님)

- [x] **O-1. Task 0** 만료 플랜 아카이브 + boulder 갱신 (`.sisyphus/plans/archived/*`, `.sisyphus/boulder.json`, `Reports/20260312-1530_Oracle_실패해결_완료.md`)
- [x] **O-2. Task 1** DbcParserLib 속성 타입 검증 (`Reports/20260312-1235_DbcParserLib_Type_Probe_Test.md`)
- [x] **O-3. Task 2** C 오라클 하네스 템플릿/유틸 구현 (`Reports/20260213_1817_Oracle_하네스_템플릿_구현.md`)
- [x] **O-4. Task 3** Oracle Core Engine/CLI 구현 (`Reports/20260213_1838_Oracle_Core_Engine_구현.md`, `Reports/20260213_1850_Oracle_Core_Engine_구현.md`)
- [x] **O-5. Task 4** tolerance/metadata 비교 로직 구현 (`Reports/20260213_1848_Oracle_Tolerance_Metadata_Comparison.md`)
- [x] **O-6. Task 5** 벡터 생성 오버플로 가드레일 (`Reports/20260312-1235_Oracle_Overflow_Guardrails.md`, commit `e0fc6fa`)
- [x] **O-7. Task 6** cantools 파싱 비호환 DBC 분류 (`Reports/20260312-1235_Oracle_DBC_Parse_Triage.md`)
- [x] **O-8. Task 7** Category C 예외 판정 기준 문서화 (`tests/oracle/CATEGORY_C_EXCEPTIONS.md`, `Reports/20260312-1530_Oracle_Category_C_Exception_5_추가.md`)
- [x] **O-9. Task 8** Oracle pytest 스위트 구축 (`Reports/20260213_1937_Oracle_pytest_스위트_구축.md`)
- [x] **O-10. Task 9** 전체 통합 실행 + 결과 문서화 (`Reports/20260312-1530_Oracle_실패해결_완료.md`, `tests/oracle/ORACLE_RESULTS.md`)

### Oracle 후속 backlog (우선순위순)

- [ ] **B-O1. DBC raw-range detection heuristic**
  - 근거: `tests/oracle/ORACLE_RESULTS.md` Recommendation #1, `tests/oracle/CATEGORY_C_EXCEPTIONS.md` Exception 5
  - 범위: `[min|max]`가 raw-count sentinel로 보이는 경우의 선택적 탐지/정책 결정
  - 상태: **미완료 backlog** (현재 Category C로 분류되어 있으며, 완료 항목으로 간주하지 않음)

- [ ] **B-O2. Oracle multiplex mode**
  - 근거: `tests/oracle/ORACLE_RESULTS.md` Recommendation #2, Exception 1 (`cantools_oracle_limitation`)
  - 범위: `run_oracle.py`가 mux branch를 선택해 skip 없이 검증 가능하도록 확장
  - 상태: **미완료 backlog**

- [ ] **B-O3. Valid bitmask auto-widening**
  - 근거: `tests/oracle/ORACLE_RESULTS.md` Recommendation #3, `tests/oracle/CATEGORY_C_EXCEPTIONS.md` Exception 3
  - 범위: >32 signal 메시지에서 `uint64_t` 또는 배열 기반 valid 필드 자동 선택
  - 상태: **미완료 backlog** (기존 `L-3`와 연결되는 구조 개선 과제)

---

## 작업 순서 의존성 그래프

```
C-1 (예외 삼킴 수정) ──────────────────────────┐
C-2 (에러 타입 통합 DU) ── H-3 (Facade 매핑)    │
                                                 │
H-1 (Core 테스트 구축) ── H-2 (Generator 통합) ──┤── M-1 (미사용 의존성)
                                                 │
M-2 (Dead Code 삭제) ───────────────────────────┘
M-3 (코드 생성 가독성) ── L-1 (Scriban 도입)
```

> **권장 착수 순서**: C-1 → C-2 → H-1 → H-3 → M-1 → M-2 → H-2 → M-3 → M-4 → L-*

---

> **최종 갱신**: 2026-03-12 (기존 완료 항목 상태 유지, Oracle 실패해결 플랜 O-1~O-10 완료 반영, Oracle 후속 backlog B-O1~B-O3 추가, `Reports/` 기준으로 정렬)
> **참조**: `Analysis/Codebase_Analysis.md`, `AGENTS.md`
