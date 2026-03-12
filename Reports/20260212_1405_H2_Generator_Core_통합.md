# 작업 보고서 — H-2 Generator → Core 통합 (코드 중복 제거) + M-1 미사용 의존성 제거

**일시**: 2026-02-12 14:15 (KST)
**브랜치**: `test_n_analysis`
**ROADMAP 항목**: H-2a, H-2b, H-2c, H-2d, H-2e, H-2f, M-1a~M-1e 완료

---

## 📝 작업 요약

Generator 프로젝트의 7개 중복 모듈(Config, Ir, Dbc, Codegen, Codegen.Utils, Codegen.Message, Codegen.Registry)과 Result.fs를 제거하고, Core 프로젝트로의 완전 통합을 달성했다.

**핵심 전략**: `Compat.fs` 역호환 브리지 모듈을 도입하여, Generator.Tests의 16개 테스트를 **단 한 줄도 수정하지 않고** 모두 통과시켰다. 이 브리지는 `Generator.Ir`, `Generator.Config`, `Generator.Dbc`, `Generator.Codegen`, `Generator.Result` 네임스페이스를 Core 타입으로 포워딩한다.

추가로, Core의 `Dbc.fs`에 멀티플렉서 구조 검증(`validateMuxStructure`)이 누락되어 있었음을 발견하고 즉시 보완했다. 이 검증은 (1) 메시지당 M 시그널 1개 제한, (2) m<k> 시그널의 switch value 필수 확인을 수행한다.

---

## 🛠 변경 상세

### 신규 생성
| 파일 | 설명 |
|------|------|
| `src/Generator/Compat.fs` (83줄) | 역호환 브리지 — Generator 네임스페이스의 Ir/Config/Dbc/Codegen/Result 모듈을 Core로 포워딩 |

### 수정
| 파일 | 변경 내용 |
|------|-----------|
| `src/Generator/Generator.fsproj` | 7개 Compile 항목 제거, 5개 PackageReference 제거 (Scriban, Argu, FSharp.SystemTextJson, FsToolkit.ErrorHandling, DbcParserLib, YamlDotNet), Core ProjectReference 추가. Compile 항목: `Compat.fs` → `Program.fs` |
| `src/Generator/Program.fs` | Core API 직접 사용으로 전면 리라이트 — `Signal.CANdy.Core.Config`, `Signal.CANdy.Core.Dbc`, `Signal.CANdy.Core.Codegen`, `Signal.CANdy.Core.Errors` open |
| `src/Signal.CANdy.Core/Dbc.fs` | `validateMuxStructure` 추가 (멀티플렉서 구조 검증) — 기존 combineValidators 체인에 삽입 |
| `ROADMAP.md` | H-2a~H-2f, M-1a~M-1e 체크박스 완료 처리 |

### 삭제 (디스크에서 제거)
| 파일 | 이유 |
|------|------|
| `src/Generator/Config.fs` | Core `Config.fs`와 중복 |
| `src/Generator/Ir.fs` | Core `Ir.fs`와 중복 |
| `src/Generator/Dbc.fs` | Core `Dbc.fs`와 중복 |
| `src/Generator/Codegen.fs` | Core `Codegen.fs`와 중복 |
| `src/Generator/Codegen.Utils.fs` | Core `Codegen.fs`에 포함 |
| `src/Generator/Codegen.Message.fs` | Core `Codegen.fs`에 포함 |
| `src/Generator/Codegen.Registry.fs` | Core `Codegen.fs`에 포함 |
| `src/Generator/Result.fs` | `Compat.fs`로 대체 |

### Compat.fs 타입 매핑 요약

| Legacy API | Core API | 변환 |
|-----------|---------|------|
| `Generator.Dbc.parseDbcFile: string -> Result<Ir, string list>` | `Core.Dbc.parseDbcFile: string -> Result<Ir, ParseError>` | ParseError → string list 변환 |
| `Generator.Codegen.generateCode: Ir -> string -> Config -> bool -> bool` | `Core.Codegen.generate: Ir -> string -> Config -> Result<GeneratedFiles, CodeGenError>` | Result → bool + emit-main 로직 |
| `Generator.Config.loadConfig: string -> Config option` | `Core.Config.loadFromYaml: string -> Result<Config, ValidationError>` | Result → option 변환 |
| `Generator.Result.(|Success|Failure|)` | F# 표준 `Result<_,_>` | Active pattern 유지 |

---

## ✅ 테스트 결과

### dotnet test (Release)
```
Signal.CANdy.Core.Tests: 47/47 통과 (0 실패)
Generator.Tests:         16/16 통과 (0 실패)
합계:                    63/63 통과
```

### E2E 검증
1. **Generator CLI**: `dotnet run --project src/Generator -- --dbc examples/sample.dbc --out gen --config examples/config.yaml` → `Code generation successful.`
2. **Signal.CANdy.CLI**: `dotnet run --project src/Signal.CANdy.CLI -- -d examples/sample.dbc -o gen -c examples/config.yaml -t` → `Code generation successful. Headers: 5, Sources: 3`
3. **gcc 빌드**: `mingw32-make -C gen build` → 경고 0, 오류 0
4. **Roundtrip 테스트**: `./gen/build/test_runner.exe test_roundtrip` → `Roundtrip successful!`

### 발견 및 수정한 버그
- **Core Dbc.fs에 `validateMuxStructure` 누락**: Generator의 레거시 Dbc.fs에는 있었으나 Core에 포팅되지 않았던 멀티플렉서 구조 검증. 이로 인해 Compat 브리지를 통한 2개 테스트가 실패. Core에 동일 로직 추가하여 해결.

---

## ⏭ 다음 계획

### 다음 착수 대상: M-2 (Dead Code 삭제)
- **M-2a**: `Ir.fs` — `SignalType` DU 제거 (미사용)
- **M-2b**: `Core/Library.fs` — 플레이스홀더 파일 점검
- **M-2c**: `templates/` 디렉토리 — 비어 있거나 미사용이면 제거
- **M-2d**: 빌드 및 테스트 통과 확인

### 그 이후
- M-3 (코드 생성 문자열 가독성 개선)
- M-4 (AGENTS.md Key Dependencies 갱신)
- L-* (미래 기능)
