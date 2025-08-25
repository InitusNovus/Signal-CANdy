namespace Generator

open System.IO
open Generator.Ir
open Generator.Config
open Generator.Utils
open Generator.Message
open Generator.Registry

module Codegen =

    let generateCode (ir: Ir) (outputPath: string) (config: Generator.Config.Config) (emitMain: bool) =
        try
            // Helper: find examples/main.c starting from likely roots (CWD, assembly base) and traversing upwards
            let tryFindExampleMain () =
                let candidatesFrom (startDir: string) =
                    seq {
                        let mutable d = startDir
                        for _ in 0 .. 6 do
                            let p = Path.Combine(d, "examples", "main.c")
                            if File.Exists p then yield p
                            let parent = Directory.GetParent(d)
                            if isNull parent then () else d <- parent.FullName
                    }
                let bases = [ Directory.GetCurrentDirectory(); System.AppContext.BaseDirectory ]
                bases
                |> Seq.collect candidatesFrom
                |> Seq.tryHead

            // Create output directories
            Directory.CreateDirectory (Path.Combine(outputPath, "include")) |> ignore
            Directory.CreateDirectory (Path.Combine(outputPath, "src")) |> ignore

            // Clean up legacy, unprefixed common files to prevent duplicate symbols in C builds
            let legacyHeaders = [ "utils.h"; "registry.h" ]
            let legacySources = [ "utils.c"; "registry.c" ]
            for h in legacyHeaders do
                let p = Path.Combine(outputPath, "include", h)
                if File.Exists p then
                    try File.Delete p with _ -> ()
            for s in legacySources do
                let p = Path.Combine(outputPath, "src", s)
                if File.Exists p then
                    try File.Delete p with _ -> ()

            // Generate utils.h and utils.c with prefix
            let uH = Utils.utilsHeaderName config
            let uC = Utils.utilsSourceName config
            File.WriteAllText(Path.Combine(outputPath, "include", uH), Utils.utilsHContent config)
            File.WriteAllText(Path.Combine(outputPath, "src", uC), Utils.utilsCContent config)

            // Generate code for each message
            for message in ir.Messages do
                Message.generateMessageFiles message outputPath config

            // Generate registry files with prefix
            Registry.generateRegistryFiles ir outputPath config

            // Copy example main.c into output to act as test harness (optional)
            if emitMain then
                let outMain = Path.Combine(outputPath, "src", "main.c")
                match tryFindExampleMain () with
                | Some exampleMain -> File.Copy(exampleMain, outMain, true)
                | None -> eprintfn "Warning: examples/main.c not found from working locations; skipping emit-main."

            true

        with
        | ex ->
            eprintfn "Error during code generation: %s" ex.Message
            false