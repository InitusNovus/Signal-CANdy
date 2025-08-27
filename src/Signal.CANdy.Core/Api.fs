module Signal.CANdy.Core.Api

open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Config

/// Returns the current library snapshot version. Placeholder until full API is moved.
let version () = "0.2.1-SNAPSHOT"

/// Parse a DBC file into IR. Stub for now.
let parseDbc (_path: string) : Result<Ir, string> =
    Error "NotImplemented: parseDbc"

/// Generate code into outputPath using given config. Stub for now.
let generateCode (_ir: Ir) (_outputPath: string) (_config: Config) : Result<unit, string> =
    Error "NotImplemented: generateCode"

/// Validate configuration object. Stub for now.
let validateConfig (_config: Config) : Result<Config, string> =
    Error "NotImplemented: validateConfig"
