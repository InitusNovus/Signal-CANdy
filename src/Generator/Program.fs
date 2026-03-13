namespace Generator

open System.IO
open Signal.CANdy.Core
open Signal.CANdy.Core.Config
open Signal.CANdy.Core.Errors

module Program =
    type CliArguments =
        { DbcPath: string
          OutputPath: string
          ConfigPath: string option
          Prefix: string option
          EmitMain: bool }

    /// Find examples/main.c by traversing upwards from CWD and assembly base.
    let private tryFindExampleMain () =
        let candidatesFrom (startDir: string) =
            seq {
                let mutable d = startDir

                for _ in 0..6 do
                    let p = Path.Combine(d, "examples", "main.c")

                    if File.Exists p then
                        yield p

                    let parent = Directory.GetParent(d)

                    if not (isNull parent) then
                        d <- parent.FullName
            }

        let bases = [ Directory.GetCurrentDirectory(); System.AppContext.BaseDirectory ]
        bases |> Seq.collect candidatesFrom |> Seq.tryHead

    [<EntryPoint>]
    let main argv =
        try
            let args = argv |> List.ofArray

            let rec parseArgs
                (argsList: string list)
                (currentDbc: string)
                (currentOut: string)
                (currentConfig: string option)
                (currentPrefix: string option)
                (currentEmitMain: bool)
                =
                match argsList with
                | "--dbc" :: path :: rest -> parseArgs rest path currentOut currentConfig currentPrefix currentEmitMain
                | "--out" :: path :: rest -> parseArgs rest currentDbc path currentConfig currentPrefix currentEmitMain
                | "--config" :: path :: rest ->
                    parseArgs rest currentDbc currentOut (Some path) currentPrefix currentEmitMain
                | "--prefix" :: pfx :: rest ->
                    parseArgs rest currentDbc currentOut currentConfig (Some pfx) currentEmitMain
                | "--emit-main" :: flag :: rest ->
                    let v =
                        match flag.ToLowerInvariant() with
                        | "true"
                        | "1"
                        | "yes"
                        | "y" -> true
                        | "false"
                        | "0"
                        | "no"
                        | "n" -> false
                        | _ -> true

                    parseArgs rest currentDbc currentOut currentConfig currentPrefix v
                | _ :: rest -> parseArgs rest currentDbc currentOut currentConfig currentPrefix currentEmitMain
                | [] ->
                    { DbcPath = currentDbc
                      OutputPath = currentOut
                      ConfigPath = currentConfig
                      Prefix = currentPrefix
                      EmitMain = currentEmitMain }

            let parsedArgs = parseArgs args "" "" None None true

            if parsedArgs.DbcPath = "" || parsedArgs.OutputPath = "" then
                eprintfn "Signal CANdy v0.3.2 - DBC to C Code Generator"
                eprintfn "Generate C99 parser modules from DBC files with C++ compatibility"
                eprintfn ""
                eprintfn "USAGE:"
                eprintfn "  dotnet run --project src/Generator -- --dbc <dbc_file> --out <output_dir> [OPTIONS]"
                eprintfn ""
                eprintfn "REQUIRED:"
                eprintfn "  --dbc <file>      DBC file to parse"
                eprintfn "  --out <dir>       Output directory for generated C files"
                eprintfn ""
                eprintfn "OPTIONS:"
                eprintfn "  --config <file>   YAML config file (default: built-in defaults)"
                eprintfn "  --prefix <str>    File prefix for common files (overrides config)"
                eprintfn "  --emit-main <bool> Copy examples/main.c to output (default: true)"
                eprintfn ""
                eprintfn "EXAMPLES:"
                eprintfn "  dotnet run --project src/Generator -- --dbc sample.dbc --out gen"
                eprintfn "  dotnet run --project src/Generator -- --dbc sample.dbc --out gen --config config.yaml"
                eprintfn "  dotnet run --project src/Generator -- --dbc sample.dbc --out gen --prefix my_"
                eprintfn ""
                0
            else
                printfn "DBC Path: %s" parsedArgs.DbcPath
                printfn "Output Path: %s" parsedArgs.OutputPath
                printfn "Config Path: %A" parsedArgs.ConfigPath
                printfn "Prefix Override: %A" parsedArgs.Prefix
                printfn "Emit Main: %b" parsedArgs.EmitMain

                // Load config via Core API
                let defaultCfg: Config =
                    { PhysType = "float"
                      PhysMode = "double"
                      RangeCheck = false
                      Dispatch = "binary_search"
                      CrcCounterCheck = false
                      MotorolaStartBit = "msb"
                      FilePrefix = "sc_"
                      CrcCounter = None }

                let cfg =
                    match parsedArgs.ConfigPath with
                    | Some path ->
                        match Config.loadFromYaml path with
                        | Ok c -> c
                        | Error ve ->
                            let msg =
                                match ve with
                                | ValidationError.InvalidValue s -> s
                                | ValidationError.MissingField s -> s
                                | ValidationError.IoError s -> s
                                | ValidationError.Unknown s -> s
                                | ValidationError.ByteRangeExceedsDlc(msg, rangeEnd, dlc) ->
                                    sprintf "byte_range end %d exceeds DLC %d in message '%s'." rangeEnd dlc msg
                                | ValidationError.UnknownAlgorithm name ->
                                    sprintf "unknown CRC/algorithm '%s' referenced in config." name
                                | ValidationError.SignalNotFound(messageName, signalName) ->
                                    sprintf "signal '%s' not found in message '%s'." signalName messageName
                                | ValidationError.InvalidModulus(messageName, modulus) ->
                                    sprintf "invalid counter modulus %d in message '%s' (must be >= 2)." modulus messageName
                                | ValidationError.ConfigConflict reason ->
                                    sprintf "configuration conflict: %s" reason
                                | ValidationError.CrcWidthMismatch(messageName, crcWidth, signalBits) ->
                                    sprintf "CRC width mismatch for message '%s': crc width %d vs signal bits %d." messageName crcWidth signalBits
                                | ValidationError.MessageNotFound name ->
                                    sprintf "message '%s' not found in configuration." name

                            eprintfn "Warning: Failed to load config: %s. Falling back to defaults." msg
                            defaultCfg
                    | None -> defaultCfg

                // Apply CLI overrides
                let cfg =
                    match parsedArgs.Prefix with
                    | Some pfx -> { cfg with FilePrefix = pfx }
                    | None -> cfg

                // Parse DBC via Core
                match Signal.CANdy.Core.Dbc.parseDbcFile parsedArgs.DbcPath with
                | Ok ir ->
                    // Generate via Core
                    match Signal.CANdy.Core.Codegen.generate ir parsedArgs.OutputPath cfg with
                    | Ok _ ->
                        // Emit main.c if requested
                        if parsedArgs.EmitMain then
                            let outMain = Path.Combine(parsedArgs.OutputPath, "src", "main.c")

                            match tryFindExampleMain () with
                            | Some exampleMain -> File.Copy(exampleMain, outMain, true)
                            | None ->
                                eprintfn
                                    "Warning: examples/main.c not found from working locations; skipping emit-main."

                        printfn "Code generation successful."
                        0
                    | Error ce ->
                        let msg =
                            match ce with
                            | CodeGenError.TemplateError s -> s
                            | CodeGenError.IoError s -> s
                            | CodeGenError.Unknown s -> s
                            | CodeGenError.UnsupportedFeature s -> s

                        eprintfn "Code generation failed: %s" msg
                        1
                | Error pe ->
                    let msg =
                        match pe with
                        | ParseError.InvalidDbc s -> s
                        | ParseError.IoError s -> s
                        | ParseError.Unknown s -> s

                    eprintfn "Failed to process DBC file: %s" msg
                    1
        with ex ->
            eprintfn "An unexpected error occurred: %s" ex.Message
            1
