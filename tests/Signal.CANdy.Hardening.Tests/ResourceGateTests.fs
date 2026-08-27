namespace Signal.CANdy.Hardening.Tests

open System
open System.IO
open System.Text
open System.Text.Json
open Xunit

module ResourceGateTests =

    let private manifestPath =
        Path.Combine(TestSupport.repoRoot, "hardening", "build-budget.json")

    let private fixturePath name =
        Path.Combine(__SOURCE_DIRECTORY__, "fixtures", name)

    let private propertyMap (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.map (fun property -> property.Name, property.Value.GetInt64())
        |> Map

    let private expectedLimits =
        Map
            [ "imageBytes", 1048576L
              "rxMessages", 4096L
              "rxPrograms", 8192L
              "conversions", 1024L
              "poolSlots", 8192L
              "txMessages", 4096L
              "txPrograms", 8192L
              "txCounters", 4096L
              "nestedMuxRecords", 8192L
              "muxDepth", 4L
              "rxCounters", 4096L
              "coverageSpans", 16384L
              "coverageSpansPerPlan", 2L
              "symbolUtf8Bytes", 255L
              "payloadBytes", 64L
              "freshnessMs", 2147483647L
              "runtimeStateBytesIlp32", 147472L
              "runtimeScratchBytes", 64L ]

    let private expectedRecordBytes =
        Map
            [ "header", 32L
              "directory", 32L
              "directoryEntry", 8L
              "rxMessage", 8L
              "program", 16L
              "conversion", 24L
              "footer", 4L
              "extensionHeader", 40L
              "nestedMux", 36L
              "quality", 4L
              "protectionHeader", 48L
              "protectionPlan", 16L
              "rxCounter", 16L
              "coverageSpan", 4L
              "txHeader", 32L
              "txMessage", 24L
              "txCounter", 24L ]

    let private expectedIlp32 =
        Map
            [ "sc_schema_t", 92L
              "sc_frame_t", 72L
              "sc_slot_t", 16L
              "sc_runtime_state_t", 8L
              "sc_tx_counter_state_t", 12L
              "sc_tx_token_t", 20L
              "sc_activation_descriptor_t", 120L
              "sc_activation_target_t", 60L
              "sc_activation_storage_t", 16L
              "sc_activation_slot_t", 20L
              "sc_activation_token_t", 24L
              "sc_activation_view_t", 24L
              "sc_activation_controller_t", 128L ]

    let private expectedPointer64 =
        Map
            [ "sc_schema_t", 104L
              "sc_frame_t", 72L
              "sc_slot_t", 16L
              "sc_runtime_state_t", 16L
              "sc_tx_counter_state_t", 12L
              "sc_tx_token_t", 32L
              "sc_activation_descriptor_t", 128L
              "sc_activation_target_t", 72L
              "sc_activation_storage_t", 32L
              "sc_activation_slot_t", 40L
              "sc_activation_token_t", 24L
              "sc_activation_view_t", 40L
              "sc_activation_controller_t", 184L ]

    let private expectedCeilings =
        Map
            [ "binaryBytes", 70456L
              "textBytes", 69332L
              "dataBytes", 1116L
              "bssBytes", 49704L
              "flashLoadBytes", 70448L
              "ramBytes", 50820L
              "maxStaticFrameBytes", 512L
              "dynamicStackEntries", 0L ]

    [<Fact>]
    [<Trait("Issue25Gate", "Resources")>]
    let ``build budget manifest is the exact closed design schema`` () =
        let options =
            JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false)

        use document = JsonDocument.Parse(File.ReadAllBytes(manifestPath), options)
        let root = document.RootElement
        Assert.Equal("sc.build-budget/v1", root.GetProperty("format").GetString())

        Assert.Equal<string list>(
            [ "format"; "scimg"; "cRuntimeTypes"; "cc1aActivation" ],
            root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        )

        let scimg = root.GetProperty("scimg")
        Assert.Equal<Map<string, int64>>(expectedLimits, propertyMap (scimg.GetProperty("limits")))
        Assert.Equal<Map<string, int64>>(expectedRecordBytes, propertyMap (scimg.GetProperty("recordBytes")))

        let runtimeTypes = root.GetProperty("cRuntimeTypes")
        Assert.Equal<Map<string, int64>>(expectedIlp32, propertyMap (runtimeTypes.GetProperty("ilp32")))
        Assert.Equal<Map<string, int64>>(expectedPointer64, propertyMap (runtimeTypes.GetProperty("pointer64")))

        let activation = root.GetProperty("cc1aActivation")
        let provenance = activation.GetProperty("provenance")
        Assert.Equal("d65073319446c40819bd8f1e85c09214978fc92f", provenance.GetProperty("siblingCommit").GetString())
        Assert.Equal("arm-none-eabi-gcc 13.3.1", provenance.GetProperty("toolchain").GetString())
        Assert.Equal("0x08008000", provenance.GetProperty("origin").GetString())
        Assert.Equal(94208, provenance.GetProperty("flashCapacityBytes").GetInt32())
        Assert.Equal(131072, provenance.GetProperty("ramCapacityBytes").GetInt32())
        Assert.Equal(1024, provenance.GetProperty("stackReservedBytes").GetInt32())

        let baseline = activation.GetProperty("baseline")
        Assert.Equal(68408, baseline.GetProperty("binaryBytes").GetInt32())

        Assert.Equal(
            "09e4aad7bb88de29d36b83b0fd8351e5e2cb594c453e96c1ae25a1336f329f83",
            baseline.GetProperty("binarySha256").GetString()
        )

        Assert.Equal(67284, baseline.GetProperty("textBytes").GetInt32())
        Assert.Equal(1116, baseline.GetProperty("dataBytes").GetInt32())
        Assert.Equal(49192, baseline.GetProperty("bssBytes").GetInt32())
        Assert.Equal(68400, baseline.GetProperty("flashLoadBytes").GetInt32())
        Assert.Equal(50308, baseline.GetProperty("ramBytes").GetInt32())
        Assert.Equal(1047, baseline.GetProperty("stackUsageEntries").GetInt32())
        Assert.Equal(344, baseline.GetProperty("maxStaticFrameBytes").GetInt32())
        Assert.Equal(0, baseline.GetProperty("dynamicStackEntries").GetInt32())
        Assert.Equal<Map<string, int64>>(expectedCeilings, propertyMap (activation.GetProperty("ceilings")))

    [<Fact>]
    [<Trait("Issue25Gate", "Resources")>]
    let ``CC1A receipt pins exact size hash and static stack evidence`` () =
        use receipt =
            JsonDocument.Parse(File.ReadAllBytes(fixturePath "cc1a-activation-receipt.json"))

        let root = receipt.RootElement
        Assert.Equal("sc.cc1a-build-receipt/v1", root.GetProperty("format").GetString())
        let observed = root.GetProperty("observed")
        Assert.Equal(68408, observed.GetProperty("binaryBytes").GetInt32())

        Assert.Equal(
            "09e4aad7bb88de29d36b83b0fd8351e5e2cb594c453e96c1ae25a1336f329f83",
            observed.GetProperty("binarySha256").GetString()
        )

        Assert.Equal(1047, observed.GetProperty("stackUsageEntries").GetInt32())
        Assert.Equal(344, observed.GetProperty("maxStaticFrameBytes").GetInt32())
        Assert.Equal(0, observed.GetProperty("dynamicStackEntries").GetInt32())

    [<Fact>]
    [<Trait("Issue25Gate", "Resources")>]
    let ``strict verifier tests equality and plus one for every limit ceiling and type`` () =
        let comparisons =
            [ yield!
                  expectedLimits
                  |> Map.toList
                  |> List.map (fun (name, value) -> "scimg.limits." + name, "ceiling", value)
              yield!
                  expectedCeilings
                  |> Map.toList
                  |> List.map (fun (name, value) -> "firmware." + name, "ceiling", value)
              yield!
                  expectedIlp32
                  |> Map.toList
                  |> List.map (fun (name, value) -> "types.ilp32." + name, "exact", value)
              yield!
                  expectedPointer64
                  |> Map.toList
                  |> List.map (fun (name, value) -> "types.pointer64." + name, "exact", value) ]

        Assert.Equal(52, comparisons.Length)

        TestSupport.withTempDirectory (fun temporary ->
            let requestPath = Path.Combine(temporary, "boundary-request.json")

            do
                use stream = File.Create(requestPath)
                use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
                writer.WriteStartObject()
                writer.WriteString("format", "sc.build-budget-boundaries/v1")
                writer.WriteStartArray("comparisons")

                for path, comparison, maximum in comparisons do
                    writer.WriteStartObject()
                    writer.WriteString("path", path)
                    writer.WriteString("comparison", comparison)
                    writer.WriteNumber("equality", maximum)
                    writer.WriteNumber("plusOne", maximum + 1L)
                    writer.WriteEndObject()

                writer.WriteEndArray()
                writer.WriteEndObject()
                writer.Flush()

            let project =
                Path.Combine(TestSupport.repoRoot, "tools", "Signal.CANdy.Hardening", "Signal.CANdy.Hardening.fsproj")

            let receipt = fixturePath "cc1a-activation-receipt.json"

            let result =
                TestSupport.runProcess
                    TestSupport.repoRoot
                    "dotnet"
                    [ "run"
                      "--no-restore"
                      "--project"
                      project
                      "--"
                      "verify-budget"
                      "--manifest"
                      manifestPath
                      "--receipt"
                      receipt
                      "--boundaries"
                      requestPath ]

            Assert.True(result.ExitCode = 0, result.StandardError)

            for path, _, maximum in comparisons do
                Assert.Contains(sprintf "PASS %s observed=%d max=%d" path maximum maximum, result.StandardOutput)

                Assert.Contains(
                    sprintf "SCBUDGET001 %s observed=%d max=%d" path (maximum + 1L) maximum,
                    result.StandardOutput
                )

            Assert.Contains("\"baseline\":", result.StandardOutput)
            Assert.Contains("\"observed\":", result.StandardOutput)
            Assert.Contains("\"delta\":", result.StandardOutput)
            Assert.Contains("\"ceiling\":", result.StandardOutput))

    [<Fact>]
    [<Trait("Issue25Gate", "Resources")>]
    let ``C runtime object has no heap references or mutable static storage`` () =
        let project =
            Path.Combine(TestSupport.repoRoot, "tools", "Signal.CANdy.Hardening", "Signal.CANdy.Hardening.fsproj")

        let result =
            TestSupport.runProcess
                TestSupport.repoRoot
                "dotnet"
                [ "run"
                  "--no-restore"
                  "--project"
                  project
                  "--"
                  "scan-runtime"
                  "--source"
                  "runtime/c99/src/signal_candy_runtime.c" ]

        Assert.True(result.ExitCode = 0, result.StandardError)
        Assert.Contains("heapUndefined=0", result.StandardOutput)
        Assert.Contains("mutableStatic=0", result.StandardOutput)

    [<Fact>]
    [<Trait("Issue25Gate", "Resources")>]
    let ``strict verifier rejects duplicate unknown and malformed manifest fields`` () =
        let project =
            Path.Combine(TestSupport.repoRoot, "tools", "Signal.CANdy.Hardening", "Signal.CANdy.Hardening.fsproj")

        for name, text in
            [ "duplicate", "{\"format\":\"sc.build-budget/v1\",\"format\":\"sc.build-budget/v1\"}"
              "unknown", "{\"format\":\"sc.build-budget/v1\",\"unknown\":0}"
              "malformed", "{\"format\":\"sc.build-budget/v1\"" ] do
            TestSupport.withTempDirectory (fun temporary ->
                let path = Path.Combine(temporary, name + ".json")
                File.WriteAllText(path, text, UTF8Encoding(false))

                let result =
                    TestSupport.runProcess
                        TestSupport.repoRoot
                        "dotnet"
                        [ "run"
                          "--no-restore"
                          "--project"
                          project
                          "--"
                          "verify-budget"
                          "--manifest"
                          path ]

                Assert.NotEqual(0, result.ExitCode)
                Assert.Contains("SCBUDGET002", result.StandardError))
