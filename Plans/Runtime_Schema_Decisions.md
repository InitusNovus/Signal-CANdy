# Runtime Schema v1 — Architecture Decisions

> **상태:** DECIDED / DEFERRED 기록 (patch-forward)
>
> **추적 이슈:** #17 — `[RFC] Shared semantic IR and pool-bound runtime schema images`
>
> **상위 문서:** `Plans/Runtime_Schema_Architecture.md` (이하 RFC)
>
> **목적:** RFC §31의 미결정 질문 중 첫 vertical slice(RFC §34의 1~7단계)에 필요한 항목을 DECIDED 또는 DEFERRED로 확정한다. 각 항목은 질문·결정·근거·가역성을 함께 기록하며, 결정이 바뀌면 이 문서를 patch-forward한다. RFC 본문의 배경·대안은 수정하지 않는다.
>
> **표기:** `D-NN` = 결정 번호. `DECIDED`는 v1 구현이 이 결정을 따른다는 뜻이고, `DEFERRED`는 v1 범위 밖으로 미루되 재검토 트리거를 명시한다.

---

## 0. 이 문서의 지위

이 문서는 RFC §31의 모든 질문 그룹(Terminology, Pool ABI, IR, Authoring, Numeric, Runtime image, Runtime behavior, Host/deployment)을 다룬다. 첫 vertical slice에 필요한 것만 DECIDED이고, 나머지는 DEFERRED + 트리거다. 구현 단위(차일드 이슈)는 이 문서의 결정을 전제로 분할된다.

---

## 1. Terminology / Product boundary

### D-01 `Pool Contract` 용어 확정 — DECIDED

- **질문:** `Pool Definition` / `Pool Contract` / `Application Contract` 중 최종 명칭?
- **결정:** 계층 분리. **Pool Definition** = 사람이 작성하는 입력 포맷(JSON). **Pool Contract IR** = 컴파일된 내부 모델(F# 레코드). **Application Contract** = Pool Contract + 정책(ownership, validity, freshness)을 아우르는 포괄 용어로 문서에서만 사용.
- **근거:** 입력 포맷과 컴파일된 모델이 같은 이름이면 staged IR 논의(RFC §10)에서 혼란. 포괄 용어는 별도 타입 없이 문서 개념으로 족하다.
- **가역성:** 높음 — 명명만의 문제.

### D-02 제품 기술 변경 시점 — DEFERRED

- **트리거:** runtime backend vertical slice가 main에 promote될 때 README/README.ko의 제품 문구를 갱신한다. 그 전에 바꾸면 문서-구현 불일치만 만든다.

### D-03 runtime component/repository 배치 — DECIDED (v1)

- **질문:** runtime component를 본체에 둘 것인가 분리할 것인가?
- **결정:** v1은 본체 `runtime/c99/` 아래에 둔다 (RFC §32 shape 참조). 별도 repo/NuGet 분리는 하지 않는다.
- **근거:** vertical slice 단계에서 분리하면 CI·버전 동기화 비용만 발생. differential test가 같은 repo에서 도는 것이 검증에 유리.
- **가역성:** 중간 — 디렉터리 이동은 기계적이지만 하위 호환 공표 여부에 따라 비용이 달라짐. 분리 시점에서 별도 결정.

---

## 2. Module layout (v1 vertical slice)

### D-04 새 모듈은 `src/Signal.CANdy.Core/` 내부 파일로 추가 — DECIDED

- **질문:** RFC §32의 `Signal.CANdy.Compiler` / `Signal.CANdy.RuntimeImage` 신규 프로젝트를 지금 만들 것인가?
- **결정:** v1 vertical slice는 기존 `Signal.CANdy.Core` 프로젝트에 모듈 파일로 추가한다: `Pool.fs`(Pool Contract IR + manifest), `Wire.fs`(Wire IR + Ir 어댑터), `Binding.fs`(Binding IR), `Linked.fs`(Linked Schema IR + linker), `Scimg.fs`(Runtime Image IR + binary writer/reader/inspector). C 런타임은 `runtime/c99/`.
- **근거:** 프로젝트 분할은 packaging 결정(NuGet boundary, D-03와 동일 지연 사유). 모듈 경계를 파일로 먼저 증명하고, 경계가 안정화되면 프로젝트로 이동하는 비용은 기계적이다.
- **가역성:** 높음 — 파일 이동 + fsproj 업데이트.

### D-05 기존 `Ir.fs`는 유지, Wire IR는 별도 모듈 — DECIDED

- **질문:** 현재 `Ir.fs`를 Wire IR로 rename/migrate할 것인가?
- **결정:** `Ir.fs`는 현 공개 API 그대로 유지하고, `Wire.fs`가 정규화된 Wire IR 타입과 `Ir → Wire IR` 어댑터를 제공한다. rename/제거는 후속 세대에서 별도 마이그레이션으로 결정.
- **근거:** 공개 API 호환(RFC §30-3, 기존 AOT backend 보존)과 staged IR 도입을 동시에 만족하는 최소 경로.
- **가역성:** 높음.

---

## 3. Pool ABI v1

### D-06 uniform slot pool — DECIDED

- **질문:** typed struct / uniform slot / hybrid 중 v1은 무엇인가?
- **결정:** **uniform slot pool**. 슬롯 = `{ uint64 raw_value; uint32 flags; }` 12바이트(정렬은 C 런타임이 `sc_slot_t`로 정의). 값은 신호 storage type 규칙(u/i 8..64는 정수 원값, f32는 IEEE754 단정도 비트열, f64는 배정도 비트열)대로 `raw_value`에 저장. flags 하위 바이트에 `valid`(bit0), `updated`(bit1), `changed`(bit2) 배치.
- **근거:** 스키마 hot-swap·동적 바인딩에서 offset 불변(RFC §30-12,13 원칙의 직접적 결과). typed struct는 ABI 굳히기가 되어 되돌리기 어렵다.
- **가역성:** 중간 — slot 표현은 pool ABI 호환성 검사 대상이므로 v1 확정 후에는 image version으로 관리.

### D-07 pool quality metadata는 side table — DECIDED (v1에서는 미포함)

- **질문:** validity/freshness 등 quality metadata를 value struct에 붙일지 side table로 둘지?
- **결정:** 개념적으로 side table(= slot flags)이며, v1은 flags의 valid/updated/changed만 구현. freshness timestamp는 DEFERRED(D-18).
- **근거:** slot 크기 균일 유지.
- **가역성:** 높음.

### D-08 static/dynamic region — DEFERRED

- **트리거:** dynamic tagged pool region(RFC §28.1)을 수용할 때. v1 image에는 region 개념 없음.

### D-09 semantic ID namespace — DECIDED

- **결정:** semantic signal id는 **uint32**, 저자가 부여·소유. image 내에서 dense binding index(u16)와 분리. namespace version 정책(전역 레지스트리 등)은 v1 범위 밖 — 로컬 프로젝트 내 유일성만 검증.
- **근거:** RFC §5의 4-ID 분리 원칙(semantic id / binding index / pool slot / CAN id)을 그대로 코드화.
- **가역성:** 높음.

---

## 4. Staged IR 경계 (v1 구현 스코프)

### D-10 v1 파이프라인 확정 — DECIDED

- **결정:** v1은 RFC §10 계층 중 다음만 구현한다:

```text
examples DBC --(기존 Dbc.fs)--> Ir --(Wire.fs 어댑터)--> Wire IR
pool.json --(Pool.fs)--> Pool Contract IR
Wire IR + Pool Contract IR + binding.json --(Binding.fs/Linked.fs linker)--> Linked Schema IR
Linked Schema IR --(Scimg.fs lowering)--> Runtime Image IR --writer--> .scimg
.scimg --reader/inspector--> 검증/요약
```

- Source AST/DTO 계층, F# DSL, Runtime Image IR 공개 노출 여부는 v1에서 미결정 상태 유지(DEFERRED).
- **근거:** vertical slice의 최소 의존 경로. 나머지 계층은 요구가 생길 때 추가.
- **가역성:** 높음 — 추가 계층은 삽입 가능.

### D-11 Linked Schema IR 공유 범위 — DECIDED

- **결정:** AOT C backend는 현행(Codegen.fs, Ir 기반)을 그대로 두고 Linked Schema IR를 소비하지 않는다. differential test가 두 경로의 값 일치를 증명한다. AOT backend의 Linked IR 이관은 후속 세대.
- **근거:** 동시 이관은 리스크가 크고 differential test 전제가 사라진다.
- **가역성:** 높음.

---

## 5. Authoring 포맷 v1

### D-12 Pool Definition과 Binding은 JSON — DECIDED

- **질문:** canonical authoring format은 JSON/YAML/F# DSL 중 무엇인가?
- **결정:** v1 입력은 **JSON** (pool definition, binding). 파싱은 System.Text.Json으로 엄격하게(unknown key 거부). canonical JSON 스키마 문서화는 fixture + inspector 산출물로 대체하고 정식 JSON Schema 파일은 DEFERRED(D-24).
- **근거:** host tooling·테스트 파이프라인과의 마찰 최소. DBC는 계속 frontend.
- **가역성:** 높음.

### D-13 F# shallow DSL — DEFERRED

- **트리거:** JSON authoring 경험이 축적되고 반복 패턴이 확인될 때 별도 제안.

---

## 6. Numeric semantics v1

### D-14 affine conversion (double) — DECIDED

- **질문:** exact decimal/rational, rounding/saturation 기본값?
- **결정:** v1 conversion은 **identity 또는 affine(`factor: float64`, `offset: float64`)** 로 제한. 계산식은 단일 곱셈+덧셈 `phys = raw * factor + offset` (또는 역변환 `raw = (phys - offset) / factor` — v1 decode는 정방향만). decimal literal은 Wire IR에 문자열로 보존하되 image lowering에서 float64로 변환. rounding/saturation 정책, fixed-point plan, rational type은 v1 미포함.
- **근거:** DBC scale/offset 직행. 곱셈+덧셈 한 번의 IEEE754 double은 .NET과 C99(SSE2) 양쪽에서 비트 동일하므로 differential test가 자명해진다 — 이것이 v1에서 rational을 미루는 결정적 이유다.
- **가역성:** 중간 — conversion 표현은 image format의 conversion table과 묶여 version 관리.

### D-15 단위(unit) — DECIDED (v1: 문자열 + 일치 검사)

- **결정:** unit은 문자열이며, binding 시 pool unit과 wire unit이 다르면 링커 에러. 단위 변환 계산은 v1 미포함(단위가 같다는 전제下 affine만).
- **근거:** type-safe unit system은 별도 설계 과제.
- **가역성:** 높음.

---

## 7. Runtime Image v1 binary format invariants

### D-16 이미지 헤더·인코딩 — DECIDED

- **결정:** 다음을 확정한다 (세부 필드 레이아웃은 `Scimg.fs` 구현과 동기화하여 유지):
  - **magic:** ASCII `"SCIMG01\0"` 8바이트 (magic+v1을 한 번에 식별).
  - **정수 인코딩:** 이미지 내 모든 다중 바이트 정수는 **little-endian**.
  - **offset/length:** section directory의 offset·size는 **u32** (이미지 전체가 64KiB를 넘을 수 있음). 테이블 내부 인덱스·개수는 **u16** (신호·메시지 수 상한 65535).
  - **section 최소 집합(v1):** `HDR`(header), `DIR`(section directory), `MSG`(RX message index: bus/IDE/CAN id/길이제약 → program index), `PRG`(signal descriptor 배열: start bit, length, byte order, signed, conversion index, mux, target slot index), `CNV`(dedup affine conversion table), `SYM`(선택: 디버그 심볼 — v1은 포함, inspector가 사용), `FTR`(footer: CRC32).
  - **CRC:** CRC-32(IEEE 802.3, 반사, zlib 다항식 0xEDB88320) — 이미지 전체에서 마지막 4바이트(CRC 필드 자신)를 제외한 범위.
  - **packed struct cast 금지:** reader(C#)/runtime(C) 모두 바이트 단위 safe accessor로 읽는다(RFC §14.2).
  - **determinism:** 테이블 정렬은 (MSG: CAN id 오름차순, PRG: (message index, start bit) 순), padding은 0, 타임스탬프·경로 미포함. 동일 Linked Schema는 항상 byte-identical.
- **근거:** RFC §14 후보의 최소 실행 집합. TX index/CRC-counter profile section은 v1 미포함(OUT-OF-SCOPE).
- **가역성:** 낮음 — 이미지 format은 version 관리 대상. v1 확정 후 변경은 v2 필드/magic으로.

### D-17 unknown optional section 정책 — DECIDED

- **결정:** v1 reader는 directory의 unknown section type을 만나면 **거부**(에러). skip 정책은 optional section이 실제 생길 때 재결정.
- **근거:** 보수적 검증이 malformed fuzzing·보수성 모두에 유리. skip 허용은 나중에 켤 수 있다.
- **가역성:** 높음(거부→허용 완화는 가능, 역방향은 breaking).

---

## 8. Runtime behavior v1

### D-18 decode atomicity / freshness / multiple writers — DECIDED (v1 축소)

- **결정:** v1은 **single-writer 가정**: `sc_decode`는 caller 스레드 1개에서만 호출된다고 문서화. decode는 각 신호의 pool slot에 직접 기록하며 별도 scratch double-buffer를 요구하지 않는다(스택 임시값만 사용). torn-read 방지(critical section)는 caller 책임. freshness expiry, timestamp granularity, multiple-writer arbitration는 v1 미포함.
- **근거:** vertical slice의 검증 가능한 최소 집합. API shape(`sc_decode` 시그니처)는 `now` 파라미터를 이미 확보 중이므로(RFC §16) freshness 추가가 비파괴적.
- **가역성:** 높음 — 상위 기능 추가는 새 flags/필드로.

### D-19 valid/updated/changed — DECIDED

- **결정:** decode 성공 시 slot flags에 `valid=1, updated=1` 설정. `changed`는 이전 valid 값과 새 값의 비트 패턴 비교로 설정(v1 구현: raw_value 비트가 다르면 changed=1). flags set 규칙은 문서 + 테스트로 고정.
- **근거:** RFC §17의 최소 상태 비트.
- **가역성:** 중간.

### D-20 TX encode — DECIDED (v1 제외, brief out-of-scope 확정)

- **결정:** 이 slice는 RX decode만. TX/counter는 후속(이슈 #17 차일드 후보 11번).
- **근거:** brief의 out-of-scope 확정.

---

## 9. Host / deployment (v1)

### D-21 project manifest / target capability 파일 — DEFERRED

- **결정:** v1은 별도 project manifest 없이 CLI 인자 조합으로 구성. 정식 manifest 스키마는 이미지 포맷이 안정된 뒤.
- **트리거:** CLI 인자 조합이 3개 이상의 입력 조합을 요구할 때.

### D-22 binding lock/fingerprint — DEFERRED

- **트리거:** binding에 대한 drift detection 요구가 생길 때(L reviewed binding 정책, RFC §8).

### D-23 map/report JSON compatibility — DEFERRED

- **v1 대체:** inspector가 이미지 요약 JSON을 출력하나 호환성 미보증.

### D-24 정식 JSON Schema 파일 (pool/binding) — DEFERRED

- **v1 대체:** fixture JSON + F# 파서의 엄격한 unknown-key 거부가 계약 역할.

### D-25 device-side capability 조회 — DEFERRED

- **트리거:** host GUI/transport 연동 설계 시.

---

## 10. 결정 요약

| # | 주제 | 결정 |
|---|------|------|
| D-01 | 용어 | Pool Definition(입력) / Pool Contract IR(컴파일) / Application Contract(포괄 개념) |
| D-02 | 제품 문구 | DEFERRED — main promote 시 |
| D-03 | runtime 배치 | v1: 본체 `runtime/c99/` |
| D-04 | 모듈 배치 | v1: `Signal.CANdy.Core` 내 `Pool/Wire/Binding/Linked/Scimg.fs` |
| D-05 | 기존 Ir.fs | 유지 + `Wire.fs` 어댑터 |
| D-06 | pool ABI | uniform slot: `u64 raw + u32 flags` |
| D-07 | quality metadata | side table(flags), v1: valid/updated/changed |
| D-08 | static/dynamic region | DEFERRED |
| D-09 | semantic id | u32, 저자 소유; dense binding index(u16)와 분리 |
| D-10 | v1 파이프라인 | DBC→Ir→Wire IR + pool.json + binding.json → Linked → .scimg |
| D-11 | Linked IR 공유 | AOT는 현행 유지, differential test로 값 일치 증명 |
| D-12 | authoring | JSON (엄격 파싱) |
| D-13 | F# DSL | DEFERRED |
| D-14 | conversion | identity/affine (float64, 곱셈+덧셈 1회) |
| D-15 | unit | 문자열 일치 검사만 |
| D-16 | 이미지 v1 | `"SCIMG01\0"`, LE, u32 section offset, u16 인덱스, MSG/PRG/CNV/SYM/FTR, CRC32 |
| D-17 | unknown section | 거부 |
| D-18 | atomicity | single-writer 가정, caller 책임 명시 |
| D-19 | flags | valid/updated/changed 규칙 확정 |
| D-20 | TX | v1 OUT-OF-SCOPE |
| D-21~25 | host/manifest/schema | DEFERRED (트리거 명시) |

---

## 11. 편집 원칙

- 이 문서의 결정이 바뀌면 기존 항목을 지우지 않고 `변경: <내용> (YYYY-MM-DD, 근거)`를 덧붙인다.
- 구현(코드)과 이 문서가 어긋나면 구현 또는 문서 중 하나를 즉시 맞춘다. 방치된 불일치는 결정이 아니다.
- 바이너리 포맷 세부(D-16)는 `Scimg.fs`의 포맷 주석과 1:1로 동기화한다.
