namespace Signal.CANdy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Xunit
open Xunit.Sdk
open Signal.CANdy.Core
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Pool

module P0DifferentialTests =

    let private unwrap description result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "%s failed: %A" description errors

    let private repoRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private fixturePath extension =
        Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "p0_aot_scimg." + extension)

    let private findExecutable () =
        let fixedGcc = @"C:\msys64\ucrt64\bin\gcc.exe"

        if File.Exists(fixedGcc) then
            Some fixedGcc
        else
            let locator, arguments =
                if OperatingSystem.IsWindows() then
                    "where.exe", [ "gcc" ]
                else
                    "which", [ "gcc" ]

            try
                let startInfo = ProcessStartInfo(locator)
                startInfo.UseShellExecute <- false
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                arguments |> List.iter startInfo.ArgumentList.Add
                use childProcess = Process.Start(startInfo)
                let stdoutTask = childProcess.StandardOutput.ReadToEndAsync()

                if not (childProcess.WaitForExit(10_000)) then
                    childProcess.Kill(entireProcessTree = true)
                    childProcess.WaitForExit()
                    None
                else
                    let stdout = stdoutTask.Result

                    if childProcess.ExitCode = 0 then
                        stdout.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.tryHead
                        |> Option.filter File.Exists
                    else
                        None
            with _ ->
                None

    let private runProcess executable arguments =
        let startInfo = ProcessStartInfo(executable)
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        arguments |> List.iter startInfo.ArgumentList.Add
        use childProcess = Process.Start(startInfo)
        let stdoutTask = childProcess.StandardOutput.ReadToEndAsync()
        let stderrTask = childProcess.StandardError.ReadToEndAsync()

        if not (childProcess.WaitForExit(120_000)) then
            childProcess.Kill(entireProcessTree = true)
            childProcess.WaitForExit()
            failwith $"process did not exit within the bounded timeout: {executable}"

        let stdout = stdoutTask.Result
        let stderr = stderrTask.Result
        childProcess.ExitCode, stdout, stderr

    [<Fact>]
    let ``P0 same DBC generated AOT and SCIMG runtime are differentially conformant`` () =
        let gcc =
            match findExecutable () with
            | Some path -> path
            | None -> raise (SkipException.ForSkip("gcc was not found; skipping C99 differential test."))

        let tempDirectory =
            Path.Combine(Path.GetTempPath(), "p0-aot-scimg-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempDirectory) |> ignore

        try
            let ir = Dbc.parseDbcFile (fixturePath "dbc") |> unwrap "DBC parse"
            let wire = Wire.toWireModel ir |> unwrap "Wire normalization"

            let pool =
                File.ReadAllText(fixturePath "pool.json")
                |> Pool.parsePoolDefinition
                |> unwrap "Pool parse"

            let bindings =
                File.ReadAllText(fixturePath "binding.json")
                |> Binding.parseBindingSet
                |> unwrap "Binding parse"

            let linked = Linked.link pool wire bindings |> unwrap "Link"
            let image = Scimg.lower linked |> unwrap "Image lowering"
            let firstImage = Scimg.write image |> unwrap "First image write"
            let secondImage = Scimg.write image |> unwrap "Second image write"
            Assert.Equal<byte>(firstImage, secondImage)

            let config = Config.loadFromYaml (fixturePath "yaml") |> unwrap "Config parse"
            let generatedDirectory = Path.Combine(tempDirectory, "generated")

            let generated =
                Codegen.generate ir generatedDirectory config |> unwrap "AOT generation"

            let imagePath = Path.Combine(tempDirectory, "p0-aot-scimg.scimg")
            let executablePath = Path.Combine(tempDirectory, "p0_diff_harness.exe")
            File.WriteAllBytes(imagePath, firstImage)

            let includePath = Path.Combine(repoRoot, "runtime", "c99", "include")

            let runtimePath =
                Path.Combine(repoRoot, "runtime", "c99", "src", "signal_candy_runtime.c")

            let harnessPath =
                Path.Combine(repoRoot, "runtime", "c99", "tests", "p0_aot_scimg_diff_harness.c")

            let compileArguments =
                [ "-std=c99"
                  "-Wall"
                  "-Wextra"
                  "-Werror"
                  "-O2"
                  "-I" + includePath
                  "-I" + Path.Combine(generatedDirectory, "include")
                  runtimePath ]
                @ generated.Sources
                @ [ harnessPath; "-lm"; "-o"; executablePath ]

            let compileExit, compileStdout, compileStderr = runProcess gcc compileArguments

            Assert.True(
                (compileExit = 0),
                sprintf
                    "gcc failed with exit code %d.\nstdout:\n%s\nstderr:\n%s"
                    compileExit
                    compileStdout
                    compileStderr
            )

            let runExit, runStdout, runStderr = runProcess executablePath [ imagePath ]

            Assert.True(
                (runExit = 0),
                sprintf
                    "Differential harness failed with exit code %d.\nstdout:\n%s\nstderr:\n%s"
                    runExit
                    runStdout
                    runStderr
            )

            [ "PASS classic-fd"
              "PASS le-motorola"
              "PASS signed-scaled"
              "PASS mux"
              "PASS crc-counter"
              "PASS counter-transmitted-0-1"
              "ALL PASS" ]
            |> List.iter (fun marker -> Assert.Contains(marker, runStdout))
        finally
            if Directory.Exists(tempDirectory) then
                Directory.Delete(tempDirectory, true)
