namespace Generator

open System.IO
open Generator.Ir
open Generator.Config
open Generator.Utils
open Generator.Message
open Generator.Registry

module Codegen =

    let generateCode (ir: Ir) (outputPath: string) (config: Generator.Config.Config) =
        try
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

            // Copy example main.c into output to act as test harness
            let exampleMain = Path.Combine("examples", "main.c")
            let outMain = Path.Combine(outputPath, "src", "main.c")
            if File.Exists(exampleMain) then
                File.Copy(exampleMain, outMain, true)

            true

        with
        | ex ->
            eprintfn "Error during code generation: %s" ex.Message
            false