module Signal.CANdy.Core.Api

open System.Threading.Tasks
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Config
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Dbc
open Signal.CANdy.Core.Codegen

/// Returns the current library snapshot version. Placeholder until full API is moved.
let version () = "0.2.1"

/// Parse a DBC file into IR. Stub for now.
let parseDbc (path: string) : Result<Ir, ParseError> =
    Signal.CANdy.Core.Dbc.parseDbcFile path

/// Validate configuration object. Stub for now.
let validateConfig (config: Config) : Result<Config, ValidationError> =
    Signal.CANdy.Core.Config.validate config

/// Generate code (sync) using IR and Config.
let generateCode (ir: Ir) (outputPath: string) (config: Config) : Result<GeneratedFiles, CodeGenError> =
    Signal.CANdy.Core.Codegen.generate ir outputPath config

/// Generate code (async) using IR and Config.
let generateCodeAsync (ir: Ir) (outputPath: string) (config: Config) : Task<Result<GeneratedFiles, CodeGenError>> = task {
    return generateCode ir outputPath config
}

/// Convenience: parse dbc, load config path (optional), and generate.
let generateFromPaths (dbcPath: string) (outputPath: string) (configPath: string option) : Task<Result<GeneratedFiles, CodeGenError>> = task {
    // Load config (optional path -> YAML; otherwise sensible defaults)
    let configResult : Result<Config, CodeGenError> =
        match configPath with
        | Some p ->
            match Signal.CANdy.Core.Config.loadFromYaml p with
            | Ok cfg -> Ok cfg
            | Error ve -> Error (CodeGenError.Unknown (sprintf "Config error: %A" ve))
        | None ->
            Ok {
                PhysType = "float"
                PhysMode = "double"
                RangeCheck = false
                Dispatch = "binary_search"
                CrcCounterCheck = false
                MotorolaStartBit = "msb"
                FilePrefix = "sc_"
            }

    match configResult with
    | Error e -> return Error e
    | Ok cfg ->
        // Parse DBC
        match Signal.CANdy.Core.Dbc.parseDbcFile dbcPath with
        | Error pe -> return Error (CodeGenError.Unknown (sprintf "Parse error: %A" pe))
        | Ok ir ->
            // Delegate to codegen (currently stubbed)
            return generateCode ir outputPath cfg
}
