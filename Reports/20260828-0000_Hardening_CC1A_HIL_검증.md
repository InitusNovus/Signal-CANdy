# Hardening CC1A HIL 검증 보고서

## 📝 작업 요약

Issue #25의 deterministic malformed-image property/resource hardening을 CC1A-Test-1에서 최종 검증했다. C99 runtime에 strict UTF-8 symbol 검증이 추가되어 runtime/firmware bytes가 변경되었으므로 sibling sync 후 TT_Host daemon 9802 UART/IAP로만 재플래시하고 activation A/B/C smoke를 실행했다. ST-Link/SWD는 사용하지 않았다.

## 🛠 변경 상세

- Main `c34d9ab`: deterministic SplitMix64 6-base 10,000 corpus, F#/C cross-oracle, ASan+UBSan runners, heap/static scan, build-budget verifier, scripts/hardening.ps1, CI hardening job, C UTF-8 검증 추가.
- Sibling `80ccaa2`: hardened runtime sync(빌드 예산 provenance 고정).
- hardening/build-budget.json 및 receipt: 최종 firmware 68,648 bytes SHA-256 `29d7c601b6e95c2782fbc172be1069672e594883d41d52011b9f989c107cbf18`, 모든 ceiling 미달, 상향 없음.
- TestSupport/ResourceGateTests: CI 전체 실행 30초 제한 → 15분 예산 및 provenance 고정 업데이트.

## ✅ 테스트 결과

- Hardening tests 16/16 PASS (10,000 corpus, cross-oracle 100 accepted/9,900 rejected 일치, sanitizer 0 report, static scan 0).
- C runtime suites 120/120, native A/B/C 11/11 + ALL PASS, 이전 firmware modes 보존 빌드 PASS.
- CC1A flash operation `79686b00`: Succeeded/NoError.
- NI CAN3 activation smoke 29 named PASS + ALL PASS(heartbeat, A TX/RX, prepare/abort, commit gen2 reset, old-A mismatch, B RX9/TX9, malformed C reject, B RX10/TX10).
- Daemon 9802 disconnect/종료, temp/cache 정리 확인.

## ⏭ 다음 계획

Issue #25 close 후 #17 completion audit과 bench claim 반납.
