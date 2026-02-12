/// Backward-compatibility bridge: re-exports Core types under the Generator namespace
/// so that existing Generator.Tests continue to compile after the Core consolidation (H-2).
namespace Generator

open Signal.CANdy.Core

/// Re-export IR types under Generator.Ir
module Ir =
    type ByteOrder = Ir.ByteOrder
    type Signal = Ir.Signal
    type Message = Ir.Message
    type Ir = Ir.Ir

/// Re-export Config types under Generator.Config
module Config =
    type Config = Config.Config

    /// Legacy adapter: returns Config option (old API).
    /// Delegates to Core's loadFromYaml + validate.
    let loadConfig (configPath: string) : Config option =
        match Config.loadFromYaml configPath with
        | Ok cfg -> Some cfg
        | Error _ -> None

/// Re-export Result active pattern for test backward compatibility
module Result =
    let (|Success|Failure|) (r: Result<_, _>) =
        match r with
        | Ok v -> Success v
        | Error e -> Failure e

/// Backward-compatible Dbc module.
/// Core returns Result<Ir, ParseError>; legacy tests expect Result<Ir, string list>.
module Dbc =

    /// Legacy adapter: wraps Core's parseDbcFile, converting ParseError to string list.
    let parseDbcFile (filePath: string) : Result<Ir.Ir, string list> =
        match Signal.CANdy.Core.Dbc.parseDbcFile filePath with
        | Ok ir -> Ok ir
        | Error pe ->
            let msg =
                match pe with
                | Errors.ParseError.InvalidDbc s -> s
                | Errors.ParseError.IoError s -> sprintf "Error parsing DBC file: %s" s
                | Errors.ParseError.Unknown s -> s
            Error [ msg ]

/// Backward-compatible Codegen module.
/// Core returns Result<GeneratedFiles, CodeGenError>; legacy API returns bool.
module Codegen =

    /// Legacy adapter: wraps Core's generate, returning bool and handling emit-main.
    let generateCode
        (ir: Ir.Ir)
        (outputPath: string)
        (config: Config.Config)
        (emitMain: bool)
        : bool =
        match Signal.CANdy.Core.Codegen.generate ir outputPath config with
        | Ok _ ->
            if emitMain then
                // Replicate legacy emit-main behavior: find examples/main.c and copy
                let tryFindExampleMain () =
                    let candidatesFrom (startDir: string) =
                        seq {
                            let mutable d = startDir
                            for _ in 0 .. 6 do
                                let p = System.IO.Path.Combine(d, "examples", "main.c")
                                if System.IO.File.Exists p then yield p
                                let parent = System.IO.Directory.GetParent(d)
                                if not (isNull parent) then d <- parent.FullName
                        }
                    let bases =
                        [ System.IO.Directory.GetCurrentDirectory()
                          System.AppContext.BaseDirectory ]
                    bases |> Seq.collect candidatesFrom |> Seq.tryHead
                let outMain = System.IO.Path.Combine(outputPath, "src", "main.c")
                match tryFindExampleMain () with
                | Some exampleMain -> System.IO.File.Copy(exampleMain, outMain, true)
                | None -> eprintfn "Warning: examples/main.c not found from working locations; skipping emit-main."
            true
        | Error _ -> false
