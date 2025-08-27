module Signal.CANdy.Core.Api

open System.Threading.Tasks
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Config
open Signal.CANdy.Core.Errors

/// Returns the current library snapshot version. Placeholder until full API is moved.
let version () = "0.2.1-SNAPSHOT"

/// Parse a DBC file into IR. Stub for now.
let parseDbc (_path: string) : Result<Ir, ParseError> =
    Error (ParseError.Unknown "NotImplemented: parseDbc")

/// Validate configuration object. Stub for now.
let validateConfig (_config: Config) : Result<Config, ValidationError> =
    Error (ValidationError.Unknown "NotImplemented: validateConfig")

/// Generate code (sync) using IR and Config.
let generateCode (_ir: Ir) (_outputPath: string) (_config: Config) : Result<GeneratedFiles, CodeGenError> =
    Error (CodeGenError.Unknown "NotImplemented: generateCode")

/// Generate code (async) using IR and Config.
let generateCodeAsync (ir: Ir) (outputPath: string) (config: Config) : Task<Result<GeneratedFiles, CodeGenError>> = task {
    return generateCode ir outputPath config
}

/// Convenience: parse dbc, load config path (optional), and generate.
let generateFromPaths (_dbcPath: string) (_outputPath: string) (_configPath: string option) : Task<Result<GeneratedFiles, CodeGenError>> = task {
    // Placeholder orchestration to be implemented during migration
    return Error (CodeGenError.Unknown "NotImplemented: generateFromPaths")
}
