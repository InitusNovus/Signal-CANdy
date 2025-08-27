open System
open Signal.CANdy.Core
open Signal.CANdy.Core.Errors

module Cli =
    type Parsed = {
        DbcPath: string option
        OutDir: string option
        ConfigPath: string option
        ShowVersion: bool
        ShowHelp: bool
        Unknown: string list
    }

    let empty: Parsed =
        { DbcPath = None
          OutDir = None
          ConfigPath = None
          ShowVersion = false
          ShowHelp = false
          Unknown = [] }

    let usage () =
        String.concat "\n" [
            "Signal.CANdy.CLI — DBC → C code generator";
            "";
            "Usage:";
            "  signal-candy --dbc <file.dbc> --out <out_dir> [--config <config.yaml>]";
            "  signal-candy --version";
            "  signal-candy --help";
            "";
            "Options:";
            "  --dbc <path>       Path to input DBC file (required)";
            "  --out <dir>        Output directory for generated C files (required)";
            "  --config <path>    Optional YAML config (phys_type, range_check, dispatch, etc.)";
            "  --version          Print library version and exit";
            "  --help, -h         Show this help and exit"
        ]

    let parse (argv: string array): Parsed =
        let rec loop i (st: Parsed): Parsed =
            if i >= argv.Length then st
            else
                match argv.[i] with
                | "--dbc" when i + 1 < argv.Length -> loop (i + 2) { st with DbcPath = Some argv.[i + 1] }
                | "--out" when i + 1 < argv.Length -> loop (i + 2) { st with OutDir = Some argv.[i + 1] }
                | "--config" when i + 1 < argv.Length -> loop (i + 2) { st with ConfigPath = Some argv.[i + 1] }
                | "--version" -> loop (i + 1) { st with ShowVersion = true }
                | "--help" | "-h" -> loop (i + 1) { st with ShowHelp = true }
                | unk -> loop (i + 1) { st with Unknown = st.Unknown @ [ unk ] }
        loop 0 empty

[<EntryPoint>]
let main argv: int =
    let args = Cli.parse argv

    if args.ShowHelp then
        printfn "%s" (Cli.usage ())
        0
    elif args.ShowVersion then
        printfn "%s" (Signal.CANdy.Core.Api.version ())
        0
    elif args.Unknown |> List.isEmpty |> not then
        eprintfn "Unknown arguments: %s" (String.Join(" ", args.Unknown))
        eprintfn "\n%s" (Cli.usage ())
        2
    else
        match args.DbcPath, args.OutDir with
        | Some dbc, Some outDir ->
            try
                let cfgOpt = args.ConfigPath
                let t = Signal.CANdy.Core.Api.generateFromPaths dbc outDir cfgOpt
                let res = t.GetAwaiter().GetResult()
                match res with
                | Ok files ->
                    printfn "Code generation successful."
                    printfn "Headers: %d, Sources: %d, Others: %d" (files.Headers.Length) (files.Sources.Length) (files.Others.Length)
                    0
                | Error err ->
                    let msg =
                        match err with
                        | CodeGenError.TemplateError s -> sprintf "Template error: %s" s
                        | CodeGenError.IoError s -> sprintf "IO error: %s" s
                        | CodeGenError.Unknown s -> sprintf "Error: %s" s
                    eprintfn "%s" msg
                    1
            with ex ->
                eprintfn "Unhandled error: %s" ex.Message
                1
        | _ ->
            eprintfn "Missing required arguments."
            eprintfn "\n%s" (Cli.usage ())
            2
