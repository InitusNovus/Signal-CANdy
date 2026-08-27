namespace Signal.CANdy.Core.Tests

open System
open System.IO
open System.Security.Cryptography
open Xunit
open Signal.CANdy.Core
open Signal.CANdy.Core.RuntimeBuild
open Signal.CANdy.Core.Scimg

module Issue24ScimgDetailedTests =

    let private repoRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private activationPath name =
        Path.Combine(repoRoot, "examples", "scimg_activation_demo", name)

    let private frozenImage name =
        File.ReadAllBytes(activationPath (Path.Combine("build", name)))

    let private unwrap description result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "%s failed: %A" description errors

    let private sha256 (bytes: byte array) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private range startExclusive endExclusive : ImageRange =
        { Start = startExclusive
          End = endExclusive }

    let private assertRange expectedStart expectedEnd (actual: ImageRange) =
        Assert.Equal(range expectedStart expectedEnd, actual)

    let private compileActivation suffix =
        let dbc = activationPath (sprintf "schema_%s.dbc" suffix)
        let poolPath = activationPath "pool.json"
        let bindingPath = activationPath (sprintf "binding_%s.json" suffix)
        let ir = Dbc.parseDbcFile dbc |> unwrap "DBC parse"
        let wire = Wire.toWireModel ir |> unwrap "wire normalize"

        let pool =
            File.ReadAllText(poolPath) |> Pool.parsePoolDefinition |> unwrap "pool parse"

        let bindings =
            File.ReadAllText(bindingPath)
            |> Binding.parseBindingSet
            |> unwrap "binding parse"

        compile
            { Pool = pool
              Wires = [ "schema-" + suffix, wire ]
              Bindings = bindings }
        |> unwrap "compile"

    [<Fact>]
    let ``Detailed lowering writing and reading preserve the compatibility values and bytes`` () =
        let compiled = compileActivation "a"
        Assert.True(compiled.MapDocument.IsNone)
        Assert.True(compiled.MapJson.IsNone)

        let lowered, loweringTrace =
            lowerDetailed compiled.Linked |> unwrap "detailed lower"

        Assert.Equal(compiled.Image, lowered)
        Assert.NotNull(loweringTrace)

        let bytes, writeLayout = writeDetailed lowered |> unwrap "detailed write"
        Assert.Equal<byte>(compiled.ImageBytes, bytes)

        let validated = readDetailed bytes |> unwrap "detailed read"
        Assert.Equal(lowered, validated.Image)
        Assert.Equal(writeLayout, validated.Layout)
        Assert.Equal<byte>(bytes, write validated.Image |> unwrap "compatibility write")
        Assert.Equal(validated.Image, read bytes |> unwrap "compatibility read")

    [<Fact>]
    let ``A and B detailed layouts expose every exact absolute half-open table range`` () =
        for name in [ "schema_a.scimg"; "schema_b.scimg" ] do
            let validated: ValidatedImage = frozenImage name |> readDetailed |> unwrap name
            let regions = validated.Layout.Regions

            assertRange 0u 32u regions.Header
            assertRange 32u 64u regions.Directory
            assertRange 64u 72u regions.RxMessages
            assertRange 72u 88u regions.RxPrograms
            assertRange 88u 112u regions.Conversions
            assertRange 112u 164u regions.Symbols
            assertRange 164u 204u regions.ExtensionHeader.Value
            assertRange 204u 204u regions.NestedMuxRecords.Value
            assertRange 204u 216u regions.QualityEntries.Value
            assertRange 216u 264u regions.ProtectionHeader.Value
            assertRange 264u 280u regions.RxProtectionPlans.Value
            assertRange 280u 296u regions.TxProtectionPlans.Value
            assertRange 296u 312u regions.RxCounters.Value
            assertRange 312u 320u regions.CoverageSpans.Value
            assertRange 320u 352u regions.TxHeader.Value
            assertRange 352u 376u regions.TxMessages.Value
            assertRange 376u 408u regions.TxPrograms.Value
            assertRange 408u 432u regions.TxCounters.Value
            assertRange 432u 440u regions.TxTemplates.Value
            assertRange 440u 444u regions.Footer

    [<Fact>]
    let ``A and B detailed layouts expose every exact record range`` () =
        for name in [ "schema_a.scimg"; "schema_b.scimg" ] do
            let layout = (frozenImage name |> readDetailed |> unwrap name).Layout
            Assert.Equal<ImageRange list>([ range 64u 72u ], layout.RxMessageRanges)
            Assert.Equal<ImageRange list>([ range 72u 88u ], layout.RxProgramRanges)
            Assert.Equal<ImageRange list>([ range 88u 112u ], layout.ConversionRanges)
            Assert.Equal<ImageRange list>([], layout.NestedMuxRecordRanges)

            Assert.Equal<ImageRange list>(
                [ range 204u 208u; range 208u 212u; range 212u 216u ],
                layout.QualityEntryRanges
            )

            Assert.Equal<ImageRange list>([ range 264u 280u ], layout.RxProtectionPlanRanges)
            Assert.Equal<ImageRange list>([ range 280u 296u ], layout.TxProtectionPlanRanges)
            Assert.Equal<ImageRange list>([ range 296u 312u ], layout.RxCounterRanges)
            Assert.Equal<ImageRange list>([ range 312u 316u; range 316u 320u ], layout.CoverageSpanRanges)
            Assert.Equal<ImageRange list>([ range 352u 376u ], layout.TxMessageRanges)
            Assert.Equal<ImageRange list>([ range 376u 392u; range 392u 408u ], layout.TxProgramRanges)
            Assert.Equal<ImageRange list>([ range 408u 432u ], layout.TxCounterRanges)
            Assert.Equal<ImageRange list>([ range 432u 440u ], layout.TxTemplateRanges)

    [<Fact>]
    let ``Detailed reader rejects malformed layout before returning a validated image`` () =
        let original = frozenImage "schema_a.scimg"

        [ 12, 0uy // total size
          28, 0uy // extension/container size
          32, 0uy // RX message table offset
          40, 0uy // RX program table offset
          48, 0uy // conversion table offset
          56, 0uy // symbol table offset
          164, 0uy // extension magic
          176, 0uy // nested table relative offset
          180, 0uy // quality table relative offset
          184, 0uy // TX relative offset
          192, 0uy // protection relative offset
          228, 0uy // RX protection relative offset
          232, 0uy // TX protection relative offset
          236, 0uy // RX counter relative offset
          240, 0uy // coverage relative offset
          332, 0uy // TX message relative offset
          336, 0uy // TX program relative offset
          340, 0uy // TX counter relative offset
          344, 0uy // TX template relative offset
          440, 0uy ] // footer CRC
        |> List.iter (fun (offset, value) ->
            let malformed = Array.copy original
            malformed.[offset] <- value
            readDetailed malformed |> Result.isError |> Assert.True)

    [<Fact>]
    let ``Legacy inspect output and exact flashed A and B image bytes remain frozen`` () =
        let cases =
            [ "schema_a.scimg",
              "9197bf85693f823f3623f9562a2a892468dc461a1c7cdaf4f60a6dc91cad6d1e",
              "10914a41cdefd4808095258791516f1bbde34e17e97b4b65247f70d5f93f8469"
              "schema_b.scimg",
              "6b1a5bdf3255bff17e12195bea2fd4703ae6427e06f2e701d7fde231e05312f2",
              "c7d44ef4d7311dcc6c4113e143d5d388f6bf66df43618d49b1cbc1728bad7803" ]

        for imageName, expectedImageHash, expectedLegacyInspectHash in cases do
            let bytes = frozenImage imageName
            let legacy = inspect bytes |> unwrap "legacy inspect"
            Assert.Equal(expectedImageHash, sha256 bytes)
            Assert.Equal(expectedLegacyInspectHash, sha256 (Text.Encoding.UTF8.GetBytes legacy))
            Assert.DoesNotContain("sc.inspect/v1", legacy)
            Assert.EndsWith("\n", legacy)
