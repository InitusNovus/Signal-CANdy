namespace Signal.CANdy.Hardening.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Scimg
open Signal.CANdy.Hardening

module CrossOracleTests =

    let private corpusPath name =
        Path.Combine(TestSupport.repoRoot, "tests", "corpus", "scimg", "v1", name)

    let private sha256 (bytes: byte array) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant()

    [<Fact>]
    [<Trait("Issue25Gate", "CrossOracle")>]
    let ``minimized invalid UTF8 symbol is table rejection in both parsers`` () =
        let imagePath = corpusPath "invalid-utf8-symbol.scimg"
        let sidecarPath = corpusPath "invalid-utf8-symbol.json"
        let bytes = File.ReadAllBytes(imagePath)
        use sidecar = JsonDocument.Parse(File.ReadAllBytes(sidecarPath))
        let root = sidecar.RootElement

        Assert.Equal("sc.scimg-regression/v1", root.GetProperty("format").GetString())
        Assert.Equal("invalid-utf8-symbol", root.GetProperty("id").GetString())
        Assert.Equal(128, bytes.Length)
        Assert.Equal(root.GetProperty("sha256").GetString(), sha256 bytes)
        Assert.Equal("reject:table", root.GetProperty("expected").GetProperty("fsharp").GetString())
        Assert.Equal("reject:table", root.GetProperty("expected").GetProperty("c").GetString())

        let sourceCaseId = root.GetProperty("sourceCaseId").GetString()

        let sourcePlan =
            Contract.replay sourceCaseId
            |> Option.defaultWith (fun () -> failwith "source case is not replayable")

        Assert.Equal("sym.name.malformedUtf8", sourcePlan.Target)

        match readDetailed bytes with
        | Error errors -> Assert.Contains(ImageTable, errors)
        | Ok _ -> failwith "F# detailed parser accepted the malformed UTF-8 regression"

        TestSupport.withTempDirectory (fun temporary ->
            let executable =
                Path.Combine(
                    temporary,
                    if OperatingSystem.IsWindows() then
                        "schema_open_harness.exe"
                    else
                        "schema_open_harness"
                )

            let harness =
                Path.Combine(TestSupport.repoRoot, "runtime", "c99", "tests", "schema_open_harness.c")

            let includeDirectory =
                Path.Combine(TestSupport.repoRoot, "runtime", "c99", "include")

            let compile =
                TestSupport.runProcess
                    TestSupport.repoRoot
                    "cc"
                    [ "-std=c99"
                      "-Wall"
                      "-Wextra"
                      "-Werror"
                      "-O2"
                      "-I" + includeDirectory
                      harness
                      "-o"
                      executable ]

            Assert.True(compile.ExitCode = 0, compile.StandardError)

            let result =
                TestSupport.runProcess TestSupport.repoRoot executable [ "--image"; "invalid-utf8-symbol"; imagePath ]

            Assert.True(result.ExitCode = 0, result.StandardError)
            use record = JsonDocument.Parse(result.StandardOutput)
            let oracle = record.RootElement
            Assert.False(oracle.GetProperty("accepted").GetBoolean(), "C oracle JSONL: " + result.StandardOutput)
            Assert.Equal(-7, oracle.GetProperty("status").GetInt32())
            Assert.Equal("table", oracle.GetProperty("family").GetString()))
