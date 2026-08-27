# Runtime Schema Vertical Slice 세션 보고서

- 완료 시각: 2026-08-27 13:46 KST (+09:00)
- 범위: runtime-schema authoring 입력부터 `.scimg`, C99 RX decode, differential 검증까지의 end-to-end 연결

## 📝 작업 요약

- `binding.json` v1 계약을 strict `System.Text.Json` 파서로 구현했다. root/binding/conversion의 unknown·duplicate key를 거부하고 `identity`/`affine` 계약, 필수 수치 필드, non-zero factor를 검증한다.
- 기존 codegen 경로 앞에 `signal-candy scimg` 분기를 추가해 DBC → Wire → Pool/Binding → Linked → Scimg → binary/inspect JSON 경로를 연결했다.
- malloc 없이 고정 상한 버퍼를 사용하는 argv 기반 C99 differential harness와 독립 F# reference evaluator를 추가했다.
- 10개 frame / 20개 slot expectation으로 little endian, affine, identity, Motorola big endian, sign extension, mux active/inactive를 실제 C runtime과 비교했다.
- ROADMAP item 2를 vertical slice 완료 상태로 갱신했다.

## 🛠 변경 상세

- Binding parser는 `InvalidJson`으로 JSON/shape 오류를 반환하고, 파싱 성공 후 기존 `Binding.validate`를 호출한다. programmatic binding에도 affine factor 0 검증이 적용된다.
- CLI 성공 출력은 `Wrote <path> (<n> bytes, messages=<m>, signals=<s>)` 형식이며 `--inspect`가 지정되면 검증된 inspector JSON도 기록한다. 기존 AOT codegen 흐름은 동일한 분기 내부에 유지했다.
- Differential vector 형식은 harness header에 문서화했다.
  - `F <canid-decimal> <ext0|1> <len> <hexbytes...>`: pool을 0으로 초기화하고 frame 1개 decode
  - `E <slot> <expected-u64-decimal> <expected-flags-decimal>`: 직전 frame 결과 확인
- DBC parser의 `IsCrc`/`IsCounter`는 기존 이름 기반 hint이므로 그 자체를 runtime profile로 해석하지 않았다. Wire v1은 실제 `CrcMeta`/`CounterMeta`가 설정된 신호만 unsupported로 거부한다. 이 해석으로 pinned fixture의 일반 신호 `Counter`를 identity binding으로 컴파일하면서 configured CRC/counter profile은 계속 명시적으로 차단한다.
- Reference evaluator는 Scimg/runtime extraction 코드를 재사용하지 않고 fixture IR에서 start/length/order/sign을 직접 읽어 추출, sign extension, affine double 계산, u/i/f32/f64 slot bit 표현을 계산한다.
- 기존 ValidationError formatter의 비완전 패턴을 fallback 처리해 전체 build의 F# warning을 0으로 만들었다.

## ✅ 테스트 결과

- Build: `dotnet build -c Release --nologo`
  - exit 0, errors 0, `warning FS` 0
  - restore 시 기존 Scriban 6.2.1 NU1902/NU1903/NU1904 audit 경고는 유지됨
- Focused differential: `dotnet test -c Release -v minimal --nologo --filter "FullyQualifiedName~Differential"`
  - passed 1, failed 0, skipped 0
- Full suite: `dotnet test -c Release -v minimal --nologo`
  - Core.Tests: passed 165 / 165
  - Generator.Tests: passed 27 / 27
  - 합계: passed 192, failed 0, skipped 0
- Differential gcc compile line(테스트의 temp 경로 치환 표기):
  - `C:\msys64\ucrt64\bin\gcc.exe -std=c99 -Wall -Wextra -Werror -O2 -I<repo>\runtime\c99\include <repo>\runtime\c99\src\signal_candy_runtime.c <repo>\runtime\c99\tests\diff_harness.c -o %TEMP%\scimgdiff-<guid>\diff_harness.exe`
  - exit 0; harness exit 0
- Differential vectors: 10 frames / 20 expectations
  1. `DEMO_STD`: Temp_raw=0, Counter=0
  2. `DEMO_STD`: Temp_raw=1234, Counter=127
  3. `DEMO_STD`: Temp_raw=65535, Counter=255
  4. `DEMO_BE`: Speed_be=0, Signed_be=-1
  5. `DEMO_BE`: Speed_be=4660, Signed_be=-128
  6. `DEMO_BE`: Speed_be=65535, Signed_be=-1
  7. `DEMO_MUX`: selector=0, MuxBranchA untouched (`raw=0`, `flags=0`)
  8. `DEMO_MUX`: selector=1, MuxBranchA=1
  9. `DEMO_MUX`: selector=1, MuxBranchA=65535
  10. `DEMO_MUX`: selector=2, MuxBranchA untouched (`raw=0`, `flags=0`)
- Determinism:
  - 같은 LinkedSchema의 in-test double write가 byte-identical
  - CLI 두 번 출력 SHA-256 동일: `d25dc336c2eb44b39873c2cfa45f8cca00fce54558ea793840f682fd0414726b`
- CLI 실행 출력:
  - `Wrote tmp/g008-demo.scimg (376 bytes, messages=3, signals=7)`
  - `Wrote tmp/g008-demo-2.scimg (376 bytes, messages=3, signals=7)`
- C99 regression: `bash runtime/c99/tests/run.sh` → `ALL PASS (25 tests)`
- Fantomas: 변경 F# 파일 format 후 `fantomas --check` exit 0
- Cleanup receipt: repository demo/image/inspect/test executable 잔여물 0, `%TEMP%/scimgdiff-*` 디렉터리 0

## ⏭ 다음 계획

1. RFC §33 후보를 실제 child issue로 분할한다. 특히 host compiler/CLI/inspect/diff, differential/resource regression, malformed-image fuzzing의 완료 조건과 소유 경계를 명시한다.
2. ROADMAP item 2의 다음 runtime 세대로 TX encode와 logical message ID 경로를 설계·구현한다.
3. stateful counter semantics와 CRC/counter profile resolution을 별도 slice로 진행하고, RX/TX 상태·오류 정책 및 differential oracle을 먼저 고정한다.
