# AGENTS.md — Signal-CANdy

F# (.NET 8) code generator that produces portable C99 parser modules from CAN DBC files.

## Project Layout

```
src/Signal.CANdy.Core/   Core F# library (parsing, IR, config, codegen)
src/Signal.CANdy/        C#-friendly facade (maps Result → exceptions)
src/Signal.CANdy.CLI/    CLI tool (argument parsing, harness generation)
src/Generator/           Legacy standalone generator (Exe)
tests/Signal.CANdy.Core.Tests/  xUnit + FsUnit tests for Core library
tests/Generator.Tests/   xUnit + FsUnit tests (references Generator project)
templates/               Scriban templates for C code generation
examples/                Sample DBC files, YAML configs, and C test runner
Plans/                  Active/archived roadmap documents
```

## Build & Test Commands

Prerequisites: .NET SDK 8.0+. Optional for C validation: gcc, g++, make.

```bash
# Restore & build
dotnet restore
dotnet build --configuration Release --nologo

# Run ALL F# tests
dotnet test --configuration Release -v minimal --nologo

# Run a SINGLE test by display name (backtick-quoted names become display names)
dotnet test --configuration Release --filter "DisplayName~Validation fails for duplicate"

# Run tests in a single test class/module
dotnet test --configuration Release --filter "FullyQualifiedName~DbcValidationTests"

# Generate C code from a DBC (legacy generator)
dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen --config examples/config.yaml

# Generate C code via CLI (with test harness)
dotnet run --project src/Signal.CANdy.CLI -- -d examples/sample.dbc -o gen -t

# Build and run generated C code
make -C gen build
./gen/build/test_runner test_roundtrip

# Format check (Fantomas — used in CI)
dotnet tool install fantomas-tool --global
dotnet fantomas --check

# Pack NuGet locally
dotnet pack -c Release src/Signal.CANdy.Core/Signal.CANdy.Core.fsproj -o artifacts
dotnet pack -c Release src/Signal.CANdy/Signal.CANdy.fsproj -o artifacts
```

## CI Pipeline

Defined in `.github/workflows/ci.yml`. Key jobs:
- **build-test**: restore → build → codegen sanity → `dotnet test` → codegen fixed_suite → build C with Make → C++ compat check → smoke test → NuGet pack
- **lint**: repo hygiene checks

Release workflow (`.github/workflows/release.yml`) triggers on `v*` tags pushed to main.

### Pre-Release Checklist (릴리스 전 필수 확인)

버전 태그를 생성하기 **전에** 아래 항목을 반드시 확인한다. 누락 시 NuGet 패키지에 구버전 문서가 포함되거나 README가 불일치한다.

| # | 확인 항목 | 대상 파일 |
|---|-----------|-----------|
| 1 | `<Version>` 태그가 릴리스 버전과 일치 | `*.fsproj` (Core, Signal.CANdy) |
| 2 | `Api.fs` `version()` 반환값이 릴리스 버전과 일치 | `src/Signal.CANdy.Core/Api.fs` |
| 3 | NuGet install 예시 버전 번호 일치 | `README.md`, `README.NuGet.md` (Core, Facade) |
| 4 | README(EN/KO) 프로젝트 구조·기능 목록 최신 반영 | `README.md`, `README.ko.md` |
| 5 | `dotnet build -c Release` 0 warnings/errors | — |
| 6 | `dotnet test -c Release` 전부 통과 | — |
| 7 | `fantomas --check src/ tests/` 통과 | — |

## Code Style

### Namespaces & Modules

- Core library files: `namespace Signal.CANdy.Core` at top, then `module <Name> =`
- Legacy generator files: `namespace Generator` then `module <Name> =`
- Nested sub-modules within a file are acceptable (e.g., `module Utils =` inside Codegen.fs)
- API facade: `module Signal.CANdy.Core.Api` (combined namespace+module form)

### Naming Conventions

| Element | Convention | Example |
|---------|-----------|---------|
| Types, records, DUs, modules | PascalCase | `Signal`, `ByteOrder`, `ParseError` |
| Record fields | PascalCase | `StartBit`, `IsSigned`, `ValueTable` |
| DU cases | PascalCase | `Little`, `Big`, `InvalidDbc` |
| Functions and values | camelCase | `parseDbcFile`, `validateOverlaps` |
| Private helpers | `let private` + camelCase | `let private coveredBits ...` |
| Test names | Backtick-quoted descriptive | `` let ``Validation fails for X`` () = `` |

### Imports / `open` Statements

Order: System namespaces first, then project-local modules.

```fsharp
open System
open System.IO
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Errors
```

### Formatting

- **Indentation**: 4 spaces. No tabs.
- **Line length**: No hard limit. Long lines are common in codegen string templates; prefer readability elsewhere.
- **Formatter**: Fantomas (CI runs `dotnet fantomas --check`). No repo-level `.editorconfig` or Fantomas config exists yet.

### Types

- Prefer **records** for structured data: `type Signal = { Name: string; StartBit: uint16; ... }`
- Prefer **discriminated unions** for enums and errors: `type ParseError = | InvalidDbc of string | IoError of string`
- **Classes** are used only in the C#-facing facade (`src/Signal.CANdy/Library.fs`)
- Use `option` for nullable/optional fields: `Minimum: float option`
- Use `list` as the default collection type

### Error Handling

- Domain errors are **discriminated unions** defined in `Errors.fs`: `ParseError`, `CodeGenError`, `ValidationError`
- Functions return `Result<T, ErrorDU>` — never throw exceptions in core logic
- The C# facade (`src/Signal.CANdy/Library.fs`) maps `Result` to typed exceptions
- Legacy generator uses an active pattern (`Success`/`Failure`) in `Generator.Result` that wraps `Ok`/`Error`

```fsharp
// Core — return Result
let parseDbcFile (path: string) : Result<Ir, ParseError> = ...

// Facade — map to exceptions for C# consumers
match Api.parseDbc path with
| Ok ir -> ir
| Error e -> raise (SignalCandyParseException(msg))
```

### Function Style

- Use `|>` pipelines for list/seq transformations
- Compose logic via small private helpers (`let private`)
- `match` for control flow and `Option`/`Result` deconstruction
- `Option.defaultValue`, `Option.map`, `Map.tryFind` for safe access
- Mutable locals are acceptable only in imperative parsing code (e.g., line-by-line DBC parsing in `Dbc.fs`)

```fsharp
messages
|> List.groupBy (fun m -> m.Id)
|> List.tryPick (fun (id, ms) ->
    if List.length ms > 1
    then Some (sprintf "Duplicate message ID %u found." id)
    else None)
```

## Testing

- **Framework**: xUnit + FsUnit.Xunit (assertions via `should equal`, `should contain`, etc.)
- **Location**: `tests/Generator.Tests/` — references the legacy `Generator` project
- **Organization**: One module per feature area (`CodegenTests`, `DbcValidationTests`, `ValueTableTests`)
- **Naming**: Use backtick-quoted descriptive names with `[<Fact>]`

```fsharp
[<Fact>]
let ``Validation fails for duplicate message IDs`` () =
    let dbcPath = createTempDbcFile dbcContent
    let result = parseDbcFile dbcPath
    match result with
    | Success _ -> failwith "Expected a failure, but got success."
    | Failure errors ->
        errors |> should equal ["Duplicate message ID 100 found."]
    File.Delete(dbcPath)
```

- Temp DBC files: create with `createTempDbcFile`, clean up with `File.Delete` in test body
- `CodegenTests.fs` contains integration tests that shell out to `dotnet run` and `gcc`

## Key Dependencies

| Package | Version | Used In |
|---------|---------|---------|
| DbcParserLib | 1.7.0 | Core — DBC file parsing |
| YamlDotNet | 16.3.0 | Core — YAML config loading |
| Scriban | 6.2.1 | Core — C code generation templates |
| FsUnit.xUnit | 7.1.1 | Tests — assertion matchers |
| xunit | 2.5.3 | Tests — test framework |
| Microsoft.NET.Test.Sdk | 17.8.0 | Tests — test SDK |

## Things to Avoid

- **Do not throw exceptions** in core library code — return `Result<T, ErrorDU>` instead
- **Do not use `as any`-style casts** or suppress warnings — F# type system should be respected
- **Do not commit generated output** (`gen/` directory is gitignored)
- **Do not commit proprietary DBC files** — `external_test/` is for local testing only
- **Do not modify test framework** — xUnit + FsUnit is the standard; keep assertions idiomatic
- **Mutable state** only in parsing/IO-bound code where it improves clarity

## Evidence & Source-of-Truth

- **Primary evidence**: actual repository source files, tracked workspace files, current directory layout, build/test outputs, and generated reports under `Reports/`
- **Secondary support**: prior reports, `.sisyphus/` planning notes, helper tools, and external references
- Do not let secondary support override direct repository evidence
- If something is not directly supported by source evidence, state that clearly instead of filling the gap with assumptions
- Do not treat absence of evidence as proof that something was removed, deprecated, or intentionally changed

## Local-Only / Historical Boundaries

- `Reports/` is an append-only historical record for completed sessions. Existing reports must not be modified after the session that created them ends
- If a prior report is stale or inaccurate, record the correction in the current session's report instead of rewriting history
- `.sisyphus/` is agent working state, not canonical product source. Use it for planning/recovery context, but verify important claims against actual repo files before acting on them
- `gen/` and `tmp/` are working/output areas. Do not treat them as authoritative implementation sources unless the task is explicitly about generated artifacts or transient validation output
- `external_test/` may contain local validation material; do not treat proprietary/local contents there as safe-to-commit by default

## 작업 보고 및 로그 (Workflow & Reporting)

> ⚠️ **이 섹션의 규칙은 예외 없이 반드시 준수해야 합니다.**
> 보고 누락은 작업 미완료와 동일하게 취급됩니다.

### 규칙 1: 작업 종료 시 필수 보고

**모든 작업 세션이 종료될 때, 반드시 `Reports/` 폴더에 작업 보고서를 작성해야 한다.**
코드 변경이 있었든 분석만 수행했든, 세션에서 수행한 모든 내용을 기록한다.
보고서가 없는 작업 세션은 **완료된 것으로 인정하지 않는다.**

### 규칙 2: 파일 작명 규칙

보고서 파일명은 다음 형식을 **반드시** 따른다:

```
Reports/YYYYMMDD_HHMM_작업내용요약.md
```

- `YYYYMMDD`: 작업 날짜 (예: 20260212)
- `HHMM`: 작업 종료 시각 (24시간제, 예: 1430)
- `작업내용요약`: 핵심 작업을 간결하게 (예: `Dbc_예외삼킴_수정`, `Core_테스트_구축`)

예시: `Reports/20260212_1430_Dbc_예외삼킴_수정.md`

### 규칙 3: 보고서 필수 포함 항목

모든 보고서는 다음 4개 섹션을 **빠짐없이** 포함해야 한다:

| 섹션 | 내용 |
|------|------|
| 📝 **작업 요약** | 이번 세션에서 수행한 작업의 한 줄 요약 및 상세 설명 |
| 🛠 **변경 상세** | 수정/생성/삭제한 파일 목록과 각 변경의 구체적 내용 |
| ✅ **테스트 결과** | `dotnet test` 결과, C 빌드 결과, 수동 검증 내역 등 |
| ⏭ **다음 계획** | 다음 세션에서 착수할 활성 계획(`Plans/ROADMAP.md`) 항목 및 선행 조건 |

### 규칙 4: ROADMAP 업데이트

작업 세션에서 활성 계획 항목을 완료했다면, **해당 세션 내에서 즉시** `Plans/ROADMAP.md`의 체크박스를 `[x]`로 갱신한다.
보고서에도 완료된 ROADMAP 항목 ID를 명시한다 (예: "C-1a, C-1b 완료").

### 규칙 5: 보고서 불변성과 정정 방식

- 기존 `Reports/` 파일은 **불변의 이력**으로 취급한다. 과거 세션이 끝난 뒤에는 수정하지 않는다
- 과거 보고서의 내용이 현재 사실과 어긋나는 것이 확인되면, 원본을 고치지 말고 **현재 세션 보고서**에 정정 사항을 별도 섹션으로 남긴다
- 즉, **이력은 불변 / 현재 진실은 patch-forward** 원칙을 따른다

### 규칙 6: 장기 실행 배치의 RUN_ID 규칙 (선택 적용)

- 기본 규칙은 위의 `Reports/YYYYMMDD_HHMM_작업내용요약.md` 단일 보고서 방식이다
- 다만 작업이 여러 세션에 걸쳐 이어지는 **장기 실행 배치**라면, 필요 시 `RUN_ID`(`yyyymmdd-hhmm`, KST 기준)를 정하고 여러 보고서가 동일한 RUN_ID를 공유하도록 할 수 있다
- 같은 미종료 배치를 이어서 수행하는 경우에는 새 RUN_ID를 임의로 만들지 말고 기존 RUN_ID를 재사용한다
- 이 선택 규칙은 복구성과 추적성을 높이기 위한 보강 규칙이며, 현재 레포의 기본 단일 보고서 관행을 대체하지 않는다

---

> 이 규칙들은 프로젝트의 **추적 가능성(traceability)**과 **재현 가능성(reproducibility)**을 보장하기 위한 것입니다.
> 어떠한 사유로도 생략하지 마십시오.
