# CC1A Protection HIL 검증 보고서

## 📝 작업 요약

Signal-CANdy issue #21 CRC/counter 보호 프로파일을 CC1A-Test-1에서 검증했다. `examples/scimg_protection_demo`의 표준 ID `0x325` TX/논리 ID 33과 표준 ID `0x326` RX fixture를 실제 CLI로 생성·검사했고, sibling `CC1A_SignalCANdy_RuntimeTest`의 `SIGNAL_CANDY_PROTECTION_HIL` 모드와 NI-XNET CAN3 하네스로 exact CRC/counter 및 거부 원자성을 확인했다. TT_Host daemon 9802 UART/IAP만 사용했고 ST-Link/SWD는 사용하지 않았다.

## 🛠 변경 상세

- `examples/scimg_protection_demo/{protection_demo.dbc,pool.json,binding.json,README.md}`: CRC-8/SAE-J1850 TX, CRC-16/CCITT-FALSE little-endian RX, 4-bit modulus-16 counter, 고정 벡터 및 결정론 기대값을 추가했다.
- `docs/RuntimeImageFormat.md`: EX01 protection feature, PR01 header/plan/counter/span, CRC profile, caller-owned state, RX rejection 원자성, TX prepare/terminal commit 계약을 규범화했다.
- `src/Signal.CANdy.Core/Scimg.fs`, `tests/Signal.CANdy.Core.Tests/ProtectionScimgTests.fs`: protection-only 이미지에서 EX01 quality count를 pool slot count로 잘못 기록해 reader/inspect가 `ImageTable`로 거부하던 문제를 실제 quality entry count(0)로 수정하고 회귀 테스트를 추가했다.
- sibling runtime/header:
  - `firmware/external/signal_candy/{include,src}`를 `3d28c7e`와 byte-identical하게 동기화했다.
  - `firmware/tools-expected/scimg_protection_demo.h`를 timestamp-free로 생성하고 Makefile 선택 경로를 추가했다.
- sibling firmware:
  - `firmware/Makefile`, `App_SignalCandyRuntime.{h,c}`에 세 HIL 모드 상호 배타성 및 protection mode를 추가했다.
  - caller-owned schema/state/scratch를 사용한다. heartbeat 후 logical 33을 tracked enqueue하고 `GAS_FDCAN_V1_TERMINAL_SENT`에서만 counter commit한다.
  - `sc_decode_state`로 `0x326`을 처리하고 CRC/counter 거부 시 state/pool을 보존한 채 고정 `0x7A1` classic 8-byte 진단을 발생시킨다.
- sibling host/tool/docs:
  - `host/sc_validate_protection.py`는 수신 세션을 trigger 전에 시작하고 blocking event read + bounded timeout을 사용한다. correctness sleep/poll은 없고 empty nonblocking drain과 cleanup을 처리한다.
  - `tools/sync_signal_candy.py`에 `protection` fixture를 추가하고 root/firmware/host/tools README를 갱신했다.
- 활성 `Plans/ROADMAP.md` 체크박스에 직접 대응하는 항목은 없어 변경하지 않았다. 기존 Reports 이력은 수정하지 않았다.

## ✅ 테스트 결과

- 실제 CLI 2회 byte 비교 및 sibling sync 결과: PASS.
  - image: 428 bytes, SHA-256 `26e6f8529af6c840d294a87cb967a490b9cd78394b2c9911fee32681660fe7df`, CRC32 `0x5B65B079`, flags `0x0005`.
  - inspect: RX messages/programs `1/1`, pool slots `3`, conversions `1`, TX messages/programs/counters `1/2/1`, nested/quality `0/0`.
  - offsets: EX01 `160` (264 bytes), PR01 `200` (104 bytes), TX01 `304` (120 bytes), footer `424`.
  - header: 3,138 bytes, SHA-256 `3b82c26c39f8bbd47ae56a1cb797ecbf5fcd0ea1ad37b867caef9463cc251e35`; timestamp 없음, 배열 정의 1개.
- `dotnet build Signal.CANdy.sln -c Release --no-restore`: 0 errors, 기존 Scriban NU1902/NU1903/NU1904 advisory warnings 14개.
- protection F# tests: 48/48 PASS. C99 protection tests: 17/17 PASS. 생성된 실제 428-byte fixture를 C99 runtime에 열어 두 TX payload, valid decode, bad-CRC/counter-jump 상태 불변을 확인한 host harness도 PASS.
- sibling `-Wall -Wextra -Werror` preservation builds: TX, RX, quality 모두 PASS.
- sibling protection clean build 2회: binary hash/size 동일 PASS.
  - ELF: 2,348,432 bytes, SHA-256 `2fde702c6a6f7391e0f9eb2e8a229b78badfbc10fbbdeb5a48c4456d1f59f9d8`.
  - BIN: 59,740 bytes, SHA-256 `62bf044052e41278aba79717ea04e22dc22a70932f2ec8b90be09ea90fefd78e`.
  - size output: text 58,616 / data 1,116 / bss 48,112; `.isr_vector` VMA/LMA `0x08008000`; embedded image symbol size `0x1AC` and exact image occurrence 1.
  - runtime header/source SHA-256: `85a8325a42d350e726eef58b0909fa24a6c1ae96000905775c7c25fb6e4c0a4c` / `50a8ca4fd2607ba0d156a9848ec61979ff1a272f2cbf8c9af97f37aa94a4fc7d`.
- `py_compile`, `--list-expectations`, deterministic header assertions, changed-file diagnostics, `git diff --check`: PASS.
- 첫 flash operation `e6f705cf`는 성공했으나 heartbeat가 없었다. Protection fixture의 pool slot count는 3인데 firmware init이 1을 assert하여 SOS 경로로 진입한 것이 원인이었다.
- init guard를 `APP_SIGNAL_CANDY_SLOT_COUNT`로 수정한 firmware BIN은 59,748 bytes, SHA-256 `a7f5b0236f033da7db89393e1d212da96fd1ec47dd016d868d9269e443cd4b3b`.
- TT_Host flash operation `c0b16495`: `Succeeded`, `NoError`.
- NI CAN3 실기기 결과:
  - heartbeat `up_ms=1006`, `rx_matched=0`.
  - TX counter 0: `00 34 12 A5 00 00 00 A5`.
  - TX counter 1: `01 34 12 A5 00 00 00 F8`.
  - valid RX diagnostic: `01 00 00 00 78 56 03 01`.
  - bad CRC diagnostic: `01 01 00 01 78 56 03 01`.
  - counter jump diagnostic: `01 01 01 02 78 56 03 01`.
  - 최종 `ALL PASS`.
- NI sessions close, daemon 9802 disconnect/종료, temp/cache 제거를 확인했다.

## ⏭ 다음 계획

Issue #21에 commit/test/HIL 증거를 기록하고 close한다. 그 뒤에만 Target Capability + Project Manifest child를 연다. bench claim은 최종 child까지 유지한다.
