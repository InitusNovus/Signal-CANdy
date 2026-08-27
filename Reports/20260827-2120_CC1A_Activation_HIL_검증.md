# CC1A Activation HIL 검증 보고서

## 📝 작업 요약

Issue #23 transactional schema activation을 CC1A에서 검증했다. Signal-CANdy main에는 동일한 3-slot pool ABI의 compiler-generated A/B project fixture와 malformed-C native 검증을 추가했고, sibling `CC1A_SignalCANdy_RuntimeTest`에는 `581e16c` runtime, A/B generated headers/descriptors, firmware activation mode, exact CAN control/response/snapshot protocol, NI-XNET host harness를 추가했다. TT_Host daemon 9802 UART/IAP로 firmware를 flash한 뒤 한 번의 연속 CAN3 session과 추가 flash 없이 A→prepare B→abort→commit B→malformed C 흐름을 통과했다. ST-Link/SWD는 사용하지 않았다.

## 🛠 변경 상세

### Main: `Signal-CANdy`

- `examples/scimg_activation_demo/`
  - `pool.json`: protection fixture와 동일한 `RxValue/TxValue/MarkerA5` ABI, RX freshness 30 ms.
  - `schema_a.dbc`, `binding_a.json`, `project_a.yaml`: RX `0x326`, TX `0x325`, logical 33, TX initial 0.
  - `schema_b.dbc`, `binding_b.json`, `project_b.yaml`: RX `0x336`, TX `0x335`, logical 33, TX initial 9.
  - `cc1a-test-1.runtime.json`: image/state/scratch/pool을 444/60/8/3으로 고정하고 exact feature/capability를 선언.
  - `test_activation_demo.c`, `run_native_test.sh`: real runtime에서 A -> prepare B -> abort -> A -> commit B -> malformed C -> B를 실행하며 exact RX/TX vectors와 atomic C rejection을 확인.
  - `README.md`: actual project CLI, hashes, provenance, exact one-flash sequence와 `0x7A2..0x7A4` wire contract.
- Generated outputs(ignored): `examples/scimg_activation_demo/build/`의 A/B image, inspect, activation JSON, header.

### Sibling: `../CC1A_SignalCANdy_RuntimeTest`

- `firmware/external/signal_candy/{include,src}/`: Signal-CANdy commit `581e16c`와 byte-identical sync.
- `firmware/tools-expected/scimg_activation_{a,b}.h`: actual project-generated header/descriptor를 체크인 대상으로 추가.
- `firmware/Makefile`, `firmware/App/Inc/App_SignalCandyRuntime.h`: 기존 mode와 상호 배타적인 `SIGNAL_CANDY_ACTIVATION_HIL` 선택 및 두 header dependency.
- `firmware/App/Src/App_SignalCandyRuntime.c`: static controller/token, 독립 A/B schema/state buffers, exact 8-byte scratch, 3-slot pool, firmware-only malformed C, A boot, tracked TX terminals, background-owned quiescent commit, accepted-RX `0x7A1`, exact `0x7A2/0x7A3/0x7A4` 처리. Volatile source copy를 사용해 B image가 firmware binary에 한 번만 포함되도록 유지.
- `host/sc_validate_activation.py`: CAN3 receive-before-actions, exact event inbox, monotonic bounded timeout, robust nonblocking empty read, deterministic cleanup, named PASS transcript와 continuous one-flash proof.
- `tools/sync_signal_candy.py`: activation A/B actual project builds, generated descriptors/images/headers sync, protection project-header preservation, runtime drift policy 유지.
- Root/firmware/host/tools README에 mode, commands, hashes, protocol, no-reflash 절차를 동기화.

## ✅ 테스트 결과

- Main actual CLI clean validation/build: A/B 모두 PASS; validation 이후 output 없음 확인 후 project build 성공.
- A/B 공통: image 444 bytes, state 60 bytes, scratch 8 bytes, pool 3, flags `0x0007`, pool ABI `3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947`.
- Native generated-fixture driver: 11 named PASS + `ALL PASS`.
- Runtime activation regression: 45/45 PASS.
- Descriptor/image/hash/capacity independent parser check: A/B PASS.
- ARM activation firmware: clean `-Wall -Wextra -Werror` build deterministic twice.
  - `firmware/build/sc_runtime_test.bin`: 68,376 bytes, SHA-256 `9ebb7a891334fef106aed436eb8d46d93666ea173bb2ed3b2c3226b1ed0b80b5`.
  - `.isr_vector`/FLASH origin: `0x08008000`; `.text` starts `0x080081E0`.
  - A and B full 444-byte images are each embedded exactly once; descriptor and byte-array symbols are each singular.
- Preserved firmware modes all clean `-Werror` PASS: default TX, quality, protection, original RX.
- Python: `py_compile`, AST parse, LSP diagnostics, and `--list-expectations` PASS; no NI-XNET session was opened.
- Sync second run: runtime and activation expected headers unchanged; exact hashes matched.
- `git diff --check` main/sibling PASS. Generated test executable trap removed the executable; Python caches were removed.
- LSP C diagnostics without the firmware include/define context reported missing includes only; the real ARM compile with complete firmware include paths passed with warnings-as-errors.

### CC1A-Test-1 no-reflash activation HIL

- 첫 flash는 startup slot-count assertion(`3` slots를 `1`로 비교) 때문에 SOS로 진입했다. 이를 mode slot count로 수정했다.
- 다음 flash는 heartbeat까지 통과했으나 0x7A2 dispatch가 FD-capable capture metadata의 classic flag를 잘못 요구해 command를 거부했다. ID/type/8-byte payload 검증으로 수정했다.
- EMIT_TX response/data의 단일 TX queue 경합도 발견해 response service 다음 iteration에 data enqueue를 지연했다.
- 최종 firmware: 68,408 bytes, SHA-256 `09e4aad7bb88de29d36b83b0fd8351e5e2cb594c453e96c1ae25a1336f329f83`, origin `0x08008000`.
- final TT_Host flash operation `57e14ecf`: `Succeeded`, `NoError`.
- 최종 연속 session에서 29 named PASS와 `ALL PASS`:
  - A TX0/RX0, prepare B 중 A RX1 수락 및 B no-match.
  - abort 후 A TX1.
  - commit B에서 active B/generation 2.
  - raw `0x5679` 보존, RX flags zero, B RX counter와 TX counter reset.
  - old A no-match, B RX9/TX9.
  - malformed C `SC_ERR_CRC`, B state/pool/generation 불변.
  - B RX10/TX10 및 heartbeat continuity.
- 최종 A→B→C sequence 중 flash는 없었다. NI sessions close, daemon 9802 disconnect/종료, temp/cache 제거를 확인했다.

Exact generated hashes:

| Artifact | SHA-256 |
|---|---|
| A image | `9197bf85693f823f3623f9562a2a892468dc461a1c7cdaf4f60a6dc91cad6d1e` |
| A activation JSON | `fc7a44d1b989026173831cbc6c936f92031b17977fb272e6ec3f2869c9fd55f7` |
| A header | `8608191506901030fb66e9669e103df76e5500a4e68d836ea4dbee4083d7c57d` |
| B image | `6b1a5bdf3255bff17e12195bea2fd4703ae6427e06f2e701d7fde231e05312f2` |
| B activation JSON | `fe65f0d46a6dea1e0847e1cb428c9ab9d4c2f9adc48442f9e8cff8c459f43d4f` |
| B header | `2d748d4ff200afee0038895730185164dfc797ea5f6a238cb755fc851c34d265` |
| Runtime public header | `a2d85334595134a21fb38f014a3a7c8621d51e19a9e9c333353c1a77e8c144bb` |
| Runtime source | `09caecd4228004006c9493e914b56d2f44a768e6c689b569cc180b1a4f73d0ea` |

## ⏭ 다음 계획

Issue #23에 commit/test/HIL 증거를 기록하고 close한다. 그 뒤에만 host inspect diff/map JSON child를 연다. bench claim은 final child까지 유지한다.
