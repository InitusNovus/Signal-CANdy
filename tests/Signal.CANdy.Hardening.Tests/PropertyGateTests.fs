namespace Signal.CANdy.Hardening.Tests

open System.IO
open System.Text.Json
open Xunit
open Signal.CANdy.Hardening

module PropertyGateTests =

    [<Fact>]
    [<Trait("Issue25Gate", "Properties")>]
    let ``ten thousand generated cases satisfy readDetailed no-throw canonical properties`` () =
        TestSupport.withTempDirectory (fun temporary ->
            let pack = Path.Combine(temporary, "deterministic.scorp")
            let summary = Path.Combine(temporary, "properties.json")

            let driver =
                Path.Combine(
                    TestSupport.repoRoot,
                    "tools",
                    "Signal.CANdy.Hardening",
                    "bin",
                    "Release",
                    "net8.0",
                    "Signal.CANdy.Hardening.dll"
                )

            let result =
                TestSupport.runProcess
                    TestSupport.repoRoot
                    "dotnet"
                    [ driver
                      "generate"
                      "--seed"
                      sprintf "0x%016X" Contract.RootSeed
                      "--cases"
                      "10000"
                      "--output"
                      pack
                      "--property-summary"
                      summary ]

            Assert.True(result.ExitCode = 0, result.StandardError)
            Assert.True(File.Exists(pack), "deterministic corpus pack was not produced")
            Assert.True(File.Exists(summary), "F# property summary was not produced")

            use document = JsonDocument.Parse(File.ReadAllBytes(summary))
            let root = document.RootElement
            Assert.Equal("sc.hardening-properties/v1", root.GetProperty("format").GetString())
            Assert.Equal(10000, root.GetProperty("cases").GetInt32())
            Assert.Equal(0, root.GetProperty("escapedExceptions").GetInt32())
            Assert.Equal(0, root.GetProperty("nonCanonicalAccepted").GetInt32())
            Assert.Equal(0, root.GetProperty("semanticRoundtripMismatches").GetInt32()))

    [<Fact>]
    [<Trait("Issue25Gate", "Properties")>]
    let ``replay and minimization commands preserve a stable case identity`` () =
        let plan = Contract.cases.[417]

        let driver =
            Path.Combine(
                TestSupport.repoRoot,
                "tools",
                "Signal.CANdy.Hardening",
                "bin",
                "Release",
                "net8.0",
                "Signal.CANdy.Hardening.dll"
            )

        for command in [ "replay"; "minimize" ] do
            let result =
                TestSupport.runProcess TestSupport.repoRoot "dotnet" [ driver; command; "--case-id"; plan.Id ]

            Assert.True(result.ExitCode = 0, result.StandardError)
            Assert.Contains(plan.Id, result.StandardOutput)
