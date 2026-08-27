# CC1A Project Manifest HIL 검증 보고서

## 📝 작업 요약

Issue #22의 CC1A-Test-1 Target Capability와 Project Manifest를 `examples/scimg_protection_demo/`에 표준 fixture로 추가하고 실기기 검증했다. 실제 Release CLI surface로 manifest-relative validate/build, 부족 capability 거부, 원자적 무출력, 반복 결정성, direct `scimg` 경로와의 byte identity를 확인했다. manifest가 만든 header를 sibling firmware에 동기화하고 TT_Host daemon 9802 UART/IAP로 flash한 뒤 NI-XNET CAN3 smoke를 통과했다. ST-Link/SWD는 사용하지 않았다.

## 🛠 변경 상세

- 표준 capability:
  - `examples/scimg_protection_demo/cc1a-test-1.runtime.json`
  - `examples/scimg_protection_demo/cc1a-test-1.insufficient.runtime.json`
  - image/state/scratch `428/28/8`; 부족 fixture는 state만 `27`이다.
  - RX message/program `1/1`, TX message/program `1/2`, pool/conversion `3/1`, nested/depth/quality `0/0/0`, protection plan/TX counter/RX counter/span `2/1/1/2`, template/payload `8/8`을 고정했다.
  - feature는 `rx`, `tx`, `crc8-sae-j1850`, `crc16-ccitt-false`, `rx-counter`, `tx-counter`이며 pool ABI는 `sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947`이다.
- 표준 project:
  - `examples/scimg_protection_demo/project.yaml`
  - `examples/scimg_protection_demo/project-insufficient.yaml`
  - manifest-relative 입력과 `build/{protection_demo.scimg,scimg_protection_demo.h,protection_demo.inspect.json}` 출력을 선언했다.
- `examples/scimg_protection_demo/README.md`에 표준 validate/build, exact boundary, state-27 negative case, image/header hash와 symbol을 문서화했다.
- project-built 3,105-byte header를 `../CC1A_SignalCANdy_RuntimeTest/firmware/tools-expected/scimg_protection_demo.h`에 동기화했다. 배열 byte는 동일하고 generator provenance만 project build로 바뀌었다. 기존 TX default, RX, quality, protection mode selection은 변경하지 않았다.

## ✅ 테스트 결과

### 실제 CLI exit/stdout/stderr

Release driver는 `src/Signal.CANdy.CLI/bin/Release/net8.0/Signal.CANdy.CLI.dll`이었다.

- `project --help`: exit `0`, stderr empty, stdout:
  ```text
  Project commands:
    project validate <manifest.yaml>
        Parse, resolve, compile, and validate target compatibility without writing files.

    project build <manifest.yaml>
        Validate first, then atomically write the manifest-declared artifacts.
  ```
- bad grammar `project unknown <project.yaml>`: exit `2`, stdout empty, stderr `Usage: project validate|build <manifest.yaml>\n`.
- missing manifest `project validate <does-not-exist.yaml>`: exit `4`, stdout empty, stderr `error[SC2204] manifest input does not exist: C:\\Users\\initusnovus\\Desktop\\Workspace\\Signal-CANdy\\examples\\scimg_protection_demo\\does-not-exist.yaml\n`; output은 없었다.
- 서로 다른 system-temp CWD 두 곳에서 absolute `project.yaml`을 validate: 각각 exit `0`, stderr empty, stdout `Project valid: scimg-protection-demo (image=428 bytes, state=28 bytes, scratch=8 bytes)\n`. 두 실행 전후 fixture snapshot은 byte-identical이고 `build/`와 `.signal-candy-*.tmp`는 없었다.
- insufficient validate와 build: 각각 exit `3`, stdout empty, stderr `error[SC2207] target.maxRuntimeStateBytes: required 28, supported 27\n`. 각 실행 후 `build/`와 staging temp는 없었다.
- fresh build 두 번: 각각 exit `0`, stderr empty, stdout:
  ```text
  Built scimg-protection-demo: build/protection_demo.scimg (428 bytes)
  Wrote header: build/scimg_protection_demo.h
  Wrote inspect: build/protection_demo.inspect.json
  ```
  두 fresh build의 세 artifact는 모두 byte-identical했다.
- direct `scimg`: exit `0`, stderr empty, stdout `Wrote <system-temp>/direct.scimg (428 bytes, messages=1, signals=1)\n`; project image와 inspect가 각각 byte-identical했다.

### Artifact와 firmware 증거

- image: 428 bytes, SHA-256 `26e6f8529af6c840d294a87cb967a490b9cd78394b2c9911fee32681660fe7df`.
- inspect: 456 bytes, SHA-256 `9b5fe2f0f050456afe339b33286446fec980416dc83534959193d1deb4fca434`.
- project header: 3,105 bytes, SHA-256 `f07304bebbf627d64955c77221e786470d0d5abe49b449a13b024af5d17dc3bb`; `GSCIMG_PROTECTION_DEMO_BYTE_COUNT 428u`와 `gScimgProtectionDemoBytes[...]`가 각각 정확히 한 번 정의된다.
- sibling protection clean build 2회는 동일했다 (`-Wall -Wextra -Werror`):
  - final BIN `../CC1A_SignalCANdy_RuntimeTest/firmware/build/sc_runtime_test.bin`: 59,748 bytes, SHA-256 `a7f5b0236f033da7db89393e1d212da96fd1ec47dd016d868d9269e443cd4b3b`.
  - ELF: 2,348,428 bytes, SHA-256 `43aa5b12d6cbfdd737043c2495c84bbb9fec7d5df82e550ee56a5cd1e9ddfaba`.
  - size: text `58,624`, data `1,116`, bss `48,112`; `.isr_vector` VMA/LMA 및 BIN origin은 `0x08008000`.
  - image symbol `gScimgProtectionDemoBytes`: address `0x080162A4`, size `0x1AC`; manifest image는 final BIN offset `0xE2A4`에 정확히 한 번 존재한다. 따라서 final BIN은 manifest가 만든 동일 428-byte artifact를 실행한다.

### Validator

- focused issue #22 F# tests: `100 passed`, `0 failed`, `2 skipped` (Windows에서 symlink unavailable인 두 reparse test).
- `dotnet build src/Signal.CANdy.CLI/Signal.CANdy.CLI.fsproj -c Release --no-restore`: `0 errors`; 기존 Scriban advisory `NU1902/NU1903/NU1904` 14개.
- Fantomas `7.0.6 --check` on issue #22 source/tests: PASS. 저장소의 구형 bundled Fantomas는 F# shorthand syntax를 parse하지 못했으나 현재 도구로 재검증했다.
- C99 protection driver: `ALL PASS (17 tests)`.
- sibling Python `py_compile` 및 `sc_validate_protection.py --list-expectations`: PASS, 마지막 출력 `ALL PASS`.
- capability LF/no-BOM/JSON shape와 두 capability의 유일한 차이 `limits.maxRuntimeStateBytes: 28 -> 27`: PASS.
- 양 저장소 `git diff --check`: PASS. root project output/temp/cache는 증거 기록 후 제거했고 sibling final protection BIN만 ready 상태로 남겼다.

### CC1A-Test-1 manifest-built HIL

- TT_Host flash operation `1ea1a3db`: `Succeeded`, `NoError`.
- heartbeat `up_ms=1006`, `rx_matched=0`.
- TX counter 0: `00 34 12 A5 00 00 00 A5`.
- TX counter 1: `01 34 12 A5 00 00 00 F8`.
- valid RX diagnostic: `01 00 00 00 78 56 03 01`.
- bad CRC diagnostic: `01 01 00 01 78 56 03 01`.
- counter jump diagnostic: `01 01 01 02 78 56 03 01`.
- 최종 `ALL PASS`.
- NI sessions close, daemon 9802 disconnect/종료, temp/cache 제거를 확인했다.

## ⏭ 다음 계획

Issue #22에 commit/test/CLI/HIL 증거를 기록하고 close한다. 그 뒤에만 schema activation/hot-swap child를 연다. bench claim은 final child까지 유지한다.
