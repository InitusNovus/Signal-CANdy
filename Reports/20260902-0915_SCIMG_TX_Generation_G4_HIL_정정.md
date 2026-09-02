# SCIMG TX generation 정책 G4 HIL 정정 보고서

이 문서는 `Reports/20260902-0908_SCIMG_TX_Generation_소진정책.md`의 검증 누락을 patch-forward 방식으로 정정한다. 원본 보고서는 수정하지 않는다.

## 📝 작업 요약

이전 보고서에서 Fantomas가 설치되지 않았다고 잘못 판단했고, SCIMG TX generation fail-closed 변경을 host/native에서만 검증한 채 CC1A 실기기 HIL을 누락했다.

Fantomas의 올바른 실행 형식인 `fantomas --check src/ tests/`로 포맷 검사를 재수행해 PASS를 확인했다. 변경된 C99 runtime을 CC1A STM32G474 firmware runtime 사본에 byte-identical하게 반영하고, 결정론적 firmware를 TT_Host UART/IAP로 flash한 뒤 NI CAN3 P0/P1/GAS 통합 HIL을 재실행해 terminal `ALL PASS`를 확인했다.

## 🛠 변경 상세

### Signal-CANdy

- 제품 변경은 앞선 세션과 동일하다.
  - `runtime/c99/include/signal_candy_runtime.h`
  - `runtime/c99/src/signal_candy_runtime.c`
  - `runtime/c99/tests/test_protection_runtime.c`
- 본 세션 신규 문서:
  - `Reports/20260902-0915_SCIMG_TX_Generation_G4_HIL_정정.md`

### CC1A HIL firmware

- 다음 firmware runtime 사본을 현재 Signal-CANdy 변경과 byte-identical하게 동기화했다.
  - `firmware/external/signal_candy/include/signal_candy_runtime.h`
  - `firmware/external/signal_candy/src/signal_candy_runtime.c`
- sync tool은 frozen committed HEAD/tree만 허용하고 현재 변경은 미커밋 상태이므로, 동일한 최소 patch를 firmware 사본에 적용한 뒤 `cmp`로 두 파일의 byte identity를 확인했다.
- GAS_BSP/GAS_SDP private source는 변경하지 않았다.

## ✅ 테스트 결과

### Fantomas 정정

- 잘못된 호출: `dotnet fantomas --check src/ tests/`
- 올바른 호출: `fantomas --check src/ tests/`
- 결과: PASS, 출력 없음, exit 0

### STM32G474 firmware build

- canonical WPC-03 toolchain:
  - GCC 13.3.rel1 via `STM32_TOOLCHAIN_ROOT`
  - Make via `STM32_MAKE_ROOT`
- mode: `SIGNAL_CANDY_P0_P1_GAS_HIL=1`
- link:
  - text 67,828 bytes
  - data 1,116 bytes
  - bss 49,192 bytes
- clean build 2회 deterministic equality:
  - BIN SHA-256 `e9e14fbd294f7efa73af3125be402dcab6734d02a77ca6ba7e33f67307f120ff`
  - BIN size 68,952 bytes
  - ELF SHA-256 `956e859cfbbc88ef2011bbfc25b4db13b906058448e00a2da8d0e8539d2952c3`

### Hardware authority

- Manager target: `set:CC1A-Test-1`
- internal target: `set:cc1a-0025-bootloader-010`
- claim: `cd519cfa-20a5-4570-a0cb-c9a56304018b`
- owner: `agent:signal-candy-runtime`
- state: active/InUse, review overdue 아님
- exact interfaces:
  - TT_CC1A26_0025
  - TT_Host `127.0.0.1:9802`
  - UART/IAP `COM22`
  - FDCAN1 `NI CAN3`
- ST-Link/SWD 미사용

### TT_Host-only flash receipts

- Connect `2f67dc1d41f24fcda6b8ac78963ee46c`: Succeeded/NoError
- FirmwareUpdate `a7b6d5cb3e394f63849c478fd6f42636`: Succeeded/NoError
- BootloaderRelease `5082febc060746b48a6812770cd32051`: Succeeded/NoError
- Reset `e538c082bf754a3f96274cf7e9676beb`: Succeeded/NoError
- Disconnect `2f8d6984988545beafc313a3993754c6`: Succeeded/NoError

### NI CAN3 HIL

최종 실행은 35개의 receipt-bound `PASS:` line과 terminal `ALL PASS`를 출력했다.

- heartbeat
- reserve/cancel/transmitted=0 counter repeat
- actual tracked GAS enqueue 후 counter advance
- classic RX
- 실제 `SC_FRAME_FD`를 통과하는 64-byte FD 정규화
- extended unmatched traffic 무변경
- mixed classic/FD
- bad CRC/counter 원자적 거부 및 recovery
- pending TX activation busy와 기존 schema 보존
- generation 2 commit 및 old-schema drain
- malformed candidate 무변경 거부
- B RX/TX recovery
- heartbeat uptime advance

HIL은 변경 runtime이 실제 MCU firmware에 링크되어 기존 P0/P1/GAS 동작을 깨뜨리지 않았음을 입증한다. `UINT32_MAX` 소진 자체를 43억 회 target에서 반복하지는 않으며, 그 경계 동작은 native RED→GREEN 테스트가 직접 검증한다.

### 최종 회귀 및 정리

- protection 21/21 PASS
- Fantomas PASS
- runtime source/header firmware 사본 byte-identical
- `git diff --check` PASS
- NI session 종료
- TT_Host disconnect 성공 후 daemon 종료
- firmware build 및 host `__pycache__` 정리
- 관련 live process scan PASS
- CC1A claim은 기존 사용자 지시대로 유지

## ⏭ 다음 계획

1. `SC_ERR_LIMIT`을 받는 application integration은 safe quiescent reset을 요구하는 maintenance 상태로 처리한다.
2. MCU HIL은 정상 TX/activation 회귀를 담당하고, 32-bit generation 소진 경계는 deterministic native test를 source-of-truth로 유지한다.
3. 변경을 commit/push할 때 HIL firmware sibling의 동기화 patch와 본 receipt를 함께 추적한다.
4. 본 세션에서는 `Plans/ROADMAP.md` 항목을 완료하지 않았으므로 ROADMAP 체크박스는 변경하지 않았다.
