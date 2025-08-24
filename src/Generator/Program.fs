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
    }

    [<EntryPoint>]
    let main argv =
        try
            let args = argv |> List.ofArray

            let rec parseArgs (argsList: string list) (currentDbc: string) (currentOut: string) (currentConfig: string option) =
                match argsList with
                | "--dbc" :: path :: rest -> parseArgs rest path currentOut currentConfig
                | "--out" :: path :: rest -> parseArgs rest currentDbc path currentConfig
                | "--config" :: path :: rest -> parseArgs rest currentDbc currentOut (Some path)
                | _ :: rest -> parseArgs rest currentDbc currentOut currentConfig
                | [] -> { DbcPath = currentDbc; OutputPath = currentOut; ConfigPath = currentConfig }

            let parsedArgs = parseArgs args "" "" None

            if parsedArgs.DbcPath = "" || parsedArgs.OutputPath = "" then
                eprintfn "Usage: dotnet run --project src/Generator -- --dbc <dbc_file_path> --out <output_directory> [--config <config_file_path>]"
                1
            else
                printfn "DBC Path: %s" parsedArgs.DbcPath
                printfn "Output Path: %s" parsedArgs.OutputPath
                printfn "Config Path: %A" parsedArgs.ConfigPath

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

                match Dbc.parseDbcFile parsedArgs.DbcPath with
                | Ok ir ->
                    if Codegen.generateCode ir parsedArgs.OutputPath cfg then
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
