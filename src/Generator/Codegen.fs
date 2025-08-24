namespace Generator

open System.IO
open Generator.Ir
open Generator.Config
open Generator.Utils
open Generator.Message
open Generator.Registry

module Codegen =

    let generateCode (ir: Ir) (outputPath: string) (config: Config) =
        try
            // Create output directories
            Directory.CreateDirectory (Path.Combine(outputPath, "include")) |> ignore
            Directory.CreateDirectory (Path.Combine(outputPath, "src")) |> ignore

            // Generate utils.h and utils.c
            File.WriteAllText(Path.Combine(outputPath, "include", "utils.h"), Utils.utilsHContent)
            File.WriteAllText(Path.Combine(outputPath, "src", "utils.c"), Utils.utilsCContent)

            // Generate code for each message
            for message in ir.Messages do
                Message.generateMessageFiles message outputPath config

            // Generate registry files
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