# Signal.CANdy.Core

Core library for Signal.CANdy: parse DBC files, validate config, and generate C99 encode/decode code.

- Repo: https://github.com/InitusNovus/Signal-CANdy
- License: MIT

## Install

```
dotnet add package Signal.CANdy.Core --version 0.2.1
```

## Quick start (F#)

```fsharp
open Signal.CANdy.Core

let dbcPath = "examples/sample.dbc"
let outDir  = "gen"

match Api.parseDbc dbcPath with
| Ok ir ->
    match Api.generateCode(ir, outDir, Config.defaults) with
    | Ok files -> printfn "Generated: headers=%d sources=%d others=%d" (List.length files.Headers) (List.length files.Sources) (List.length files.Others)
    | Error e  -> printfn "CodeGen error: %A" e
| Error e -> printfn "Parse error: %A" e
```

Or use the higher-level path-based API (loads optional YAML config):

```fsharp
open System.Threading.Tasks
open Signal.CANdy.Core

let run () : Task = task {
    let! result = Api.generateFromPaths("examples/sample.dbc", "gen", None)
    match result with
    | Ok files -> printfn "OK: %A" files
    | Error e  -> printfn "Error: %A" e
}
```

## What's inside
- DBC parsing with validations (duplicate IDs, overlaps, DLC bounds)
- YAML config loader/validator (YamlDotNet)
- C99 codegen (headers/sources + registry/utils)
- Result-based API with discriminated union errors
