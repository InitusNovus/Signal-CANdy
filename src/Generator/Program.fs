namespace Generator

open System.IO
open Generator.Config
open Generator.Ir
open Generator.Dbc
open Generator.Codegen

module Program =
    type CliArguments = {
        DbcPath: string
        OutputPath: string
        ConfigPath: string option
        Prefix: string option
        EmitMain: bool
    }

    [<EntryPoint>]
    let main argv =
        try
            let args = argv |> List.ofArray

            let rec parseArgs (argsList: string list) (currentDbc: string) (currentOut: string) (currentConfig: string option) (currentPrefix: string option) (currentEmitMain: bool) =
                match argsList with
                | "--dbc" :: path :: rest -> parseArgs rest path currentOut currentConfig currentPrefix currentEmitMain
                | "--out" :: path :: rest -> parseArgs rest currentDbc path currentConfig currentPrefix currentEmitMain
                | "--config" :: path :: rest -> parseArgs rest currentDbc currentOut (Some path) currentPrefix currentEmitMain
                | "--prefix" :: pfx :: rest -> parseArgs rest currentDbc currentOut currentConfig (Some pfx) currentEmitMain
                | "--emit-main" :: flag :: rest ->
                    let v =
                        match flag.ToLowerInvariant() with
                        | "true" | "1" | "yes" | "y" -> true
                        | "false" | "0" | "no" | "n" -> false
                        | _ -> true
                    parseArgs rest currentDbc currentOut currentConfig currentPrefix v
                | _ :: rest -> parseArgs rest currentDbc currentOut currentConfig currentPrefix currentEmitMain
                | [] -> { DbcPath = currentDbc; OutputPath = currentOut; ConfigPath = currentConfig; Prefix = currentPrefix; EmitMain = currentEmitMain }

            let parsedArgs = parseArgs args "" "" None None true

            if parsedArgs.DbcPath = "" || parsedArgs.OutputPath = "" then
                eprintfn "Usage: dotnet run --project src/Generator -- --dbc <dbc_file_path> --out <output_directory> [--config <config_file_path>] [--prefix <file_prefix>] [--emit-main <true|false>]"
                1
            else
                printfn "DBC Path: %s" parsedArgs.DbcPath
                printfn "Output Path: %s" parsedArgs.OutputPath
                printfn "Config Path: %A" parsedArgs.ConfigPath
                printfn "Prefix Override: %A" parsedArgs.Prefix
                printfn "Emit Main: %b" parsedArgs.EmitMain

                // Load config if provided, otherwise fall back to defaults
                let defaultCfg = { PhysType = "float"; PhysMode = "double"; RangeCheck = false; Dispatch = "binary_search"; CrcCounterCheck = false; MotorolaStartBit = "msb"; FilePrefix = "sc_" }
                let cfg =
                    match parsedArgs.ConfigPath with
                    | Some path -> 
                        match Config.loadConfig path with
                        | Some c -> c
                        | None -> 
                            eprintfn "Warning: Failed to load config, falling back to defaults."
                            defaultCfg
                    | None -> defaultCfg

                // Apply CLI overrides
                let cfg =
                    match parsedArgs.Prefix with
                    | Some pfx -> { cfg with FilePrefix = pfx }
                    | None -> cfg

                match Dbc.parseDbcFile parsedArgs.DbcPath with
                | Ok ir ->
                    if Codegen.generateCode ir parsedArgs.OutputPath cfg parsedArgs.EmitMain then
                        printfn "Code generation successful."
                        0
                    else
                        eprintfn "Code generation failed."
                        1
                | Error errors ->
                    eprintfn "Failed to process DBC file:"
                    for e in errors do eprintfn "- %s" e
                    1
        with
        | ex ->
            eprintfn "An unexpected error occurred: %s" ex.Message
            1 // failure
