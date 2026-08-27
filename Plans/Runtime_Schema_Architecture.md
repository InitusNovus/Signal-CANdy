# Runtime-Loadable Schema Architecture — Exhaustive Design Handoff

> **상태:** RFC 초안 이전의 설계 원자료 / 정제 전 handoff
>
> **추적 이슈:** #17 — `[RFC] Shared semantic IR and pool-bound runtime schema images`
>
> **목적:** 이 문서는 구현을 즉시 지시하는 확정 사양이 아니다. 2026-08-26에 논의된 방향, 배경, 설계 논리, 대안, 우려, 미결정점을 가능한 한 손실 없이 레포에 전달하기 위한 장문 기록이다. 이후 실제 작업 에이전트는 이 문서를 정제하고, 모순을 해소하고, ADR/RFC/세부 이슈로 분할할 수 있다.
>
> **편집 원칙:** 정제 과정에서 중복 문장을 줄이는 것은 가능하지만, 결론만 남기고 배경·대안·실패 가능성·미결정점을 삭제하지 않는다. 중요한 결정이 바뀌면 기존 논리를 지우기보다 `Decisions`, `Alternatives`, `Open Questions`를 patch-forward한다.

---

## 0. 이 문서를 읽는 법

이 문서는 의도적으로 일반적인 RFC보다 길고, 일부 내용은 서로 다른 설계 대안을 동시에 보존한다. 현재 단계의 우선순위는 다음과 같다.

1. 대화에서 나온 정보를 잃지 않는다.
2. 확정된 방향과 아직 정하지 않은 방향을 구분한다.
3. 기존 Signal-CANdy 자산을 폐기한다고 성급히 단정하지 않는다.
4. 구현 에이전트가 원래 문제의식과 제약을 복원할 수 있게 한다.
5. 최종 binary format이나 public API를 이 문서만으로 동결하지 않는다.

따라서 `권장`, `작업 가설`, `후보`, `미결정`이라는 표현을 구분해서 읽어야 한다.

---

## 1. 한 문장 요약

Signal-CANdy를 단순한 **DBC → C99 정적 코드 생성기**에서 다음과 같은 **다중 frontend schema compiler/linker**로 확장하는 방향을 검토한다.

> 고정된 application-facing Signal Process Image / Pool ABI 위에, Host가 생성하고 flash에 교체 저장할 수 있는 CAN Wire Schema Image를 allocation-free C99 runtime이 실행한다. 기존 AOT C backend는 제거하지 않고 동일한 semantic model의 다른 backend이자 reference implementation으로 유지한다.

이 방향에서 Signal-CANdy가 생성하거나 정의할 핵심 산출물은 다음과 같다.

- application pool용 정적 C (`sc_pool.gen.h`, `sc_pool.gen.c` 등)
- Host가 읽는 `pool.manifest.json`
- DBC 또는 다른 입력을 정규화한 Wire Model
- Pool과 Wire를 명시적으로 잇는 Binding
- Target capability와 resource limits를 반영한 linked schema
- device flash에 들어갈 versioned runtime image (`.scimg` 가칭)
- Host용 symbol/map/report/normalized JSON
- runtime image를 해석하는 allocation-free C99 runtime

---

## 2. 현재 프로젝트와 문제의식

### 2.1 현재 Signal-CANdy의 중심

현재 프로젝트는 대체로 다음 파이프라인을 갖는다.

```text
DBC
  ↓ parse
현재 Ir.fs의 Message / Signal 중심 모델
  ↓ validate / codegen
portable C99 codec source
```

현재 `src/Signal.CANdy.Core/Ir.fs`는 다음과 같은 wire-level 정보를 이미 표현한다.

- message ID와 Standard/Extended 여부
- payload length
- signal bit 위치와 길이
- Little/Big Endian
- signed/unsigned
- factor/offset/min/max/unit
- multiplexing
- value table
- receiver/sender
- CRC/counter metadata 및 mode

또한 `Api.generateCode`가 `Ir`을 입력받기 때문에, parser와 backend 사이의 경계 자체는 이미 존재한다. 이 경계는 새 runtime-image backend를 추가할 수 있는 출발점이다.

### 2.2 정적 parsing의 가치 변화

초기 Signal-CANdy는 DBC를 엄격하게 파싱하고 C를 생성하는 것 자체가 큰 가치였다. 그러나 현재 문제의식은 다음과 같다.

- DBC, 사양서, Excel, 자연어 문서 등의 **정적인 해석/초안 생성은 LLM에 대부분 맡겨도 될 수 있다.**
- 그렇다고 embedded target이 검증되지 않은 LLM 출력을 직접 실행해서는 안 된다.
- LLM이 잘하는 것은 authoring, 변환 초안, 의미 추정, binding suggestion이다.
- Signal-CANdy가 계속 강하게 소유해야 할 부분은 deterministic validation, normalization, linking, resource analysis, reproducible image generation, bounded runtime execution이다.

즉 static parsing은 제거 대상이라고 단정하기보다 **여러 frontend 중 하나**로 내려간다. DBC parser는 여전히 CI, 재현성, 기존 사용자, reference test에 유용하다. 다만 제품의 정체성을 “DBC 문법을 읽는 도구”에만 묶지 않는다.

### 2.3 새 목표

Firmware를 다시 컴파일하지 않고 다음 내용을 바꿀 수 있기를 원한다.

- CAN bus / CAN ID
- Standard / Extended
- payload length
- bit layout
- endianness
- signedness
- factor / offset
- min/max, invalid raw values
- mux branch
- CRC / counter profile
- wire signal과 application pool signal의 mapping
- logical message와 wire message의 mapping
- message 추가/삭제 — 단, firmware와 runtime이 미리 허용한 pool/capability/resource 범위 안에서

Host PC가 입력 파일들을 Signal-CANdy compiler에 넣어 runtime image를 만들고, 외부 update layer가 이를 target flash에 적재한다. Device runtime은 boot 또는 activation 시 image를 검증·bind하고, 이후 CAN frame을 그 metadata로 decode/encode한다.

---

## 3. 명확히 분리할 책임 경계

### 3.1 Signal-CANdy compiler / format / runtime이 소유할 것

- source input parsing 또는 canonical input 수용
- staged IR 정의와 lowering
- Pool Contract / Manifest 형식
- Wire Model 형식
- Pool–Wire Binding 형식
- Target Capability 형식
- Project Manifest 형식
- semantic/type/unit/ownership/resource validation
- runtime image binary format
- deterministic image build
- image inspection / diff / symbol map
- runtime image validator
- message lookup 및 signal encode/decode
- conversion, mux, optional CRC/counter 실행
- runtime state/scratch 요구량 계산
- pool update quality/validity semantics

### 3.2 라이브러리 밖에 둘 것

다음은 Signal-CANdy runtime이 직접 소유하지 않는다.

- Host ↔ target transport protocol
- UART/CAN/Ethernet/BLE 등 전송 매체
- flash erase/write driver
- A/B slot 관리 자체
- bootloader rollback
- cryptographic signing / authentication / key management
- CAN peripheral driver
- message scheduling과 RTOS integration

단, 외부 계층이 안전하게 구현될 수 있도록 Signal-CANdy가 다음 인터페이스/descriptor를 정의할 수는 있다.

- image validate/bind API
- required memory query
- image hash / schema generation
- runtime capabilities descriptor
- activation 전후 state reset 요구
- image integrity CRC

전체 blob CRC는 accidental corruption 검사용이다. 악의적 변경 방지를 위한 signature/authentication은 update layer의 책임이다.

---

## 4. 핵심 mental model: Signal Process Image / Pool

“데이터 풀”은 단순한 임시 저장소보다 PLC/fieldbus의 **Process Image**에 가깝다.

```text
┌───────────────────────────────────┐
│ Application                       │
│                                   │
│ VehicleSpeed  → semantic signal   │
│ SteeringAngle → semantic signal   │
│ TorqueCommand → semantic signal   │
└─────────────────┬─────────────────┘
                  │ stable application contract
                  ▼
┌───────────────────────────────────┐
│ Signal Process Image / Pool       │
│ generated static ABI              │
└─────────────────┬─────────────────┘
                  │ runtime binding
                  ▼
┌───────────────────────────────────┐
│ Replaceable Wire Schema Image     │
│ CAN IDs / bits / units / mux ...  │
└─────────────────┬─────────────────┘
                  ▼
             CAN / CAN FD
```

Firmware application은 가능한 한 다음과 같은 wire detail을 몰라야 한다.

- `0x123`
- bit 8..23
- Motorola start-bit convention
- raw factor 0.01
- message DLC

Application이 알아야 하는 것은 다음에 가깝다.

- `VehicleSpeed`라는 stable semantic signal
- canonical storage type
- canonical unit
- validity/freshness/ownership
- `TorqueCommand`라는 logical message endpoint

예를 들어 Host가 wire schema를 다음처럼 바꾸어도 application code는 바뀌지 않아야 한다.

```text
Before:
CAN0 / 0x123 / bit 0..15 / unsigned / factor 0.01
    → VehicleSpeed

After:
CAN1 / 0x321 / bit 24..39 / Motorola / signed / factor 0.1 / offset -40
    → VehicleSpeed
```

변하는 것은 CAN Wire ABI이고, 고정되는 것은 application과 Pool Contract 사이의 ABI다.

---

## 5. 네 가지 ID를 혼동하지 않는다

동적 schema에서 다음 식별자는 역할이 완전히 다르다.

### 5.1 Semantic Signal ID

Application 의미에 장기간 결속되는 stable ID.

```text
VehicleSpeed = 0x01000001
```

원칙 후보:

- rename되어도 ID 유지
- 한 번 폐기한 ID는 재사용하지 않음
- 이름의 즉석 hash를 외부 ABI로 직접 사용하지 않음
- domain namespace를 상위 bit에 예약 가능
- experimental/vendor/private range를 별도로 둘 수 있음

### 5.2 Logical Message ID

Application이 TX encode 요청 또는 message-level 의미를 참조할 때 사용하는 ID.

```text
TorqueCommand = 0x80000001
```

Application이 wire CAN ID를 직접 사용하면 schema hot-swap의 의미가 약해진다. 따라서 다음과 같은 API를 지향할 수 있다.

```c
sc_encode(runtime, SC_MESSAGE_TORQUE_COMMAND, pool, &frame);
```

Wire CAN ID가 바뀌어도 logical message ID는 유지된다.

### 5.3 Dense Binding Index

특정 Pool ABI 안에서 runtime이 빠르게 사용하는 조밀한 index.

```text
VehicleSpeed semantic ID → binding index 17
```

이는 public semantic identity가 아니다. ABI generation 시 재배치될 수 있다.

### 5.4 Actual C Storage Offset

해당 firmware binary에서 pool field의 실제 byte offset.

```text
binding index 17 → offsetof(sc_pool_t, vehicle_speed)
```

Runtime image에 실제 C offset을 직접 박지 않는 방안을 우선 검토한다. C compiler가 결정하는 alignment/ABI를 F# compiler가 흉내 내지 않기 위해 generated C binding table에서 `offsetof`를 사용한다.

```c
const sc_pool_binding_t sc_pool_bindings[] =
{
    {
        .semantic_id = SC_SIGNAL_VEHICLE_SPEED,
        .offset = offsetof(sc_pool_t, vehicle_speed),
        .storage_type = SC_STORAGE_F32
    }
};
```

Runtime image는 `binding index 17`만 들고, firmware에 compile된 binding table이 실제 address를 해석한다.

### 5.5 Wire ID

CAN bus, Standard/Extended, CAN ID의 조합이다.

```text
(bus_id, is_extended, can_id)
```

동일한 `0x123`이라도 CAN0/CAN1 또는 Standard/Extended가 다르면 다른 message다.

---

## 6. Pool Definition / Application Contract

### 6.1 명칭

현재 대화에서는 `Pool Definition`, `Pool Contract`, `Application Contract`가 모두 등장했다.

- `Pool Definition`: signal storage 중심으로 직관적
- `Pool Contract`: firmware-runtime ABI라는 의미 강조
- `Application Contract`: logical message endpoint, ownership, quality policy까지 포함하기에 더 넓음

현재 확정하지 않는다. 다만 실제 모델은 단순한 값 배열보다 넓어질 가능성이 크다.

### 6.2 포함할 후보 정보

- stable semantic signal ID
- logical message ID
- C symbol
- canonical storage type
- canonical unit
- RX-produced / APP-produced / TX-consumed / Derived / Bidirectional 등 ownership
- semantic min/max
- default value
- freshness requirement
- validity policy
- optional/required binding
- optional diagnostic metadata

예시 authoring input:

```yaml
format: sc.pool/v1
namespace: vehicle

signals:
  - id: "0x01000001"
    symbol: vehicle_speed
    type: f32
    unit: km/h
    direction: rx
    default: "0"
    freshness_ms: 100
    required: true

  - id: "0x01000002"
    symbol: steering_angle
    type: f32
    unit: deg
    direction: rx
    freshness_ms: 50

  - id: "0x02000001"
    symbol: torque_command
    type: f32
    unit: Nm
    direction: tx
    minimum: "-300"
    maximum: "300"

messages:
  - id: "0x80000001"
    symbol: torque_command
    direction: tx
```

이 syntax는 예시일 뿐 확정 형식이 아니다.

### 6.3 생성할 정적 C

Pool Definition은 다음을 생성할 수 있다.

- `sc_pool.gen.h`
- `sc_pool.gen.c`
- semantic signal/message ID constants
- `sc_pool_t`
- typed accessor
- validity/quality helpers
- binding table
- Pool ABI hash/version constants

예시:

```c
#define SC_SIGNAL_VEHICLE_SPEED  UINT32_C(0x01000001)
#define SC_MESSAGE_TORQUE_COMMAND UINT32_C(0x80000001)

typedef struct
{
    float vehicle_speed;
    float steering_angle;
    float torque_command;

    uint32_t valid_bits[1];
    uint32_t updated_bits[1];
    uint32_t changed_bits[1];
} sc_pool_t;
```

### 6.4 수작성 C와 generated C의 조합

전체 application state를 Signal-CANdy가 소유할 필요는 없다.

```c
typedef struct
{
    app_control_state_t control;       /* handwritten */
    app_diagnostics_t diagnostics;     /* handwritten */
    sc_pool_t signals;                 /* generated */
} app_state_t;
```

또는 일부 pool 항목/상위 구조를 수작성 C로 두고, Signal-CANdy가 제한된 선언 DSL/X-macro 정의에서 binding table과 manifest를 생성할 수 있다.

단, 일반적인 임의 C source를 F#에서 완전히 파싱하고 target compiler의 ABI를 재현하는 방향은 피한다. C 비슷한 authoring format을 쓰더라도 제한된 선언 DSL이어야 한다.

예시 후보:

```c
SC_POOL_SIGNAL(
    0x01000001,
    VEHICLE_SPEED,
    F32,
    KM_PER_HOUR,
    RX,
    FRESHNESS_100MS
)
```

### 6.5 Pool representation 대안

#### A. Uniform 64-bit slot array

```c
typedef struct
{
    uint64_t value_bits[SC_POOL_CAPACITY];
    uint32_t valid_bitmap[(SC_POOL_CAPACITY + 31u) / 32u];
    uint32_t dirty_bitmap[(SC_POOL_CAPACITY + 31u) / 32u];
} sc_signal_pool_t;
```

장점:

- runtime 구현 단순
- 모든 binding이 dense slot index
- dynamic slot에 유리

단점:

- application type safety와 debugger 가독성 저하
- storage overhead 가능
- generated accessor 의존 증가

#### B. Typed generated struct

장점:

- application code와 debugger에서 자연스러움
- storage 최적화 가능
- compile-time type safety

단점:

- binding/offset layer 필요
- atomicity/alignment 처리 복잡
- schema와 C ABI 연결 규칙 필요

#### C. Hybrid

```text
Static Typed Region
+ Dynamic Tagged Region
```

- 핵심 제어 signal은 typed static field
- logger/debug/customer-specific signal은 reserved dynamic slot

이 대안은 장기적으로 유용할 수 있으나 MVP에 반드시 넣지는 않는다.

### 6.6 Pool Manifest

동일한 Pool Definition에서 Host가 읽는 `pool.manifest.json`도 생성한다.

```json
{
  "format": "sc.pool-manifest/v1",
  "poolAbi": "sha256:...",
  "signals": [
    {
      "id": "0x01000001",
      "symbol": "vehicle_speed",
      "type": "f32",
      "unit": "km/h",
      "direction": "rx",
      "required": true
    }
  ],
  "messages": [
    {
      "id": "0x80000001",
      "symbol": "torque_command",
      "direction": "tx"
    }
  ]
}
```

차량에 이미 올라간 firmware용 image를 Host에서 만들 때 원본 firmware source나 Pool Definition이 없어도, 해당 firmware가 배포한 Pool Manifest와 Target Capability만 있으면 link할 수 있는 구조를 지향한다.

---

## 7. Wire Definition

### 7.1 DBC는 frontend다

DBC는 계속 중요하지만 내부의 최종 계약은 아니다.

```text
DBC
ARXML
Excel
사양서
LLM-generated canonical JSON
future F# API / DSL
        ↓
Canonical Wire IR
```

LLM이 DBC나 자연어를 해석해 canonical input을 만들 수 있어도, deterministic validation은 반드시 거친다.

### 7.2 Wire Model에 포함할 후보

- source namespace
- physical bus identity
- message symbol
- bus + Standard/Extended + CAN ID
- payload byte length
- direction hint / sender / receiver
- cycle time 또는 timing metadata
- signal symbol
- start bit / length
- normalized byte order/start-bit convention
- signedness
- exact scale/offset representation
- wire physical unit
- raw/physical min/max
- invalid raw values
- enum/value table
- mux selector / branch predicate
- CRC/counter field 및 profile
- TX default payload template
- source location/fingerprint

### 7.3 Canonical Wire JSON 예시

```json
{
  "format": "sc.wire/v1",
  "buses": [
    {
      "id": "powertrain",
      "messages": [
        {
          "symbol": "VehicleStatus",
          "canId": "0x123",
          "extended": false,
          "length": 8,
          "directionHint": "rx",
          "signals": [
            {
              "symbol": "VehicleSpeed",
              "startBit": 0,
              "length": 16,
              "byteOrder": "little",
              "signed": false,
              "factor": "0.01",
              "offset": "0",
              "unit": "km/h"
            }
          ]
        }
      ]
    }
  ]
}
```

### 7.4 Wire Overlay

DBC에는 정보가 없거나 vendor attribute로만 존재할 수 있다.

- 실제 CAN channel assignment
- local ECU 기준 RX/TX
- CRC algorithm / byte range / Data ID
- alive counter modulus/increment
- invalid raw value
- reserved bit default
- timeout
- message initialization template

따라서 원본 Wire Source와 별도의 Overlay를 둘 수 있다.

```yaml
format: sc.wire-overlay/v1
source: powertrain.dbc
bus: can0
local_node: VCU

messages:
  VehicleStatus:
    cycle_ms: 10
    crc:
      signal: CRC
      algorithm: crc8-sae-j1850
      byte_range: [0, 6]
    counter:
      signal: AliveCounter
      modulus: 16
```

Overlay가 원본을 임의로 덮어쓰는 방식인지, 누락된 의미만 보충하는 방식인지, conflict를 어떻게 보고할지는 추후 정한다.

---

## 8. Pool–Wire Binding

Pool과 Wire를 같은 파일에 합치지 않는다. Binding은 두 세계 사이의 **의도적인 계약**이다.

```text
DBC = wire의 사실
Pool/Application Contract = application 의미의 사실
Binding = 두 사실을 연결하는 배포별 의도
```

### 8.1 Binding이 필요한 이유

동일한 Pool Contract에 대해 여러 고객/차종 DBC를 연결할 수 있다.

```text
Same Pool
├─ Vehicle A wire schema
├─ Vehicle B wire schema
└─ Bench/simulator wire schema
```

반대로 동일한 DBC를 서로 다른 firmware role에 연결할 수 있다.

```text
Same DBC
├─ Control ECU pool
├─ Logger pool
└─ Gateway pool
```

### 8.2 Binding에 포함할 후보

- pool semantic signal ID ↔ wire signal endpoint
- logical message ID ↔ wire message
- direction
- ownership
- unit conversion
- inactive mux policy
- invalid raw policy
- out-of-range encode policy
- rounding policy
- optional/required binding
- explicit ignore/unavailable
- source layout fingerprint

예시:

```yaml
format: sc.binding/v1

message_bindings:
  - logical_message: "0x80000001"
    wire_message: powertrain.TorqueCommand
    direction: tx

signal_bindings:
  - pool: "0x01000001"
    wire: powertrain.VehicleStatus.VehicleSpeed
    direction: rx
    inactive_policy: invalidate

  - pool: "0x01000002"
    wire: powertrain.VehicleStatus.SteeringAngle
    direction: rx
    inactive_policy: hold_last

  - pool: "0x02000001"
    wire: powertrain.TorqueCommand.RequestedTorque
    direction: tx
    invalid_policy: fail
    out_of_range: saturate
    rounding: nearest_even
```

### 8.3 Suggest → Review → Lock → Build

LLM이나 이름/단위 similarity로 binding 초안을 만드는 것은 적극 활용할 수 있다.

```text
suggest
  ↓
review
  ↓
lock
  ↓
build --locked
```

Release build에서 매번 fuzzy inference를 다시 실행하면 안 된다. 확정 binding에는 source fingerprint를 포함해 DBC drift를 감지한다.

```json
{
  "poolSignalId": "0x01000001",
  "wireSource": "powertrain",
  "messageId": "0x123",
  "extended": false,
  "signalName": "VehicleSpeed",
  "expectedLayoutHash": "sha256:..."
}
```

같은 이름이 유지되더라도 start bit, factor, length, unit 등이 바뀌면 `binding drift detected`로 실패하거나 명시적 review를 요구한다.

### 8.4 명시적 미연결

“mapping 없음”과 “의도적으로 사용하지 않음”을 구분한다.

```yaml
ignored_wire_signals:
  - wire: powertrain.VehicleStatus.FactoryDebug
    reason: not used by this firmware

unbound_pool_signals:
  - pool: "0x01000009"
    policy: optional
    default_quality: unavailable
```

Release mode 후보 규칙:

- required pool signal unbound → error
- 의도 없이 남은 wire signal → warning 또는 strict error
- RX pool signal에 다중 writer → 기본 error
- TX inverse conversion 불가능 → error

---

## 9. Target Capabilities와 Project Manifest

### 9.1 Target Capabilities

Schema의 의미가 맞아도 target runtime이 실행할 수 있는지는 별도 문제다.

```json
{
  "format": "sc.runtime-capabilities/v1",
  "runtimeImageMajor": 1,
  "runtimeImageMinor": 0,
  "features": [
    "can-fd",
    "motorola",
    "float32",
    "crc8",
    "single-level-mux"
  ],
  "limits": {
    "maxImageBytes": 131072,
    "maxMessages": 512,
    "maxSignals": 4096,
    "maxSignalsPerMessage": 128,
    "maxRuntimeStateBytes": 16384
  },
  "poolAbiHash": "sha256:..."
}
```

Target profile은 다음에서 얻을 수 있다.

- firmware build에서 checked-in/generated artifact
- 실제 connected device의 capability query

Host가 device에서 읽은 capability로 image를 link하면 mismatch를 upload 전에 차단할 수 있다.

### 9.2 Project Manifest

사용자가 개별 CLI option을 매번 조합하지 않도록 프로젝트 진입점 하나를 둔다.

```yaml
format: sc.project/v1
name: demo-car-vcu

pool:
  manifest: generated/pool.manifest.json

wire_sources:
  - name: powertrain
    type: dbc
    path: dbc/powertrain.dbc
    overlay: wire/powertrain.overlay.yaml

  - name: body
    type: canonical-json
    path: wire/body.scwire.json

bindings:
  - bindings/vcu.binding.yaml

target:
  capabilities: target/stm32g4.runtime.json

build:
  release_mode: true
  include_debug_symbols: false
  dispatch: binary_search

outputs:
  image: out/demo-car-vcu.scimg
  map: out/demo-car-vcu.map.json
  report: out/demo-car-vcu.report.json
  normalized_schema: out/demo-car-vcu.normalized.json
```

---

## 10. Staged IR Architecture

### 10.1 핵심 원칙

현재 `Ir.fs`에 Pool, Binding, Host JSON, target capability, runtime offsets를 모두 optional field로 추가하는 **mega-IR**을 피한다.

각 단계는 서로 다른 책임과 안정성을 가진다.

```text
Source ASTs
  ├─ DBC AST/import result
  ├─ canonical JSON DTO
  ├─ future F# shallow DSL
  └─ future quotation/deep DSL
          ↓
       Wire IR

Pool Definition AST
          ↓
   Pool Contract IR
          ↓
        Binding IR
          ↓
Linked Schema IR
  ├───────────────→ AOT C backend
  └───────────────→ Host reference evaluator
          ↓
Runtime Image IR
          ↓
Binary .scimg
```

### 10.2 Source AST / DTO

입력 형식의 문법과 source location을 보존한다.

- DBC의 vendor-specific quirk
- JSON line/column
- YAML key
- F# DSL source expression

이 단계는 rich semantic truth가 아니라 parsing/diagnostic용이다.

### 10.3 Wire IR

모든 frontend를 동일한 CAN 의미로 정규화한다.

- Motorola start bit convention 정규화
- exact numeric literal 보존
- mux 구조 정규화
- source identity/fingerprint 유지
- wire unit과 invalid domain 유지

현재 `Ir.fs`는 가장 가까운 출발점이지만, current public API compatibility와 새 모델의 명칭을 별도로 검토해야 한다.

### 10.4 Pool Contract IR

Application semantic ABI만 표현한다. CAN bit layout을 포함하지 않는다.

### 10.5 Binding IR

아직 unresolved symbolic reference와 사용자 정책을 보존한다. Suggestion provenance와 lock/fingerprint도 여기에 포함할 수 있다.

### 10.6 Linked Schema IR

모든 symbol과 ID를 resolve하고 다음을 끝낸 모델이다.

- pool type ↔ wire type compatibility
- unit composition
- raw↔canonical conversion plan
- ownership / writer validation
- logical message resolution
- mux branch plan
- CRC/counter profile resolution
- target capability validation
- resource count estimation

AOT C backend와 Runtime Image backend가 가능한 한 이 단계까지 공유한다.

### 10.7 Runtime Image IR

Target binary에 가까운 낮은 단계다.

- fixed-width field
- dense index
- string 제거 또는 symbol section 분리
- precomputed lookup/index
- normalized operations
- deduplicated conversion/profile table
- explicit section sizes
- resource requirements

Rich Semantic IR를 직접 binary serialization하지 않고, Host에서 target-friendly execution plan으로 lowering한다.

### 10.8 C IR 필요 여부

기존 static backend가 template 기반 C source를 계속 생성할 수 있다. 별도의 범용 C AST/C IR까지 도입할지는 미결정이다.

- 단순 template이면 현재 구조 유지 가능
- 여러 C backend/optimization/pass가 필요해지면 C-oriented lower IR 고려

Runtime Image IR와 C source emission IR를 억지로 동일하게 만들 필요는 없다.

---

## 11. F# DSL / FP-first 언어 구상과의 관계

이전 논의에는 Signal-CANdy를 더 넓은 **완전 정적 FP-first 언어 / compiler** 방향으로 확장하는 구상도 있었다. 핵심 제약은 다음과 같았다.

- runtime heap 금지
- 함수적 구성은 compile time에 완전히 제거
- bounded event execution
- effect/state를 명시적으로 모델링
- static type과 DBC/schema 결속
- 지원하지 못하는 경우 조용한 fallback 대신 정직한 compile failure
- resource를 사전 검증
- target executor는 얇고 deterministic
- “함수형처럼 보이지만 핵심 의미론은 빠진” 반쪽 구현을 피함

이 RFC가 곧바로 완전한 새 언어 구현을 승인하는 것은 아니다. 다만 staged IR는 향후 다음 두 종류 frontend를 허용할 수 있어야 한다.

### 11.1 Shallow F# DSL

F# combinator가 직접 Semantic IR를 구성한다.

```text
F# values/combinators
        ↓
Pool Contract IR / Wire IR / Binding IR
```

장점:

- 구현 단순
- F# type system 활용
- arbitrary F# execution은 Host compile time에 끝남

### 11.2 Deep DSL / Quotations

F# Quotations (`Expr`) 또는 별도 AST를 받아 normalization/HIR/MIR/LIR를 거쳐 C/runtime image로 낮춘다.

```text
F# quotation / language AST
        ↓
normalized HIR
        ↓
effect/state/resource analysis
        ↓
MIR / Linked Schema or Program IR
        ↓
C99 or thin target executor format
```

이 방향은 schema codec을 넘어 bounded control/dataflow language까지 확장될 수 있으므로 별도 RFC가 필요할 가능성이 높다.

### 11.3 이번 architecture와 공유하는 철학

Runtime Schema Image는 general-purpose VM이 아니지만 다음 원칙은 FP-first 언어 구상과 일치한다.

- heap-free
- bounded execution
- explicit state
- compile-time resource analysis
- deterministic failure
- thin target runtime
- rich Host IR → 낮은 target representation

따라서 현재 runtime schema backend는 장기 compiler architecture의 실용적인 하위 기반이 될 수 있다. 그러나 “schema descriptor interpreter”와 “application logic language executor”를 같은 v1 format에 섞지는 않는다.

---

## 12. Host Compiler Architecture

Host에서 image를 만드는 별도 로직을 다시 구현하지 않는다. Host는 Signal-CANdy compiler를 호출한다.

```text
Signal.CANdy.Compiler library
├─ CLI
├─ Host GUI
├─ CI/build pipeline
└─ tests/tools
```

### 12.1 Library-first

CLI text를 Host GUI가 parsing하게 하지 않는다. Compiler library가 structured result를 반환한다.

```fsharp
type ImageBuildInput =
    { PoolManifest: PoolManifest
      WireSources: WireSource list
      Bindings: BindingSpec list
      Target: RuntimeCapabilities
      Options: ImageBuildOptions }

type ImageBuildArtifacts =
    { Image: byte array
      ImageHash: string
      Map: SchemaMap
      Report: BuildReport
      NormalizedSchema: LinkedSchema }

val buildImage:
    ImageBuildInput ->
    Result<ImageBuildArtifacts, Diagnostic list>
```

실제 type/API 명칭은 추후 정한다.

### 12.2 CLI 후보

```text
signal-candy pool build
signal-candy wire import
signal-candy bind suggest
signal-candy bind validate
signal-candy image build
signal-candy image inspect
signal-candy image diff
```

예시:

```bash
signal-candy image build demo-car-vcu.scproject.yaml
```

### 12.3 Host GUI flow

```text
DBC/Wire source load
  ↓
Pool Manifest load or device query
  ↓
Binding suggestion/edit/review
  ↓
Target capability query
  ↓
Compiler library build
  ↓
structured diagnostics / preview / diff
  ↓
.scimg byte[]
  ↓
external transport/update layer
```

### 12.4 Host artifact 분리

#### `pool.manifest.json`

Firmware application ABI.

#### `normalized-schema.json`

모든 source/overlay/binding을 합친 canonical linked meaning. 재현과 디버깅에 사용.

#### `map.json`

Runtime index/operation과 human-readable symbol/source mapping.

#### `report.json`

- image size
- message/signal count
- operation count
- required runtime state/scratch
- unsupported feature
- unused/ignored/unbound 항목
- warning/error
- input hash
- target capability

Target binary에는 긴 name/unit/source location을 넣지 않고 Host map에만 둘 수 있다.

---

## 13. Runtime Image는 “압축 DBC”가 아니라 lowered execution image

Target에서 DBC 의미를 다시 해석하지 않는다.

```text
DBC / JSON / DSL
        ↓
Canonical Semantic Models
        ↓ validate / normalize / link
        ↓ target-aware lowering
Runtime Schema Image
```

Host에서 미리 끝낼 것:

- Motorola 표기 convention 정규화
- bit coverage 검증
- sign extension plan
- mux branch resolution
- conversion plan 선택
- range/invalid policy resolve
- CRC/counter profile 연결
- pool binding resolve
- 중복 conversion/profile dedup
- lookup strategy 선택

Target은 이미 검증된 descriptor program만 실행한다.

---

## 14. Runtime Image section 후보

정확한 byte layout은 별도 binary-format RFC에서 정한다. 논리적 section 후보는 다음과 같다.

```text
┌────────────────────────────────────────┐
│ Header                                 │
│ - magic                                │
│ - format major/minor                   │
│ - total size                           │
│ - schema generation/revision           │
│ - required runtime features            │
│ - pool ABI hash                        │
│ - runtime state/scratch requirements   │
│ - whole-image integrity CRC/hash       │
├────────────────────────────────────────┤
│ Section Directory                      │
├────────────────────────────────────────┤
│ RX Message Index                       │
│ - bus/std-ext/CAN ID                   │
│ - length constraints                   │
│ - message program index/offset         │
├────────────────────────────────────────┤
│ TX Logical Message Index               │
│ - logical message ID                   │
│ - message program index/offset         │
├────────────────────────────────────────┤
│ Message Programs / Descriptor Arrays   │
│ - extract/insert operations            │
│ - mux predicates                       │
│ - conversion references                │
│ - default payload template             │
├────────────────────────────────────────┤
│ Conversion Table                       │
│ - identity                             │
│ - integer affine                       │
│ - fixed point                          │
│ - float32/float64 fallback             │
├────────────────────────────────────────┤
│ CRC / Counter Profiles                 │
├────────────────────────────────────────┤
│ Pool Binding References                │
├────────────────────────────────────────┤
│ Optional Debug/Symbol Section          │
└────────────────────────────────────────┘
```

### 14.1 Pointer-free / relocation-free

모든 reference는 다음 중 하나다.

- image start 기준 relative offset
- section-local index
- dense table index

Target pointer, Host pointer, packed C struct pointer를 저장하지 않는다.

장점:

- internal flash / external QSPI memory-map 모두 가능
- relocation 불필요
- Host/target address width 차이와 무관
- image hash/reproducibility 유리

### 14.2 Binary endianness와 accessor

Binary format 자체의 endianness를 하나로 고정한다. 후보는 little-endian이다.

C에서 blob address를 `packed struct *`로 cast하는 구현은 피한다.

- alignment fault
- compiler packing rule
- strict aliasing
- ABI 차이

대신 safe accessor가 integer field를 읽는다. 성능이 필요하면 bind 시 validated native cache를 caller-provided state에 구축할 수 있다.

### 14.3 일반 압축보다 구조적 압축 우선

전체 blob을 LZ4 등으로 압축하면 random access가 어려워지고 RAM에서 풀어야 할 수 있다.

먼저 고려할 것:

- string/debug section 분리
- repeated factor/offset profile 공유
- CRC profile 공유
- 16-bit dense index
- flag bit packing
- message별 contiguous operation
- optional direct map

예를 들어 signal descriptor가 16 bytes면 1000 signals가 약 16 KiB다. 실제 target constraint를 측정하기 전 과도한 variable-length compression을 넣지 않는다.

Transport 시 compression은 외부 update layer에서 할 수 있고, flash에는 random-access image를 저장할 수 있다.

### 14.4 Deterministic build

같은 canonical input과 target profile은 같은 binary를 생성해야 한다.

- stable sort
- deterministic table interning
- padding byte zero
- build timestamp를 hashed payload에 넣지 않음
- JSON key order에 의존하지 않음
- decimal lowering rule 고정
- source path normalization 정책

Schema hash는 raw JSON text보다 canonical model 또는 final image payload를 기준으로 계산한다.

---

## 15. Descriptor Interpreter, not a general VM

Runtime image는 실행 계획이지만 Turing-complete VM이 아니다.

허용 operation 후보:

```text
EXTRACT
INSERT
SIGN_EXTEND
CONVERT
CHECK_RANGE
CHECK_INVALID_RAW
CHECK_MUX
VERIFY_CRC
CHECK_COUNTER
UPDATE_COUNTER
SET_QUALITY
END
```

금지/제한 후보:

- backward jump
- arbitrary loop
- indirect call
- arbitrary memory address
- unbounded recursion
- arbitrary expression evaluator
- user-defined target code

v1은 opcode bytecode보다 검증된 fixed descriptor array로 시작할 수 있다.

```c
typedef struct
{
    uint16_t binding_index;
    uint16_t start_bit;
    uint8_t bit_length;
    uint8_t flags;
    uint16_t conversion_index;
    uint16_t predicate_index;
} sc_signal_op_t;
```

Architecture 관점에서는 작은 VM과 유사하지만 구현/문서는 **bounded descriptor interpreter**로 부르는 편이 안전하다.

---

## 16. Allocation-free C99 Runtime API 후보

```c
typedef struct
{
    uint32_t id;
    uint8_t flags;
    uint8_t len;
    uint8_t data[64];
} sc_frame_t;

typedef struct sc_schema sc_schema_t;
typedef struct sc_runtime_state sc_runtime_state_t;
typedef struct sc_signal_pool sc_signal_pool_t;

sc_status_t sc_schema_open(
    sc_schema_t *schema,
    const void *image,
    size_t image_size,
    const sc_runtime_limits_t *limits);

size_t sc_schema_required_state_bytes(
    const sc_schema_t *schema);

size_t sc_schema_required_scratch_bytes(
    const sc_schema_t *schema);

sc_status_t sc_decode(
    const sc_schema_t *schema,
    sc_runtime_state_t *state,
    uint8_t bus_id,
    const sc_frame_t *frame,
    sc_time_t now,
    sc_signal_pool_t *pool,
    void *scratch,
    size_t scratch_size);

sc_status_t sc_encode(
    const sc_schema_t *schema,
    sc_runtime_state_t *state,
    uint32_t logical_message_id,
    const sc_signal_pool_t *pool,
    sc_frame_t *frame,
    void *scratch,
    size_t scratch_size);
```

실제 API shape는 미결정이다. 고정할 원칙 후보:

- runtime heap allocation 없음
- image immutable
- pool/state/scratch는 caller-owned
- state/scratch size query 가능
- `open/bind` 시 전체 validation
- encode/decode hot path는 최소 검사
- transport-independent frame representation
- CAN FD payload length는 normalized byte length 0..64

---

## 17. Decode pipeline과 atomicity

### 17.1 Lookup key

```text
(bus_id, is_extended, can_id)
```

DLC raw code보다 normalized payload length를 runtime에 넘기는 것이 transport abstraction에 유리하다. BRS/ESI는 codec에 필요하지 않으면 optional metadata다.

### 17.2 권장 decode 순서

```text
message lookup
  ↓
payload length validation
  ↓
mux selector extraction
  ↓
CRC validation
  ↓
counter validation
  ↓
scratch decode / conversion
  ↓
all checks passed
  ↓
atomic pool commit
  ↓
valid/updated/changed/quality update
```

CRC 실패 전에 일부 pool field가 갱신되면 안 된다.

가능한 구현:

- CRC/counter를 먼저 검사하고 signal decode
- message-local scratch에 decode 후 commit
- transactional pool API

MVP에서는 message별 worst-case scratch size를 compile time에 계산하고 caller-provided scratch를 사용한다.

### 17.3 `valid`, `updated`, `changed` 분리

값이 바뀌지 않았어도 새 frame이 정상 수신될 수 있다.

- `valid`: 현재 값을 사용할 수 있음
- `updated`: 이번 cycle/epoch에 새 frame으로 갱신됨
- `changed`: 이전 값과 실제 value가 다름

Quality 후보:

```c
typedef enum
{
    SC_QUALITY_UNINITIALIZED,
    SC_QUALITY_VALID,
    SC_QUALITY_STALE,
    SC_QUALITY_INVALID,
    SC_QUALITY_UNAVAILABLE
} sc_quality_t;
```

세부 원인은 별도 diagnostic counter/last-error로 둘 수 있다.

- CRC failure
- counter failure
- range failure
- invalid raw
- DLC mismatch
- mux inactive

### 17.4 Mux inactive policy

- hold last
- invalidate
- set default

Binding 또는 Pool Contract에서 명시한다.

### 17.5 Torn read / multi-signal snapshot

32-bit MCU에서 `double`/`uint64_t` write를 application이 동시에 읽으면 torn read가 가능하다. 또한 한 message의 여러 signal을 application이 중간 상태로 볼 수 있다.

대안:

- critical section hook
- double buffer
- generated accessor + sequence lock
- message/group snapshot API

Seqlock 예시:

```text
commit start: sequence odd
write all values/quality
commit end: sequence even
```

Application snapshot은 sequence가 동일한 even인지 확인하고 아니면 재시도한다.

---

## 18. Freshness와 time ownership

Runtime이 hardware timer를 소유하지 않아도 freshness를 정의할 수 있다.

```c
sc_decode(..., now_ticks, ...);
sc_runtime_expire(runtime, now_ticks, pool);
```

Timestamp granularity 대안:

- message별 last-received timestamp
- freshness-enabled signal만 timestamp
- 모든 slot별 timestamp
- generation/epoch counter만 저장

MVP에서는 message별 timestamp가 단순하고 메모리 효율적이다. 다만 한 pool signal에 여러 source writer를 허용하면 복잡해진다.

---

## 19. Ownership과 multiple writers

Pool Contract에 방향/소유권을 명시한다.

```text
VehicleSpeed: RX-produced, APP-consumed
TorqueCommand: APP-produced, TX-consumed
DerivedSpeed: APP-produced/Derived
```

기본 validator 후보:

- RX Pool Signal에 최대 한 개 writer
- TX Pool Signal은 여러 message consumer 허용
- RX와 APP가 동시에 쓰는 Bidirectional은 명시적으로 opt-in

여러 source arbitration은 MVP에서 제외할 수 있다.

- priority source
- last writer wins
- fallback source

이런 정책을 runtime에 넣기보다 `VehicleSpeed_CAN0`, `VehicleSpeed_CAN1`을 별도 pool signal로 받고 application이 선택하는 편이 더 명시적일 수 있다.

---

## 20. Numeric semantics와 unit conversion

### 20.1 두 단계 의미

```text
Raw CAN bits
  ↓ DBC/wire physical conversion
Wire Physical Value
  ↓ Binding unit/semantic conversion
Pool Canonical Value
```

예:

```text
Wire: raw × 0.001 = m/s
Pool: km/h
Binding: m/s × 3.6

Lowered runtime conversion:
raw × 0.0036 → km/h
```

Pool unit은 Wire Schema가 아니라 Pool Contract가 소유한다. Schema를 바꿨는데 application field의 unit가 조용히 변하는 일을 막는다.

### 20.2 Storage type와 wire type 분리

12-bit signed raw를 pool에서 다음 중 하나로 저장할 수 있다.

- `int16_t`
- `float32`
- `float64`
- Q-format fixed point

Compiler가 conversion plan을 생성한다.

### 20.3 Exact numeric representation

Host JSON의 일반 number는 JavaScript 64-bit integer와 exact decimal 표현에 위험하다.

문자열 표현 후보:

```json
{
  "signalId": "0x01000001",
  "rawMask": "0xFFFFFFFFFFFFFFFF",
  "factor": "0.01",
  "offset": "-40",
  "crcPolynomial": "0x1D"
}
```

Compiler 내부에서는 가능한 오래 다음을 유지한다.

- decimal literal
- rational numerator/denominator
- exact integer

최종 lowering에서 다음 plan을 선택한다.

- identity
- integer affine
- fixed-point
- float32
- float64

### 20.4 Encode inverse semantics

TX는 inverse conversion이 가능해야 한다.

정해야 할 정책:

- rounding: toward-zero / floor / ceil / nearest-even / nearest-away
- out-of-range: error / saturate / wrap
- invalid pool value: fail / default / invalid raw code / hold template
- NaN/Infinity handling

Host preview, AOT C, Runtime C가 동일한 경계 결과를 내야 한다.

---

## 21. TX payload와 stateful counter

### 21.1 Payload initialization

RX는 unused bit를 무시할 수 있지만 TX는 payload 전체를 결정해야 한다.

후보:

- zero fill
- constant payload template
- preserve caller buffer
- require full bit coverage

Runtime image message section에 default payload template을 넣는 방안이 유용하다.

```text
FF 00 80 00 00 00 00 00
  ↓ insert signals
  ↓ insert counter
  ↓ compute/insert CRC
```

### 21.2 Counter commit timing

`encode()` 호출 때 counter를 즉시 증가시키면 driver queue 실패 시 bus counter가 건너뛸 수 있다.

후보 API:

```c
sc_encode_prepare(..., &frame, &tx_token);
can_result = can_send(&frame);
sc_encode_commit(runtime, &tx_token, can_result == CAN_OK);
```

또는 증가 기준을 명시한다.

- encode invocation
- driver queue accepted
- actual transmit complete

Preview/test API가 state를 진행시키지 않도록 pure preview와 stateful encode를 분리할 수도 있다.

---

## 22. Schema activation / hot swap

Flash image 일부를 in-place patch하기보다 immutable image 전체 교체를 우선한다.

```text
Active Image A
Inactive Image B
```

외부 update flow 예시:

1. Host가 새 image 생성
2. update layer가 B slot에 write
3. Signal-CANdy validator로 B 검사
4. integrity, format, feature, bounds, pool ABI 검사
5. 새 runtime state 준비
6. critical section에서 active schema pointer 교체
7. old image/state reclamation 가능 시점 처리

### 22.1 Runtime state의 schema 종속성

- TX alive counter
- last RX counter
- last timestamp
- diagnostics
- resolved lookup cache
- mux-related state

State에 `bound_schema_hash`를 둘 수 있다.

MVP activation policy 후보:

- RX-produced pool signal 전부 invalid
- APP-produced/TX source value 유지
- updated/changed bit clear
- CRC/counter state reset
- timestamp reset

향후 schema diff로 compatible message state만 migration할 수 있지만 v1에 필수는 아니다.

### 22.2 Pool ABI mismatch

Image header의 `pool_abi_hash`와 firmware constant가 다르면 activation 거부.

더 유연한 runtime semantic lookup 방식도 가능하지만, 핵심 제어 signal에는 strict ABI가 안전하다.

---

## 23. Capability negotiation과 version compatibility

Runtime descriptor 후보:

```c
typedef struct
{
    uint16_t runtime_format_major;
    uint16_t runtime_format_minor;
    uint64_t feature_flags;
    uint32_t max_image_size;
    uint32_t max_messages;
    uint32_t max_signals;
    uint32_t max_runtime_state_bytes;
    uint8_t pool_abi_hash[32];
} sc_runtime_capabilities_t;
```

Host는 target capability를 읽고 compile/link한다.

Image header 후보:

- format major/minor
- minimum runtime version
- required feature bitmap
- pool ABI hash
- schema hash
- schema generation/revision
- state/scratch requirement

Compatibility 정책은 별도 결정이 필요하다.

- unknown optional section skip 가능 여부
- major mismatch reject
- minor backward compatibility
- feature flag negotiation
- schema generation rollback policy

---

## 24. Validation과 trust boundary

### 24.1 Compiler/linker validation

최소 검사 후보:

- signal bit range가 payload를 초과하는지
- overlap이 허용된 mux branch인지 불법 overlap인지
- duplicate message lookup key
- factor가 zero인지
- inverse conversion 가능 여부
- exact raw range가 bit width/signedness에 맞는지
- Pool type/unit와 compatible한지
- required binding 누락
- multiple writer
- logical message duplicate
- mux selector/branch valid
- CRC byte range/field overlap
- counter modulus 표현 가능
- target feature 지원
- max messages/signals/ops/state/scratch/image size
- source fingerprint drift
- deterministic ordering ambiguity

### 24.2 Runtime image validator

Host compiler가 만든 image라도 target은 신뢰하지 않고 activation 전 검증한다.

- total size / section bounds
- integer overflow-safe offset+length
- section overlap/duplicate
- count × element-size overflow
- index bounds
- program termination
- unsupported opcode/feature
- maximum operations
- binding index bounds
- conversion/profile index bounds
- payload bit range
- lookup table order/invariant
- CRC/integrity
- Pool ABI

### 24.3 LLM trust boundary

LLM이 해도 되는 것:

- DBC/문서/Excel 의미 해석
- canonical JSON draft
- binding suggestion
- naming/unit inference
- diagnostic 설명

LLM이 bypass하면 안 되는 것:

- deterministic parser/schema validation
- semantic linking
- resource analysis
- binary layout generation
- release binding lock
- target activation validation

Trusted boundary는 authoring output이 아니라 Signal-CANdy compiler/validator에서 시작한다.

---

## 25. 기존 AOT C backend와의 관계

AOT backend를 제거하지 않는다.

장점:

- fixed deployment에서 최고 성능/최소 runtime overhead
- generated C를 code review 가능
- 현재 사용자와 API 보존
- Runtime backend reference oracle
- differential test 대상

공유 후보:

```text
Wire/Pool/Binding frontends
        ↓
Linked Schema IR
        ├─ current/static message-oriented C backend
        ├─ future pool-bound AOT C backend
        └─ runtime image backend
```

현재 message-oriented generated API와 새 pool-bound API가 완전히 같아야 한다고 미리 가정하지 않는다. Migration layer나 별도 backend naming이 필요할 수 있다.

---

## 26. Testing Strategy

### 26.1 Differential test

동일한 Linked Schema에 대해 비교한다.

```text
AOT-generated C
Runtime descriptor interpreter
Host reference/oracle
```

검사:

- decode value/quality
- encode payload
- roundtrip
- endian/signed edge
- range/rounding
- mux active/inactive
- CRC/counter
- CAN FD

### 26.2 Property-based test

- random schema within constraints
- random payload
- boundary/adversarial value
- encode→decode properties
- AOT/runtime equivalence

F# FsCheck 또는 Host Python oracle와 역할을 나눌 수 있다.

### 26.3 Malformed image fuzzing

- truncated header/section
- invalid offset/count
- integer overflow
- unknown opcode
- no END
- out-of-range binding
- circular/invalid reference
- duplicate section
- noncanonical lookup order

목표는 malformed image가 error로 거부되고 out-of-bounds, hang, memory corruption이 없음을 보이는 것이다.

### 26.4 Cross-platform/ABI test

- 32/64-bit Host
- ARM GCC/Clang
- alignment-strict target
- C99/C++ include compatibility
- big/little Host 여부와 무관한 format reader

### 26.5 Resource regression

각 fixture에 대해 다음을 snapshot/report할 수 있다.

- image bytes
- state bytes
- scratch bytes
- operations per message
- worst-case lookup/execute bound

---

## 27. MVP 후보

아직 확정된 scope는 아니지만, 가장 현실적인 첫 vertical slice는 다음과 같다.

### 27.1 Host/compiler

- Pool Definition 또는 최소 Pool Manifest model
- DBC/current IR → Wire IR adapter
- explicit Binding
- Target Capability
- Linked Schema validation
- Runtime Image v1 writer/reader/inspect
- map/report JSON

### 27.2 Runtime C99

- immutable image
- caller-owned state/scratch/pool
- Standard/Extended CAN
- CAN FD 0..64 bytes
- Little/Big Endian
- signed/unsigned
- identity/affine conversion
- float32 또는 선택된 최소 storage subset
- RX decode
- TX encode by logical message ID
- single-level mux
- valid/updated/changed
- deterministic validation

### 27.3 MVP 이후 후보

- CRC/counter full profiles
- nested/extended mux
- fixed-point optimized plan
- freshness expiry
- dynamic diagnostic pool
- direct-map/cache optimization
- schema state migration
- parameter overlay
- signature integration hook

CRC/counter를 v1에 포함할지는 현재 구현 자산과 실제 use case를 보고 결정한다.

---

## 28. Later Extensions / 별도 RFC 후보

### 28.1 Dynamic tagged pool region

Runtime에 새 wire signal을 추가해 Host logger가 볼 수 있지만 application logic은 모르는 영역.

### 28.2 Base image + parameter overlay

전체 image 교체 대신 CAN ID/factor/timeout 등 작은 parameter만 자주 변경. v1부터 넣으면 format/validation 복잡도가 증가하므로 후순위.

### 28.3 Full FP-first bounded language

Schema codec을 넘어 event/dataflow/control logic까지 compile. 별도 language constitution과 effect/resource/type semantics 필요.

### 28.4 Other transports

Pool/Binding/Linked IR가 CAN-specific lower layer와 잘 분리되면 LIN, SOME/IP, serial field protocol 등으로 확장할 가능성. 현재 범위는 CAN/CAN FD에 집중한다.

### 28.5 On-device schema introspection

Debug symbol section 또는 Host map 없이 target CLI가 일부 schema를 조회. Flash/RAM trade-off가 있다.

---

## 29. 명시적 Non-goals

초기 architecture에서 하지 않는 것:

- application control logic을 runtime image로 임의 교체
- general-purpose scripting VM
- binding file의 arbitrary expression/code execution
- MCU에서 DBC text parsing
- Signal-CANdy runtime 내부 flash updater
- runtime heap과 unbounded schema
- unreviewed LLM binding 자동 승인
- 임의 C source 전체 parsing 및 target ABI 재현
- 모든 vendor CRC/mux dialect를 v1에서 완벽 지원
- 기존 AOT backend 즉시 폐기

---

## 30. 지금까지 비교적 확실해진 결정

아래는 현재 대화에서 방향성이 강하게 합의된 항목이다. 최종 RFC에서 다시 확인하되, 이유 없이 잃지 않는다.

1. DBC는 여러 source frontend 중 하나다.
2. MCU에서 DBC 자체를 parsing하지 않는다.
3. 정적 C backend는 유지한다.
4. 새 runtime-image backend는 기존 compiler architecture의 두 번째 backend로 본다.
5. Pool/Application Contract와 Wire Schema를 분리한다.
6. Pool–Wire Binding은 별도의 명시적 입력이다.
7. Pool Definition에서 firmware용 C와 Host용 Manifest를 함께 생성하는 방향이 자연스럽다.
8. Host는 별도 image generator를 재구현하지 않고 Signal-CANdy compiler/library를 사용한다.
9. Host transport와 flash update는 runtime library 밖이다.
10. Runtime은 allocation-free C99를 목표로 한다.
11. Runtime image는 general-purpose VM이 아니라 bounded descriptor/execution image다.
12. 실제 C offset을 portable binary ABI로 직접 굳히는 것을 피한다.
13. semantic signal ID, binding index, C offset, CAN ID를 분리한다.
14. one giant IR보다 staged IR를 지향한다.
15. LLM authoring과 deterministic validation/linking의 trust boundary를 분리한다.
16. 동일 input은 deterministic image를 만들어야 한다.
17. 기존 AOT backend/runtime backend/oracle의 differential testing을 활용한다.

---

## 31. 아직 결정하지 않은 질문

### Terminology / Product boundary

- `Pool Definition`, `Pool Contract`, `Application Contract` 중 최종 명칭은?
- Signal-CANdy의 product description을 언제/어떻게 변경할 것인가?
- Runtime component/package/repository를 본체에 둘 것인가 분리할 것인가?

### Pool ABI

- typed struct, uniform slot, hybrid 중 무엇을 v1로 할 것인가?
- actual offset binding table의 정확한 ABI는?
- pool quality metadata를 value struct에 붙일지 side table로 둘지?
- static/dynamic region을 v1에 포함할지?
- semantic ID namespace/version 정책은?

### IR

- 현재 `Ir.fs`를 Wire IR로 rename/migrate할지, compatibility wrapper를 둘지?
- source AST와 canonical Wire IR의 exact boundary는?
- Linked Schema IR를 AOT/runtime backend가 얼마나 공유할지?
- Runtime Image IR를 public library API로 노출할지 internal로 둘지?
- C-oriented IR가 필요한지?

### Authoring

- canonical authoring format은 JSON/YAML/F# DSL 중 무엇인가?
- Pool Definition의 source-of-truth는 data file인가 제한된 C/X-macro인가?
- F# shallow DSL을 official surface로 제공할지?
- deep DSL/FP-first language는 별도 프로젝트/RFC인가?

### Numeric semantics

- exact decimal/rational type은 무엇인가?
- unit system을 type-safe하게 구현할지 string+validator로 시작할지?
- fixed-point plan의 representation은?
- rounding/saturation 기본값은?

### Runtime image

- magic/endianness/alignment/section header exact format은?
- offset width 16/32/64 중 무엇인가?
- unknown optional section 처리 정책은?
- CRC/hash algorithm은?
- debug symbol section을 image에 허용할지 Host map only로 둘지?
- CRC/counter/mux scope를 v1 어디까지 넣을지?

### Runtime behavior

- decode atomicity 구현은 scratch/critical section/double-buffer 중 무엇인가?
- freshness timestamp granularity는?
- schema swap 시 어떤 state를 보존할지?
- TX counter commit point는?
- multiple writer/arbitration을 runtime이 지원할지?
- invalid/NaN/default policy의 소유자는 Pool/Binding/Wire 중 어디인가?

### Host/deployment

- device에서 full Pool Manifest를 읽을지 hash/capabilities만 읽을지?
- project manifest format은?
- binding lock/fingerprint exact algorithm은?
- map/report/normalized JSON의 compatibility guarantee는?

---

## 32. 제안 repository shape

확정이 아니라 구현 분할용 초안이다.

```text
src/
  Signal.CANdy.Core/
    semantic/wire models
    validation

  Signal.CANdy.Compiler/
    pool compiler
    wire import adapters
    binding linker
    target capability validation

  Signal.CANdy.RuntimeImage/
    image IR
    binary writer/reader
    inspector/diff

  Signal.CANdy.CLI/
    thin command wrapper

runtime/
  c99/
    include/
      signal_candy_runtime.h
    src/
      signal_candy_runtime.c
      signal_candy_bits.c
      signal_candy_crc.c

schemas/
  pool-manifest/
  wire/
  binding/
  target-capabilities/
  project/

Plans/
  Runtime_Schema_Architecture.md
```

NuGet/package boundary는 public API stability를 고려해 별도 결정한다.

---

## 33. 후속 issue 분할 후보

RFC가 architecture를 받아들인 뒤 생성한다. 지금 즉시 모두 열어 stale backlog를 만들 필요는 없다.

1. Application Contract / Pool Definition semantics
2. Generated C Pool ABI and binding table
3. Pool Manifest schema
4. Wire IR normalization and current `Ir.fs` migration
5. Pool–Wire Binding schema and linker diagnostics
6. Target Capability / Project Manifest formats
7. Linked Schema IR and conversion/unit rules
8. Runtime Image binary format v1
9. Allocation-free C99 runtime validator/binder
10. Runtime RX decode vertical slice
11. Runtime TX encode and stateful counter semantics
12. Host compiler API / CLI / inspect / diff
13. Differential tests and resource regression
14. Malformed image fuzzing
15. Optional F# DSL / FP-first language follow-up RFC

---

## 34. 권장 작업 순서

```text
RFC terminology and invariants
  ↓
Pool Contract + Manifest minimal model
  ↓
Current IR → Wire IR adapter
  ↓
Explicit Binding + Linked Schema
  ↓
Tiny Runtime Image v1
  ↓
Validator + one-message RX decode vertical slice
  ↓
AOT/runtime differential test
  ↓
TX/logical message
  ↓
Mux/quality/freshness
  ↓
CRC/counter and optimization
```

처음부터 완전한 binary VM, 모든 input format, full dynamic pool을 동시에 구현하지 않는다. 그러나 축소 구현이 장기 의미론을 거짓으로 표현해서도 안 된다. 지원하지 않는 기능은 명시적 `UnsupportedFeature` 또는 compile failure로 처리한다.

---

## 35. 이 RFC 문서의 완료 조건

이 문서가 바로 구현 사양이 되는 것이 아니라, 후속 정제 PR/RFC가 다음을 확정하거나 명시적으로 defer해야 한다.

- terminology
- staged IR boundaries
- current IR migration strategy
- minimum Pool/Wire/Binding/Target input contracts
- runtime/external boundary
- runtime image v1 invariants
- pool ABI strategy
- deterministic numeric semantics
- activation/state policy
- existing AOT backend compatibility
- MVP vertical slice
- child issue sequence

Tracking issue #17은 설계 문서 merge만으로 자동 close하지 않는다. Accepted architecture와 child issue 링크가 기록된 뒤 close한다.

---

## 36. 보존해야 할 중심 통찰

이 방향의 가치는 “DBC를 더 잘 파싱하는 것”에만 있지 않다.

LLM이 source interpretation을 대부분 대신할 수 있는 시대에도, embedded deployment에는 다음이 필요하다.

- stable application semantics
- explicit binding
- deterministic compilation
- resource-bounded target execution
- host/target compatibility contract
- malformed input rejection
- reproducible binary artifact
- static/AOT와 dynamic/runtime 사이의 공통 semantic truth

Signal-CANdy는 이 신뢰 경계를 담당하는 도구로 재정의될 수 있다.

> Authoring은 유연하게, linking은 엄격하게, target runtime은 얇고 bounded하게.
