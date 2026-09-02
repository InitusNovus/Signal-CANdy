# SCIMG TX generation fail-closed 통합 보고서

## 📝 작업 요약

SCIMG stateful TX reservation generation의 32-bit ABI를 유지하면서 generation 재사용을 금지하는 fail-closed 정책을 core runtime과 canonical CC1A application까지 통합했다.

Core는 generation `1..UINT32_MAX`를 한 initialization epoch 안에서 한 번씩만 사용한다. terminal reservation 이후 `next_generation == 0`이면 pending reservation 동안 `SC_ERR_BUSY`, pending 해제 후 `SC_ERR_LIMIT`을 mutation 없이 반환한다. Reset은 모든 token/token copy가 폐기된 exclusive quiescent boundary에서만 허용된다.

Canonical CC1A application은 `SC_ERR_LIMIT`을 ordinary failure나 `SC_ERR_BUSY`와 구분해 maintenance-required 상태로 latch하고, latch 이후 runtime prepare 재호출을 차단한다. 자동 reset은 수행하지 않는다. 실제 STM32G474 firmware에서 UINT32_MAX terminal reservation을 CAN wire로 송신하고, 이후 limit latch와 heartbeat 동안의 persistence를 NI CAN3에서 검증했다.

## 🛠 변경 상세

### Signal-CANdy

- commit `7861508b6778703f0c3a4a6e954cb5b80f8b2e1f`
- tree `aead1eac031348416792bb5b62a62f409c3bb010`
- `runtime/c99/src/signal_candy_runtime.c`
  - pending `SC_ERR_BUSY` 우선
  - zero exhaustion sentinel에서 mutation-free `SC_ERR_LIMIT`
  - terminal generation을 1로 순환시키던 로직 제거
- `runtime/c99/include/signal_candy_runtime.h`
  - epoch-scoped uniqueness
  - reset 전 모든 token copy revoke/destruction 계약
  - safe quiescent reset 운영 한계 명시
- `runtime/c99/tests/test_protection_runtime.c`
  - UINT32_MAX terminal pending/busy/cancel/limit/reset 전 과정
  - state/pool/frame/token/scratch 무변경 검증

### CC1A_SignalCANdy_RuntimeTest

- commit `b423160e34cc9f406838aca523b26e4929172e0e`
- tree `17207a96f7796ba417559c5d6fc53a8f86939925`
- guarded encode-prepare application boundary 추가
- maintenance, busy, ordinary failure counter 분리
- maintenance latch 이후 runtime call suppression
- P0/P1-only diagnostic protocol v2 추가
- NI runner에 BUSY, ordinary failure, terminal generation, maintenance persistence scenario 추가
- sync pin을 Signal-CANdy `7861508/aead1eac`로 갱신
- firmware runtime source/header를 core와 byte-identical하게 동기화
- GAS_BSP/GAS_SDP private source 변경 없음

## ✅ 테스트 결과

### Core RED → GREEN

- pre-fix wrap 동작:
  - `FAIL: TX generation exhaustion fails closed until runtime reset`
  - protection 1/21 실패
- final:
  - protection 21/21 PASS
  - TX 15/15 PASS
- mutation review:
  - old wrap 복원 시 protection 실패
  - limit-before-busy 순서 변경 시 protection 실패

### Core release gates

- build: warning 0, error 0
- .NET:
  - Core 405 PASS, platform skip 2
  - Hardening 17 PASS
  - Generator 27 PASS
- Fantomas: PASS
- quality 22/22
- protection 21/21
- activation 57/57 normal
- activation 57/57 ASan/UBSan
- TX 15/15
- malformed 7/7 normal
- malformed 7/7 sanitized
- hardening 10,000 cases PASS
  - sanitizer report 0
  - heapUndefined 0
  - mutableStatic 0
  - binary/resource baseline delta 0
- Cortex-M4 ILP32 ABI:
  - counter state 12 bytes
  - TX token 20 bytes
  - runtime-state counter offset 8 bytes

### Canonical integration

- failing-first application contract: 3 failures
- final host contract: 4/4 PASS
- Python py_compile: PASS
- expectation list: EXPECT-only, PASS claim 없음
- sync rerun: runtime unchanged, generated fixture drift 없음
- canonical firmware build: PASS

### Deterministic firmware

- mode: `SIGNAL_CANDY_P0_P1_GAS_HIL=1`
- BIN SHA-256:
  `f2893951aa3247ace7888102a5de2834f1b19278e30cea5b0b53f8380ee466c5`
- BIN size: 69,496 bytes
- ELF SHA-256:
  `9ceb8010631c638f64d88cd04cdc2acdd79b9215e0f1cadd1d6b959aa58e269e`
- clean build 2회 동일

### Hardware receipts

- claim `cd519cfa-20a5-4570-a0cb-c9a56304018b`
- Connect `600c9cb5db024698ac64eec10b81535e`: Succeeded
- FirmwareUpdate `47dd4a37ad4f401ea7329e2cf814ba20`: Succeeded
- BootloaderRelease `d4cc5e04aa56402eab6bb15bc01969cf`: Succeeded
- Reset `7a31bbeb59704516a34cefa08ffcc2a3`: Succeeded
- Disconnect `f0185d02b9ec468ead1e77e5efb0ed30`: Succeeded
- ST-Link/SWD 미사용

### NI CAN3 HIL

- 50 receipt-bound `PASS:` lines
- terminal `ALL PASS`
- 기존 P0/P1/GAS 35개 acceptance 유지
- 신규 acceptance:
  - transient BUSY는 maintenance를 latch하지 않음
  - ordinary `SC_ERR_POOL`은 별도 failure이며 latch하지 않음
  - UINT32_MAX terminal reservation이 실제 CAN wire에 송신됨
  - 다음 prepare가 `SC_ERR_LIMIT`을 반환하고 maintenance latch
  - 반복 probe가 runtime prepare를 다시 호출하지 않음
  - heartbeat 이후에도 latch 유지

### 정리

- NI session 종료
- TT_Host disconnect 성공
- daemon 종료
- firmware build 및 `__pycache__` 정리
- 관련 process scan PASS
- CC1A claim 하나 유지

## ⏭ 다음 계획

1. Signal-CANdy report commit 후 exact final tree에서 release/HIL evidence를 재확인한다.
2. Signal-CANdy `dev`를 push하고 exact-head GitHub CI success를 확인한다.
3. Frozen core/sibling SHA를 대상으로 independent code/QA/gate review를 수행한다.
4. C003 evidence와 aggregate quality checkpoint를 완료한다.
5. 본 작업은 기존 ROADMAP 항목을 새로 완료한 것이 아니므로 `Plans/ROADMAP.md` 체크박스는 변경하지 않는다.
