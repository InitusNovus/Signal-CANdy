# SCIMG TX generation 소진 정책 적용 보고서

## 📝 작업 요약

SCIMG C99 runtime의 stateful TX 예약 generation이 `uint32_t` 범위를 순환해 과거 token과 다시 일치할 수 있는 ABA 잔여 위험을 제거했다.

32-bit MCU의 64-bit 연산 비용과 기존 공개 ABI를 고려해 generation을 `uint64_t`로 확대하지 않았다. 대신 generation `1..UINT32_MAX`를 한 번씩만 사용하고, 마지막 generation 이후 `next_generation == 0`을 영구 소진 sentinel로 유지하는 fail-closed 정책을 적용했다.

소진된 counter의 `sc_encode_prepare`는 `SC_ERR_LIMIT`을 반환하며 state, pool, output frame, token을 변경하지 않는다. 안전한 quiescent point에서 `sc_runtime_reset` 또는 새 state의 `sc_runtime_state_init`을 수행해야 generation 1부터 다시 사용할 수 있다.

## 🛠 변경 상세

- `runtime/c99/src/signal_candy_runtime.c`
  - pending reservation 확인 후 `next_generation == 0`이면 즉시 `SC_ERR_LIMIT` 반환.
  - `UINT32_MAX + 1`을 1로 되돌리던 wrap/reuse 로직 제거.
  - `UINT32_MAX` 예약 후 자연스럽게 0 sentinel에 도달하도록 변경.
- `runtime/c99/include/signal_candy_runtime.h`
  - nonzero 32-bit generation 범위와 0 소진 sentinel 계약 명시.
  - 소진 시 `SC_ERR_LIMIT`, mutation 없음, reset/init으로만 복구한다는 운영 정책 명시.
  - 32-bit ABI를 유지해 MCU에서 추가 64-bit 연산과 state 크기 증가가 없음을 명시.
- `runtime/c99/tests/test_protection_runtime.c`
  - `UINT32_MAX` generation 예약/취소가 정상 완료되는지 검증.
  - 다음 prepare가 `SC_ERR_LIMIT`으로 실패하고 state/pool/frame/token을 변경하지 않는지 검증.
  - `sc_runtime_reset` 후 generation이 1로 복구되는지 검증.

## ✅ 테스트 결과

### RED → GREEN

- 수정 전:
  - `FAIL: TX generation exhaustion fails closed until runtime reset`
  - protection suite 1/21 실패.
- 수정 후:
  - `PASS: TX generation exhaustion fails closed until runtime reset`
  - protection 21/21 PASS.

### Native/runtime

- quality 22/22 PASS
- protection 21/21 PASS
- activation 57/57 PASS
- activation ASan/UBSan 57/57 PASS
- TX 15/15 PASS
- malformed representatives normal/sanitized PASS
- hardening 10,000 cases PASS
  - ASan/UBSan report 0
  - heapUndefined 0
  - mutableStatic 0
  - binary/flash/RAM/stack baseline delta 0

### Managed/build

- `dotnet build --configuration Release --nologo`
  - warning 0, error 0
- 전체 .NET:
  - Core 405 PASS, platform skip 2
  - Hardening 17 PASS
  - Generator 27 PASS
- `git diff --check`: PASS
- Fantomas는 로컬에 설치되어 있지 않아 실행하지 못했다. F# 파일은 변경하지 않았다.

### 수동 동작 확인

실제 public API 호출 순서로 `UINT32_MAX` 예약 → cancel → 다음 prepare 거부 → runtime reset → generation 1 복구를 native protection executable에서 확인했다.

## ⏭ 다음 계획

1. 호출 애플리케이션은 `SC_ERR_LIMIT`을 일반 전송 오류로 무한 재시도하지 말고 maintenance/reset 요구 상태로 취급한다.
2. reset은 pending TX가 없고 runtime 접근이 중단된 안전한 quiescent point에서만 수행한다.
3. 다음 SCIMG ABI를 설계할 때도 32-bit MCU 비용을 우선 고려하며, generation 폭 확대는 실제 target 성능 측정 없이 채택하지 않는다.
4. 이번 정책은 SCIMG runtime의 stateful TX counter에만 적용하며 activation controller generation/serial 정책과 혼동하지 않는다.
5. 본 세션에서는 `Plans/ROADMAP.md`의 활성 항목을 완료하지 않았으므로 ROADMAP 체크박스는 변경하지 않았다.
