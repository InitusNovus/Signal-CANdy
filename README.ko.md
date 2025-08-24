# Signal CANdy — DBC to C 코드 생성기 (F#)

언어: 이 문서는 한국어입니다. 영어판은 README.md를 참고하세요.

이 프로젝트는 F# 기반 코드 생성기를 사용해 `.dbc` 파일로부터 이식성 높은 C99 파서 모듈(헤더/소스)을 생성합니다. 프로젝트 이름: Signal CANdy.

## ⚡ 빠른 시작 (5분)

1) 필수 도구 확인

```bash
dotnet --version   # 8.0+
make --version     # GNU Make
gcc --version      # C 컴파일러
```

2) 샘플 DBC로 코드 생성

```bash
dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen
```

3) 빌드 및 기본 테스트 실행

```bash
make -C gen build
./gen/build/test_runner test_roundtrip
```

예상: 테스트 통과 및 `gen/include`에 헤더가 생성됩니다.

## 📋 목차

- 시작하기 (요구 사항, 사용법)
- 지원 기능
- 구성 파일 개요 (config.yaml)
- DBC → 코드 생성 흐름
- 대규모 테스트와 스트레스 수트
- 출력 레이아웃 및 이름 규칙
- 메시지 API 네이밍 규칙
- 펌웨어에 생성물 포함하기
- 빌드 시스템 예시 (Make/CMake)
- 런타임 사용 예시
- 멀티플렉스 메시지
- 값 테이블 (VAL_)
- PhysType 세부 및 수치 정밀도
- 디스패치 모드, 레지스트리, nanopb와의 관계
- 엔디안 및 비트 유틸리티
- 프로젝트 구조
- 성능 벤치마크
- 문제 해결 (Troubleshooting)
- 제한사항
- 라이선스, 서드파티, AI 출처

## 시작하기

### 요구 사항

- .NET SDK 8.0 이상
- make (생성된 C 코드 빌드용)
- gcc (생성된 C 코드 컴파일용)

### 사용법

1) DBC 파일로부터 C 코드 생성

    ```bash
    dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen
    ```

    위 명령은 `examples/sample.dbc`를 파싱하여 `gen/` 디렉터리에 C 헤더/소스 파일을 생성합니다.

2) 생성된 C 코드 빌드

    ```bash
    make -C gen build
    ```

3) 생성된 C 코드 테스트 실행

    ```bash
    make -C gen test
    ```

## 지원 기능

- 엔디안: Little-Endian, Motorola Big-Endian(MSB 톱니형 번호 체계)
- 멀티플렉싱: 스위치(M) 1개 + 분기 신호(m<k>), `valid` 비트마스크 및 `mux_active` enum 제공
- 값 테이블: `VAL_` 파싱 → 시그널별 enum 및 `to_string` 헬퍼 생성
- 스케일 수치 연산 설정: `phys_type` float 또는 fixed + `phys_mode` 세부 선택
- 범위 체크: encode/decode 시 min/max 검증 옵션
- 디스패치 모드: `binary_search` 또는 `direct_map` 레지스트리

## 구성 파일 (config.yaml)

코드 생성 동작을 제어하는 선택적 파일입니다.

- phys_type: "float" | "fixed"
  - float: 물리값 계산에 부동소수 경로 사용
  - fixed: factor가 10^-n이고 offset이 정수인 경우 정수 fast path 사용
- phys_mode: "double" | "float" | "fixed_double" | "fixed_float"
  - double: phys_type=float일 때 중간 계산을 double로 수행(기본)
  - float:  phys_type=float일 때 중간 계산을 float로 수행
  - fixed_double: phys_type=fixed일 때 fast path 불가 시 double 폴백(기본)
  - fixed_float:  phys_type=fixed일 때 fast path 불가 시 float 폴백
  - 기본값(호환 모드):
    - phys_type 생략 또는 "float" → phys_mode는 "double"
    - phys_type이 "fixed" → phys_mode는 "fixed_double"
- range_check: true | false
  - encode/decode 시 min/max를 검증하고 범위를 벗어나면 실패
- dispatch: "binary_search" | "direct_map"
  - CAN ID → decoder 라우팅 전략
- motorola_start_bit: "msb" | "lsb"
  - 모토로라 BE 시작 비트 관례(코드 생성 시 정규화)
  - msb: MSB 기반 톱니형(기본, 도구 일반)
  - lsb: LSB 기반 표기를 내부적으로 MSB 톱니형으로 변환
- crc_counter_check: true | false
  - 향후용(보류): CRC/카운터 자동 검증 훅

예시

```yaml
# examples/config_range_check.yaml
phys_type: float
phys_mode: double     # default when omitted
range_check: true
dispatch: direct_map
crc_counter_check: false
```

```yaml
# examples/config_fixed.yaml
PhysType: fixed
PhysMode: fixed_double  # default fallback; uses integer fast path when applicable
RangeCheck: false
Dispatch: direct_map
CrcCounterCheck: false
```

```yaml
# Single-precision FPU MCU (minimize double ops in fallback)
phys_type: fixed
phys_mode: fixed_float
range_check: true
dispatch: binary_search
crc_counter_check: false
```

### 구성 파일 사용

```bash
# with config
dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen --config examples/config_range_check.yaml
make -C gen build
./gen/build/test_runner test_range_check
```

모토로라 LSB 시작 비트 예시

```bash
# generate with LSB convention (Motorola BE)
dotnet run --project src/Generator -- \
  --dbc examples/motorola_lsb_suite.dbc \
  --out gen \
  --config examples/config_motorola_lsb.yaml

make -C gen build
./gen/build/test_runner test_moto_lsb_basic
./gen/build/test_runner test_moto_lsb_roundtrip
```

## DBC에서 코드가 생성되는 방식

1) 기본 생성

```bash
# Binary search dispatch (default)
dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen
```

2) 구성 파일과 함께

```bash
# Choose phys type, dispatch strategy, range checks, etc.
dotnet run --project src/Generator -- --dbc <your>.dbc --out gen --config examples/config.yaml
```

3) 생성된 C 빌드 검증

```bash
make -C gen build
```

참고
- 생성물은 `gen/include`, `gen/src` 아래에 기록됩니다.
- 예제 테스트 러너 `gen/src/main.c`는 `examples/main.c`를 복사해 둡니다(로컬 테스트 전용, 펌웨어에 포함 금지).

## 대규모 테스트와 스트레스 수트

규모가 큰 DBC에 대해 안정성과 처리량을 빠르게 점검할 수 있도록 스트레스 하네스를 제공합니다.

- 단일 실행(코드 생성/빌드 후):

  ```pwsh
  make -C gen build
  ./gen/build/test_runner test_stress_suite
  ```

- 다수의 DBC에 대해 일괄 실행(PowerShell):

  ```pwsh
  pwsh ./scripts/bulk_stress.ps1
  # 옵션 예시
  # pwsh ./scripts/bulk_stress.ps1 -Config examples/config_directmap_fixed.yaml -OutDir gen
  ```

코퍼스 확장 가이드
- 공개/허용 라이선스의 DBC를 `external_test/` 아래에 추가하세요.
- `scripts/fetch_dbcs.md`에서 출처와 주의사항(라이선스)을 확인하세요. 사내/비공개 파일은 커밋 금지.
- 스트레스 러너는 무작위 페이로드로 인한 디코드 실패를 허용하며, 크래시 방지/처리량/강건성에 초점을 둡니다.

관련 문서
- 테스트 개요와 결과: `TEST_SUMMARY.md`
- 공개 DBC 수집 가이드: `scripts/fetch_dbcs.md`

### 출력 레이아웃 및 이름 규칙
  - sc_utils.h, sc_registry.h (접두사 설정 가능: file_prefix)
  - 메시지별 헤더 `<message>.h` (snake_case 파일명)
- gen/src/
  - sc_utils.c, sc_registry.c (접두사 설정 가능)
  - 메시지별 소스 `<message>.c`
  - main.c (테스트 러너; 펌웨어 빌드에서 제외)

### 메시지 API 네이밍 규칙
- 타입: `<MessageName>_t` (예: `MESSAGE_1_t`, `C2_MSG0280A1_BMS2VCU_Sts1_t`)
- 함수:
  - `bool <MessageName>_decode(<MessageName>_t* out, const uint8_t data[8], size_t dlc);`
  - `bool <MessageName>_encode(uint8_t data[8], size_t* out_dlc, const <MessageName>_t* in);`
- 레지스트리(디스패치):
  - `bool decode_message(uint32_t can_id, const uint8_t data[8], size_t dlc, void* out_msg_struct);`
    - `can_id`가 알려진 메시지면 해당 타입의 구조체로 디코드하여 true 반환

## 펌웨어에 생성물 포함하기

두 가지 통합 방식이 있습니다: 메시지별 직접 호출 또는 레지스트리 기반 디스패치.

- 메시지별 직접 호출
  - `#include "<message>.h"`
  - 메시지 타입을 알고 있을 때 `<Message>_decode(...)` / `<Message>_encode(...)` 호출

- 레지스트리 기반 디스패치
  - `#include "sc_registry.h"` (또는 `<prefix>registry.h`)
  - `decode_message(can_id, data, dlc, &your_msg_struct)`로 런타임에 CAN ID로 라우팅

### 빌드 시스템 예시

Minimal GCC/Make 예시

```make
# Add include path
o_cflags += -I$(PROJECT_ROOT)/gen/include

# Compile all generated sources except the test runner
GEN_SRC := $(wildcard $(PROJECT_ROOT)/gen/src/*.c)
GEN_SRC := $(filter-out $(PROJECT_ROOT)/gen/src/main.c,$(GEN_SRC))

objgen := $(GEN_SRC:.c=.o)

$(objgen): CFLAGS += -I$(PROJECT_ROOT)/gen/include -std=c99 -Wall -Wextra

# Link your firmware with $(objgen)
firmware.elf: $(objgen) $(your_other_objects)
	$(CC) $(LDFLAGS) -o $@ $^
```

CMake 예시

```cmake
file(GLOB GEN_SRC "${CMAKE_SOURCE_DIR}/gen/src/*.c")
list(REMOVE_ITEM GEN_SRC "${CMAKE_SOURCE_DIR}/gen/src/main.c")

add_library(dbccodegen STATIC ${GEN_SRC})
target_include_directories(dbccodegen PUBLIC ${CMAKE_SOURCE_DIR}/gen/include)

add_executable(app main.c)
target_link_libraries(app PRIVATE dbccodegen)
```

벤더 IDE
- `gen/include`를 include path에 추가
- `gen/src/*.c` 중 `gen/src/main.c`를 제외하고 프로젝트에 추가

### 런타임 사용 예시

알려진 메시지 디코드

```c
#include "message_1.h"

void on_frame(const uint8_t* data, size_t dlc) {
    MESSAGE_1_t m = {0};
    if (MESSAGE_1_decode(&m, data, dlc)) {
        // m.Signal_* 사용
    }
}
```

ID 기반(레지스트리) 디코드

```c
#include "sc_registry.h"
#include "message_1.h"

void on_can(uint32_t id, const uint8_t* data, size_t dlc) {
    MESSAGE_1_t m = {0};
    if (decode_message(id, data, dlc, &m)) {
        // id 매칭 시 MESSAGE_1 디코더로 라우팅
    }
}
```

인코드

```c
#include "message_1.h"

void build_frame(uint8_t out[8], size_t* out_dlc) {
    MESSAGE_1_t m = { .Signal_1 = 123.0, .Signal_2 = 45.6 };
    if (MESSAGE_1_encode(out, out_dlc, &m)) {
        // out, *out_dlc 송신
    }
}
```

### 멀티플렉스 메시지

생성기는 DBC 멀티플렉싱을 지원합니다(스위치 `M`, 분기 신호 `m<k>`):
- 디코드: 스위치를 먼저 디코드하고 활성 분기만 디코드합니다. 구조체에는 다음 필드가 포함됩니다.
  - `uint32_t valid`: 필드별 유효성 비트(예: `MSG_VALID_SIGNAL`)
  - `<Msg>_mux_e mux_active`: 알려진 분기값 enum (예: `MSG_MUX_0`, `MSG_MUX_1` …)
- 인코드: 스위치 값이 가리키는 분기에 속한 신호만 인코드합니다.

예시
```bash
dotnet run --project src/Generator -- --dbc examples/multiplex_suite.dbc --out gen --config examples/config.yaml
make -C gen build
./gen/build/test_runner test_multiplex_roundtrip
```

참고
- 분기 선택은 스위치 신호의 원시 정수값 기준입니다(일반 DBC 관례).
- 멀티플렉스가 아닌 기반 신호는 항상 디코드/인코드됩니다.

valid와 mux_active 사용
```c
#include "mux_msg.h"

void handle_mux(const uint8_t data[8]) {
    MUX_MSG_t m = {0};
    if (MUX_MSG_decode(&m, data, 8)) {
        if (m.mux_active == MUX_MSG_MUX_1) {
            if (m.valid & MUX_MSG_VALID_SIG_M1) {
                // m.Sig_m1 사용
            }
        } else if (m.mux_active == MUX_MSG_MUX_2) {
            if (m.valid & MUX_MSG_VALID_SIG_M2) {
                // m.Sig_m2 사용
            }
        }
        if (m.valid & MUX_MSG_VALID_BASE_8) {
            // 기반 신호 처리
        }
    }
}
```

### 값 테이블 (VAL_)

`VAL_` 매핑이 있는 시그널은 C enum과 `to_string` 헬퍼가 생성됩니다:
- 헤더: `typedef enum { <MSG>_<SIG>_<NAME> = <value>, ... } <Msg>_<Sig>_e;`
- 소스: `const char* <Msg>_<Sig>_to_string(int v);` (알 수 없는 값은 `"UNKNOWN"` 반환)

예시
```bash
dotnet run --project src/Generator -- --dbc examples/value_table.dbc --out gen --config examples/config_directmap_fixed.yaml
make -C gen build
./gen/build/test_runner test_value_table
```

enum과 to_string 사용
```c
#include "vt_msg.h"

void log_mode(int v) {
    const char* label = VT_MSG_Mode_to_string(v);
    // OFF/ON/AUTO 또는 UNKNOWN
}

void compare_state(int v) {
    if (v == VT_MSG_STATE_RUN) {
        // 생성된 enum 값 비교
    }
}
```

## PhysType 세부 및 수치 정밀도

`phys_type`은 물리값 계산 방식을 결정합니다. 생성되는 C 구조체의 필드 타입은 ABI 안정성을 위해 항상 32비트 float이며, encode/decode 내부 연산 정밀도/성능은 `phys_mode`로 제어합니다.

요약
- float + phys_mode=double: double 중간 계산 사용(기본)
- float + phys_mode=float: float 중간 계산 사용
- fixed + phys_mode=fixed_double: 10^-n fast path 활성, 폴백은 double(기본)
- fixed + phys_mode=fixed_float: 10^-n fast path 활성, 폴백은 float

세부
- phys_type: float
  - 필드: float(32비트)
  - Decode
    - double: `msg->sig = (float)((double)raw * factor + offset);`
    - float:  `msg->sig = (float)(((float)raw * (float)factor) + (float)offset);`
  - Encode
    - double: `double tmp = ((double)phys - offset) / factor; raw = round(tmp);`
    - float:  `float  tmp = ((float)phys - (float)offset) / (float)factor; raw = llroundf(tmp);`
  - 비고: float 모드에서는 10^-n 정수 fast path를 사용하지 않습니다.

- phys_type: fixed
  - 필드: 여전히 float(API 일관성)
  - Fast path(적용 가능 시 항상 사용)
    - 조건: `factor = 10^-n`, `offset`은 정수
    - Decode: `scale = 10^n`; `phys = (raw + offset*scale) / scale`
    - Encode: `raw = llround((phys - offset) * scale)`
  - 폴백(적용 불가 시)
    - fixed_double: float/double 경로와 동일
    - fixed_float:  float/float 경로와 동일(encode에서 llroundf 사용)

선택 가이드(MCU/FPU)
- 단정밀 FPU에서 double 비용이 큰 MCU:
  - 10^-n 스케일 위주 → phys_type=fixed + phys_mode=fixed_float
  - 임의 스케일 혼재 → phys_type=float + phys_mode=float
- 정밀도 우선(호스트/대형 MCU) → phys_type=float + phys_mode=double 또는 phys_type=fixed + fixed_double

컴파일러 플래그 팁
- `-Wdouble-promotion` (암묵적 double 승격 경고)
- `-fsingle-precision-constant` (FP 리터럴을 float로 취급; 전역 영향 주의)
- 필요 시 템플릿 커스터마이징으로 FP 리터럴에 `f` 접미사 추가 가능

범위 체크
- `RangeCheck/range_check = true`이면 encode/decode 시 min/max를 검증하고 범위를 벗어나면 실패합니다.

## 📊 성능 벤치마크

측정 환경: Apple Silicon(arm64), gcc -O2, 대표 시나리오 기준
- 단순 메시지 라운드트립: 약 5–8M ops/sec
- 복잡/멀티플렉스 메시지 라운드트립: 약 1–4M ops/sec
- 레지스트리 디스패치 처리량: 약 7–72M ops/sec (ID 분포/전략에 따라 상이)
- 대규모 외부 DBC(27개 메시지): 코드 생성/빌드 안정 동작 확인

자세한 데이터는 `scripts/bulk_stress.ps1` 일괄 실행 및 `tmp/stress_reports/summary.csv`에서 확인할 수 있습니다.
요약 문서는 `TEST_SUMMARY.md`에 정리되어 있습니다.

## 🔧 문제 해결 (자주 만나는 이슈)

- fatal error: 'message_1.h' file not found
  - include 경로를 추가하세요: `-I./gen/include` (Make/CMake 예시 참고)

- undefined reference to MESSAGE_1_decode
  - `gen/src`의 생성 오브젝트를 링크하세요 (`gen/src/main.c`는 제외)

- Overlapping signals detected / DLC exceeds size
  - 검증기가 신호 겹침과 DLC 초과를 거부합니다. DBC를 수정하거나 분리하세요.

- 부동소수 반올림 차이로 인한 값 차이
  - 적용 가능하다면 10^-n fast path가 있는 `phys_type: fixed`를 고려하세요.

## ⚠️ 제한사항

- CRC/Counter 자동 검증은 아직 구현되지 않았습니다(설정 플래그는 예약됨)
- 기본 대상은 8바이트 클래식 CAN 프레임입니다(확장 페이로드는 템플릿 조정 필요)
- 32개 초과 신호를 갖는 매우 큰 메시지는 `valid` 비트마스크 확장이 필요할 수 있습니다

## 디스패치 모드, 레지스트리, nanopb와의 관계

메시지별 함수는 항상 생성됩니다:
- 각 메시지에 대해 `<Message>_encode(...)`, `<Message>_decode(...)`

레지스트리는 편의 라우터를 제공합니다: `decode_message(can_id, data, dlc, out_struct)`
- binary_search
  - {id, (옵션) 확장 플래그, 함수 포인터} 정렬 테이블을 이진검색. O(log N). 희소 ID에 유리.
- direct_map
  - ID 기반 직접 매핑(컴팩트 switch 또는 테이블). O(1) 룩업. ID가 희소하면 메모리 비용 증가 가능.

CRC/Counter 참고
- 설정 플래그는 존재하나 자동 검증 구현은 보류 상태입니다. 상위 레이어에서 처리하거나, 추후 YAML 기반 CRC/카운터 매핑 옵션을 기다리세요.

## 엔디안 및 비트 유틸리티

- Little-Endian과 Motorola Big-Endian(MSB 톱니형)을 지원합니다.
- 생성된 `utils.{h,c}`는 메시지 코덱에서 사용하는 `get_bits_le/be`, `set_bits_le/be`를 제공합니다.

## 프로젝트 구조

- `src/Generator`: F# 코드 생성기 소스
- `templates`: C 코드 생성을 위한 Scriban 템플릿
- `examples`: 샘플 DBC/설정/테스트용 main C 파일
- `tests/Generator.Tests`: F# 단위 테스트
- `infra`: CI/CD 설정(예: GitHub Actions)
- `gen`: 생성된 C 코드 출력 디렉터리(보통 gitignore)

## 라이선스, 서드파티, AI 출처

- 라이선스: 이 저장소는 MIT 라이선스를 따릅니다(상단의 LICENSE 참조).
- 서드파티: 테스트에 사용하는 공개 DBC는 저장소에 번들하지 않습니다. `THIRD_PARTY_NOTICES.md`와 `external_test/README.md`의 정책을 참고하세요.
- 기밀: 사내/비공개 DBC는 절대 커밋하지 마세요. `external_test/`는 기본적으로 무시되며(placeholders만 커밋), 로컬에서만 사용하세요.
- AI 도움: 이 저장소의 일부 문서와 코드는 GitHub Copilot 등 LLM의 도움을 받아 작성되었으며, 사람 유지보수자가 검토 후 반영했습니다.

사전 공개 체크리스트
- [ ] 저장소에 비밀 또는 기밀 데이터(특히 external_test/의 DBC)가 포함되지 않았는지 점검
- [ ] LICENSE 및 THIRD_PARTY_NOTICES.md 최신화 확인
- [ ] README(영/한) 내용 일치 및 최신 상태 확인
- [ ] 유효성 검사 실행: dotnet build/test, 코드 생성 + make -C gen build, 핵심 테스트 실행
# DBC to C 파서 코드 생성기 (F#)

이 문서는 한국어 버전입니다. 기능과 사용법은 README.md(영문)와 동일하며, 요약은 아래와 같습니다.

## 지원 기능
- 엔디안: Little-Endian, Motorola Big-Endian(MSB 톱니형)
- 멀티플렉서: 스위치(M) 1개, 분기 신호(m<k>) 지원, `valid` 비트마스크와 `mux_active` enum 제공
- 값 테이블: VAL_ 파싱 → 시그널별 enum 및 to_string 헬퍼 생성
- 설정 가능 스케일 계산: phys_type float/fixed + phys_mode 선택
- 범위 체크: encode/decode에서 min/max 검증 옵션
- 디스패치 모드: binary_search | direct_map

## 빠른 시작
```bash
# 코드 생성
(dotnet 8 필요)
dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen

# C 빌드
make -C gen build

# 예제 러너 실행
./gen/build/test_runner test_roundtrip
```

## 멀티플렉서 사용
- decode는 스위치를 먼저 디코드한 뒤 활성 분기만 디코드합니다.
- 구조체에 `valid` 비트마스크와 `<Msg>_mux_e mux_active`가 포함됩니다.

예시
```c
#include "mux_msg.h"

void handle_mux(const uint8_t data[8]) {
    MUX_MSG_t m = {0};
    if (MUX_MSG_decode(&m, data, 8)) {
        if (m.mux_active == MUX_MSG_MUX_1 && (m.valid & MUX_MSG_VALID_SIG_M1)) {
            // m.Sig_m1 사용
        }
    }
}
```

## 값 테이블 사용
- 헤더에 `<Msg>_<Sig>_e` enum이 생성되고, `<Msg>_<Sig>_to_string(int v)` 함수가 제공됩니다.

예시
```c
#include "vt_msg.h"

const char* label = VT_MSG_Mode_to_string(1); // "ON"
if (1 == VT_MSG_MODE_ON) {
    // enum 값 비교
}
```

## PhysType 개요
- phys_type은 물리값 계산 방식을 결정합니다. 필드 타입은 항상 float로 동일합니다.
- float/double 경로 또는 10^-n 고정소수 fast path를 선택할 수 있습니다.

요약
- float + phys_mode=double|float: 부동소수 경로
- fixed + phys_mode=fixed_double|fixed_float: 10^-n fast path + 폴백 경로

자세한 내용은 README.md의 "PhysType details and numeric precision"을 참고하세요.
