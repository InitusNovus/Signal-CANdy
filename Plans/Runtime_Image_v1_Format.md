# Runtime Image v1 (.scimg) — Byte Format Specification

> **상태:** 확정 (D-16 세부화). `src/Signal.CANdy.Core/Scimg.fs`(writer/reader)와 `runtime/c99/`(executor)는 **모두 이 문서를 따른다**. 양쪽이 어긋나면 이 문서를 기준으로 수정한다.
>
> **상위 결정:** `Plans/Runtime_Schema_Decisions.md` D-16, D-17, D-06, D-09, D-14, D-19
>
> **추적 이슈:** #17

## 1. 개요

- 모든 다중 바이트 정수는 **little-endian**.
- 모든 offset은 **이미지 시작 기준 u32**.
- 테이블 내부 인덱스·개수는 **u16**.
- packed struct cast 금지 — reader/executor 모두 바이트 단위 조립으로 필드를 읽는다.
- 동일 Linked Schema IR → 항상 byte-identical 이미지 (정렬 규칙 §7).
- 패딩은 항상 `0x00`.

## 2. 이미지 전체 구조

```text
+-----------------------------+ offset 0
| Header (32 bytes, §3)       |
+-----------------------------+ offset 32
| Section Directory (§4)      | 4 entries × 8 bytes = 32 bytes
+-----------------------------+
| MSG  section (§5)           | dir[0]
+-----------------------------+
| PRG  section (§6)           | dir[1]
+-----------------------------+
| CNV  section (§7)           | dir[2]
+-----------------------------+
| SYM  section (§8)           | dir[3]
+-----------------------------+
| CRC32 (4 bytes, §9)         | 이미지 마지막 4바이트
+-----------------------------+
```

`total_size` = 마지막 바이트까지의 크기(CRC 포함). 모든 section offset은 4바이트 정렬.

## 3. Header (32 bytes 고정)

| 오프셋 | 크기 | 필드 | 값 |
|---|---|---|---|
| 0 | 8 | magic | ASCII `"SCIMG01\0"` (`53 43 49 4D 47 30 31 00`) |
| 8 | 2 | u16 format_version | `1` |
| 10 | 2 | u16 flags | `0` (-reserved) |
| 12 | 4 | u32 total_size | CRC 포함 전체 크기 |
| 16 | 2 | u16 message_count | MSG 엔트리 수 |
| 18 | 2 | u16 signal_count | PRG 엔트리 수 |
| 20 | 2 | u16 conversion_count | CNV 엔트리 수 |
| 22 | 2 | u16 reserved | `0` |
| 24 | 8 | reserved | 모두 `0` |

v1 검증 규칙: `section_count` 필드는 없다 — v1은 항상 4개 section(MSG, PRG, CNV, SYM)을 디렉터리 순서대로 갖는다. flags/reserved 필드는 0이어야 한다(아니면 거부).

## 4. Section Directory (4 entries × 8 bytes)

위치: offset 32. 엔트리 순서가 section type을 식별한다(별도 type 태그 없음).

| 엔트리 | layout |
|---|---|
| dir[0] MSG | u32 offset, u32 size |
| dir[1] PRG | u32 offset, u32 size |
| dir[2] CNV | u32 offset, u32 size |
| dir[3] SYM | u32 offset, u32 size |

검증: 각 offset ≥ 64, offset+size ≤ total_size-4, offset 4바이트 정렬, size는 §5-§8의 배수식과 일치. section은 순서대로 배치되며 각 section 끝은 다음 offset까지 0 패딩.

## 5. MSG — RX Message Index

`message_count` 엔트리 × **8 bytes**. **CAN id 오름차순 정렬**(중복 불가).

| 오프셋 | 크기 | 필드 |
|---|---|---|
| 0 | 4 | u32 can_id — 표준: 11-bit id. 확장: 0x80000000 \| 29-bit id |
| 4 | 2 | u16 program_count — 이 메시지의 PRG 엔트리 수 |
| 6 | 2 | u16 program_index — PRG 내 시작 인덱스 (dense) |

검증: program_index + program_count ≤ signal_count, 메시지 간 프로그램 범위는 겹치지 않고 순서대로(정렬 규칙 §10), program_count ≥ 1.

## 6. PRG — Signal Descriptor Programs

`signal_count` 엔트리 × **16 bytes**.

| 오프셋 | 크기 | 필드 |
|---|---|---|
| 0 | 2 | u16 start_bit — §6.1 비트 좌표계 |
| 2 | 2 | u16 length_bits (1..64) |
| 4 | 1 | u8 byte_order — 0 = little(Intel), 1 = big(Motorola) |
| 5 | 1 | u8 is_signed — 0/1 |
| 6 | 2 | u16 conversion_index — CNV 인덱스 (0은 항상 identity) |
| 8 | 2 | u16 slot_index — 대상 pool slot (dense, 0부터) |
| 10 | 2 | u16 mux_selector_slot — 이 신호를 게이트하는 selector 신호의 slot. `0xFFFF` = 무조건 |
| 12 | 4 | u32 mux_expected_value — selector raw 값 하위 32비트와 비교 |

검증: start_bit + length_bits ≤ 512 (64바이트 × 8), length_bits ≥ 1, byte_order ≤ 1, is_signed ≤ 1, conversion_index < conversion_count, slot_index < signal_count, (mux_selector_slot == 0xFFFF) ⟺ (mux_expected_value == 0xFFFFFFFF), mux_selector_slot == 자기 slot_index 불가, byte_order=big이면 start_bit/length_bits가 byte 경계 정렬이 **아니어도** 된다(비트 단위 좌표계가 처리).

### 6.1 비트 좌표계 (정규화 규칙)

payload를 little-endian 비트 스트림으로 본다: `bit_index = byte_index*8 + bit_in_byte`, `bit_in_byte`는 LSB-first(0 = 최하위 비트).

- **Intel(little)**: 신호 LSB가 `start_bit`에 위치. extraction: `((little-endian payload as u64 window) >> start_bit) & mask` — 구현은 바이트 열에서 직접 조립(§11 참조).
- **Motorola(big)**: **정규화된** LSB 위치를 `start_bit`에 저장한다. DBC의 Motorola start bit(MSB 좌표계)를 Wire IR 단계에서 다음 규칙으로 변환: 신호 비트들을 CAN 프레임 바이트 열에 배치한 뒤(big-endian 비트 순서로 이어 붙인 형태), 그 비트열 전체가 놓인 프레임 내 **최하위 비트 위치**를 LSB-first 좌표로 기록하고, 길이는 length_bits. 즉 이미지에는 "이 신호는 프레임 비트 [start_bit, start_bit+length)를 big-endian 순서로 읽는다"는 사실만 남는다. DBC vendor 좌표계(1-based MSB 등)는 Wire IR 어댑터에서 정규화 완료.
- big 순서 extraction: 프레임 비트 구간 [start_bit, start_bit+length)를 취하고, 그 구간을 MSB-first로 해석해 정수 값을 만든다.

두 order 모두 비트 구간이 프레임 경계를 넘지 않는 한 임의 비트 정렬을 허용한다.

## 7. CNV — Conversion Table

`conversion_count` 엔트리 × **24 bytes**. 링커가 (kind, factor, offset) 튜플을 dedup/intern한다.

| 오프셋 | 크기 | 필드 |
|---|---|---|
| 0 | 1 | u8 kind — 0 = identity, 1 = affine |
| 1 | 7 | 패딩 `0` |
| 8 | 8 | f64 factor (IEEE754 LE) |
| 16 | 8 | f64 offset (IEEE754 LE) |

규칙: 엔트리 0은 항상 `{kind=0, factor=1.0, offset=0.0}`. identity 엔트리는 factor=1.0, offset=0.0이어야 한다(아니면 거부). affine은 factor ≠ 0.0을 요구(0이면 거부). decode 계산: `phys = raw * factor + offset` (IEEE754 double, 곱셈 1회 + 덧셈 1회 — .NET/C99 SSE2 양쪽 비트 동일).

## 8. SYM — Debug Symbol Section

구조 (Inspector/Host 전용; runtime은 내용을 parse하지 않고 bounds만 검사):

```text
u16 signal_name_count   (= signal_count)
u16 message_name_count  (= message_count)
signal names: signal_name_count × (u16 byte_len + UTF-8 bytes)
message names: message_name_count × (u16 byte_len + UTF-8 bytes)
```

- signal names 순서: slot_index 오름차순.
- message names 순서: MSG 디렉터리 순서(CAN id 순).
- 개별 이름 길이 1..255바이트, UTF-8, NUL 포함 금지.
- 패딩으로 section 끝을 4바이트 정렬.

## 9. CRC32

CRC-32/ISO-HDLC (zlib와 동일): 다항식 0xEDB88320(reflected), init 0xFFFFFFFF, 입력/출력 반사, 최종 XOR 0xFFFFFFFF. 계산 범위: 이미지 `[0, total_size-4)`. 저장: 이미지 마지막 4바이트(Little-endian).

## 10. 결정론 규칙 (writer)

1. MSG 테이블: CAN id 오름차순.
2. 메시지 내 PRG 엔트리: (a) mux selector 프로그램(unconditional이면서 다른 프로그램의 mux_selector_slot이 참조하는 slot에 쓰는 프로그램)이 **첫 번째**, (b) 이후 start_bit 오름차순, tie-break slot_index 오름차순.
3. CNV: 엔트리 0 = identity 고정, 이후 첫 등장 순서대로 intern(정렬하지 않음 — Linked Schema IR의 신호 순서가 곧 첫 등장 순서).
4. slot_index: Linked Schema IR의 binding 순서(pool definition 순서 중 바인딩된 신호)대로 0부터 dense 할당.
5. 패딩 0, 타임스탬프/경로/빌드 정보 미포함.
6. 같은 Linked Schema IR → 동일 바이트열 (테스트로 강제).

## 11. Runtime decode 의미 (executor 구현 계약)

1. `sc_schema_open`: §3-§9 검증 전부(크기·정렬·범위·정렬식·CRC). 실패 시 에러 코드, 부분 상태 없음.
2. `sc_decode(frame)`:
   - MSG에서 can_id(IDE 플래그 반영) 검색(순차 스캔 허용, 정렬 덕에 이분 탐색 가능). miss → `SC_OK_NO_MATCH`(에러 아님).
   - 프레임 payload 길이 `len`(0..64). 각 프로그램: `start_bit + length_bits > len*8`이면 그 신호만 **skip**(플래그 불변, 에러 아님).
   - unconditional 프로그램을 먼저 실행(테이블 순서가 이미 보장). muxed 프로그램: `pool[mux_selector_slot].raw & 0xFFFFFFFF == mux_expected_value`인 경우만 실행.
   - extraction(§6.1) → 필요 시 부호 확장 → conversion(§7) → `slot.raw = 물리값 비트열(f64는 IEEE754 double, 정수 storage는 정수값)` — **slot 표현은 storage type에 따름(D-06)**: 정수 storage는 u64 정수 원값, f32는 float32 값의 u64 확장, f64는 double 비트열.
   - flags 갱신(D-19): `valid=1`, `updated=1`, 이전 valid 상태에서 raw가 달라졌으면 `changed=1`(초기 invalid→valid는 changed=1 아님 — 최초 값 확정으로 본다. 단 이전에 valid였고 값이 같으면 changed=0).
3. 상태 비의존: `sc_decode`는 pool 외부 상태를 쓰지 않는다(v1 TX/counter 없음).

## 12. 리소스 상한 (v1)

| 항목 | 상한 |
|---|---|
| message_count | 4096 |
| signal_count | 8192 |
| conversion_count | 1024 |
| 개별 이름 | 255 바이트 |
| total_size | 1 MiB |

상한 초과 이미지는 `sc_schema_open`/reader에서 거부. (링커도 동일 상한 적용.)

## 13. 에러 코드 매핑 (참고)

reader(F#)와 runtime(C)는 공통 분류를 유지한다: magic 불일치, version 불일치, 크기/범위 위반, 정렬 위반, 테이블 수 불일치, CRC 불일치, 상한 초과. 각 구현의 에러 케이스 이름은 다르나 분류는 1:1 대응시킨다.
