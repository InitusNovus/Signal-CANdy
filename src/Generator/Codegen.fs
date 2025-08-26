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

            // Clean up stale prefixed common files to prevent duplicate symbols in C builds
            // Keep only the variants matching the current FilePrefix; remove others.
            let keepUtilsH = Utils.utilsHeaderName config
            let keepUtilsC = Utils.utilsSourceName config
            let keepRegH = sprintf "%sregistry.h" config.FilePrefix
            let keepRegC = sprintf "%sregistry.c" config.FilePrefix

            let includeDir = Path.Combine(outputPath, "include")
            let srcDir = Path.Combine(outputPath, "src")

            if Directory.Exists includeDir then
                Directory.GetFiles(includeDir, "*utils.h")
                |> Array.iter (fun f -> if Path.GetFileName(f) <> keepUtilsH then try File.Delete f with _ -> ())
                Directory.GetFiles(includeDir, "*registry.h")
                |> Array.iter (fun f -> if Path.GetFileName(f) <> keepRegH then try File.Delete f with _ -> ())

            if Directory.Exists srcDir then
                Directory.GetFiles(srcDir, "*utils.c")
                |> Array.iter (fun f -> if Path.GetFileName(f) <> keepUtilsC then try File.Delete f with _ -> ())
                Directory.GetFiles(srcDir, "*registry.c")
                |> Array.iter (fun f -> if Path.GetFileName(f) <> keepRegC then try File.Delete f with _ -> ())

            // Generate utils.h and utils.c with prefix
            let uH = Utils.utilsHeaderName config
            let uC = Utils.utilsSourceName config
            File.WriteAllText(Path.Combine(outputPath, "include", uH), Utils.utilsHContent config)
            File.WriteAllText(Path.Combine(outputPath, "src", uC), Utils.utilsCContent config)

            // Emit compatibility shims for legacy includes (utils.h, registry.h)
            let shimHeader (name: string) (target: string) =
                let guard = (name.Replace('.', '_') + "_SHIM").ToUpperInvariant()
                "#ifndef " + guard + "\n#define " + guard + "\n\n"
                + "#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
                + "#include \"" + target + "\"\n\n"
                + "#ifdef __cplusplus\n}\n#endif\n\n"
                + "#endif // " + guard
            File.WriteAllText(Path.Combine(outputPath, "include", "utils.h"), shimHeader "utils.h" uH)
            File.WriteAllText(Path.Combine(outputPath, "include", "registry.h"), shimHeader "registry.h" keepRegH)

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