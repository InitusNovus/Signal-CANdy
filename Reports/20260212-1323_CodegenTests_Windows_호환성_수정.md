# 작업 보고서 — CodegenTests Windows 호환성 수정

> **작업일시**: 2026-02-12 ~10:20  
> **ROADMAP 항목**: 해당 없음 (기존 테스트 인프라 버그 수정)  
> **선행 세션**: C-1/C-2 Critical 수정 세션

---

## 📝 작업 요약

**한 줄 요약**: `CodegenTests.fs`의 C 빌드 통합 테스트 5건이 Windows에서 `make` 명령어를 찾지 못해 실패하던 문제를 `mingw32-make` 분기로 해결했다.

### 문제

`buildAndRunCTest` 함수가 `make`를 하드코딩으로 호출했으나, Windows 환경에서는 `make`가 PATH에 없고 `mingw32-make`(MinGW)만 사용 가능했다. 또한 테스트 러너 바이너리 경로에 `.exe` 확장자가 빠져 있어 Windows에서 프로세스 시작이 실패했다.

### 해결

1. `System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform`으로 OS 감지
2. Windows → `mingw32-make`, Linux/macOS → `make` 분기
3. 테스트 러너 경로: Windows → `test_runner.exe`, 기타 → `test_runner`

---

## 🛠 변경 상세

### 수정된 파일

| 파일 | 변경 내용 |
|------|-----------|
| `tests/Generator.Tests/CodegenTests.fs` | `open System.Runtime.InteropServices` 추가. `isWindows`, `makeCommand` private 헬퍼 추가. `buildAndRunCTest`에서 `make.StartInfo.FileName <- makeCommand` 및 `runnerName` 분기 적용 |

### 변경 코드 상세

```fsharp
// 추가된 헬퍼
let private isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
let private makeCommand = if isWindows then "mingw32-make" else "make"

// buildAndRunCTest 내 변경
make.StartInfo.FileName <- makeCommand  // 기존: "make"
let runnerName = if isWindows then "test_runner.exe" else "test_runner"
run.StartInfo.FileName <- Path.Combine(genOutputPath, "build", runnerName)
```

---

## ✅ 테스트 결과

### 빌드 검증

```
dotnet build --configuration Release --nologo
→ 0 Warning(s), 0 Error(s) (전체 6개 프로젝트)
```

### 전체 테스트 결과 (29/29 통과)

| 프로젝트 | 통과 | 실패 | 비고 |
|----------|------|------|------|
| Signal.CANdy.Core.Tests | 13 | 0 | 이전 세션 신규 테스트 |
| Generator.Tests | **16** | **0** | **기존 5건 실패 → 0건 실패** |
| **합계** | **29** | **0** | |

### 수정 전후 비교

| 항목 | 수정 전 | 수정 후 |
|------|---------|---------|
| Generator.Tests 통과 | 11/16 | **16/16** |
| 실패 테스트 | 5건 (`Win32Exception: make not found`) | **0건** |

### 통과 확인된 기존 실패 테스트 5건

1. `Encode/Decode roundtrip for SimpleMessage` ✅
2. `Roundtrip with fixed phys_type` ✅
3. `Range check test` ✅
4. `Dispatch direct_map test` ✅
5. `CRC and Counter check test` ✅

---

## ⏭ 다음 계획

이전 세션 보고서(`20260212_1015_C1_C2_Critical_수정.md`)의 다음 계획과 동일:

| ROADMAP 항목 | 설명 |
|-------------|------|
| **H-1c** | `Config.loadFromYaml` / `Config.validate` 테스트 |
| **H-1d** | `Codegen.generate` 테스트 |
| **H-1e** | `Api.generateFromPaths` 엔드투엔드 테스트 |
| **H-1f** | 에지 케이스 테스트 |
| **H-1g** | CI에 Core 테스트 실행 단계 추가 |
| **H-3** | Facade 에러 매핑 정밀화 + 단위 테스트 |

---

> **환경 정보**: mingw32-make 4.4.1 (x86_64-w64-mingw32), gcc 15.2.0 (MSYS2), sh.exe/mkdir.exe from Git for Windows
