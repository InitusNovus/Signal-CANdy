namespace Signal.CANdy.Core.Tests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Signal.CANdy.Core
open Signal.CANdy.Core.ImageDocuments
open Signal.CANdy.Core.ProjectManifest
open Signal.CANdy.Core.RuntimeBuild

module ImageDocumentsTests =

    let private repoRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private demoRoot = Path.Combine(repoRoot, "examples", "scimg_activation_demo")

    let private unwrap description result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "%s failed: %A" description errors

    let private frozenImage suffix =
        File.ReadAllBytes(Path.Combine(demoRoot, "build", sprintf "schema_%s.scimg" suffix))

    let private parseJson (json: string) =
        JsonDocument.Parse(json).RootElement.Clone()

    let private names (element: JsonElement) =
        element.EnumerateObject() |> Seq.map _.Name |> Seq.toList

    let private arrayLength (name: string) (root: JsonElement) = root.GetProperty(name).GetArrayLength()

    let private assertRange startExclusive endExclusive (element: JsonElement) =
        Assert.Equal(startExclusive, element.GetProperty("start").GetUInt32())
        Assert.Equal(endExclusive, element.GetProperty("end").GetUInt32())

    let private replaceOnce (oldValue: string) (newValue: string) (value: string) =
        let offset = value.IndexOf(oldValue, StringComparison.Ordinal)
        Assert.True(offset >= 0, sprintf "Expected canonical fragment was absent: %s" oldValue)

        value.Substring(0, offset)
        + newValue
        + value.Substring(offset + oldValue.Length)

    let private withCopiedProjects action =
        let root =
            Path.Combine(Path.GetTempPath(), "signal-candy-issue24-documents-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        try
            [ "pool.json"
              "schema_a.dbc"
              "schema_b.dbc"
              "binding_a.json"
              "binding_b.json"
              "cc1a-test-1.runtime.json" ]
            |> List.iter (fun name -> File.Copy(Path.Combine(demoRoot, name), Path.Combine(root, name)))

            let manifest suffix =
                $"""format: sc.project/v1
name: issue24-{suffix}
pool:
  definition: pool.json
wireSources:
  - name: schema-{suffix}
    type: dbc
    path: schema_{suffix}.dbc
binding: binding_{suffix}.json
target: cc1a-test-1.runtime.json
outputs:
  image: build/schema_{suffix}.scimg
  map: build/schema_{suffix}.map.json
"""

            let load suffix =
                let path = Path.Combine(root, sprintf "project_%s.yaml" suffix)
                let yaml = manifest suffix
                File.WriteAllText(path, yaml)

                let resolved =
                    ProjectManifest.parse yaml
                    |> Result.bind (ProjectManifest.resolve path)
                    |> unwrap ("resolve project " + suffix)

                loadAndCompile resolved |> unwrap ("load project " + suffix) |> fst

            action (load "a") (load "b")
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)

    [<Fact>]
    let ``Versioned inspect canonical root and regions cover every current image table`` () =
        let document: InspectDocument =
            ImageDocuments.inspect (frozenImage "a") |> unwrap "inspect"

        let canonical: string = writeInspect document |> unwrap "write inspect"
        let root = parseJson canonical

        Assert.Equal<string list>(
            [ "format"
              "image"
              "runtime"
              "resources"
              "regions"
              "poolSlots"
              "conversions"
              "rxMessages"
              "rxPrograms"
              "nestedMuxRecords"
              "rxProtectionPlans"
              "txProtectionPlans"
              "rxCounters"
              "coverageSpans"
              "txMessages"
              "txPrograms"
              "txCounters"
              "txTemplates" ],
            names root
        )

        Assert.Equal("sc.inspect/v1", root.GetProperty("format").GetString())
        let image = root.GetProperty("image")

        Assert.Equal(
            "sha256:9197bf85693f823f3623f9562a2a892468dc461a1c7cdaf4f60a6dc91cad6d1e",
            image.GetProperty("sha256").GetString()
        )

        Assert.Equal(444u, image.GetProperty("totalBytes").GetUInt32())
        Assert.Equal("0x26474F02", image.GetProperty("crc32").GetString())
        Assert.True(image.GetProperty("crcValid").GetBoolean())

        let runtime = root.GetProperty("runtime")
        Assert.Equal(60u, runtime.GetProperty("stateBytes").GetUInt32())
        Assert.Equal(8u, runtime.GetProperty("scratchBytes").GetUInt32())

        let regions = root.GetProperty("regions")

        [ "header", 0u, 32u
          "directory", 32u, 64u
          "rxMessages", 64u, 72u
          "rxPrograms", 72u, 88u
          "conversions", 88u, 112u
          "symbols", 112u, 164u
          "extensionHeader", 164u, 204u
          "nestedMuxRecords", 204u, 204u
          "qualityEntries", 204u, 216u
          "protectionHeader", 216u, 264u
          "rxProtectionPlans", 264u, 280u
          "txProtectionPlans", 280u, 296u
          "rxCounters", 296u, 312u
          "coverageSpans", 312u, 320u
          "txHeader", 320u, 352u
          "txMessages", 352u, 376u
          "txPrograms", 376u, 408u
          "txCounters", 408u, 432u
          "txTemplates", 432u, 440u
          "footer", 440u, 444u ]
        |> List.iter (fun (name, startExclusive, endExclusive) ->
            assertRange startExclusive endExclusive (regions.GetProperty(name)))

        Assert.DoesNotContain('\r', canonical)
        Assert.EndsWith("\n", canonical)
        Assert.Equal(canonical, document |> writeInspect |> unwrap "repeat write")
        Assert.Equal(canonical, canonical |> parseInspect |> unwrap "parse" |> writeInspect |> unwrap "rewrite")

    [<Fact>]
    let ``Versioned inspect covers every current record table and exact A values`` () =
        let root =
            frozenImage "a"
            |> ImageDocuments.inspect
            |> unwrap "inspect"
            |> writeInspect
            |> unwrap "write"
            |> parseJson

        [ "poolSlots", 3
          "conversions", 1
          "rxMessages", 1
          "rxPrograms", 1
          "nestedMuxRecords", 0
          "rxProtectionPlans", 1
          "txProtectionPlans", 1
          "rxCounters", 1
          "coverageSpans", 2
          "txMessages", 1
          "txPrograms", 2
          "txCounters", 1
          "txTemplates", 1 ]
        |> List.iter (fun (name, count) -> Assert.Equal(count, arrayLength name root))

        let rx = root.GetProperty("rxMessages").[0]
        Assert.Equal(806u, rx.GetProperty("canId").GetUInt32())
        assertRange 64u 72u (rx.GetProperty("range"))

        let tx = root.GetProperty("txMessages").[0]
        Assert.Equal(33u, tx.GetProperty("logicalMessageId").GetUInt32())
        Assert.Equal(805u, tx.GetProperty("canId").GetUInt32())
        assertRange 352u 376u (tx.GetProperty("range"))
        assertRange 432u 440u (tx.GetProperty("templateRange"))

        let qualities = root.GetProperty("poolSlots")
        Assert.Equal(30u, qualities.[0].GetProperty("freshnessMs").GetUInt32())
        assertRange 204u 208u (qualities.[0].GetProperty("qualityRange"))
        assertRange 208u 212u (qualities.[1].GetProperty("qualityRange"))
        assertRange 212u 216u (qualities.[2].GetProperty("qualityRange"))

        let spans = root.GetProperty("coverageSpans")
        Assert.Equal(0u, spans.[0].GetProperty("byteStart").GetUInt32())
        Assert.Equal(6u, spans.[0].GetProperty("byteEnd").GetUInt32())
        Assert.Equal(0u, spans.[1].GetProperty("byteStart").GetUInt32())
        Assert.Equal(7u, spans.[1].GetProperty("byteEnd").GetUInt32())
        Assert.Equal(0u, root.GetProperty("txCounters").[0].GetProperty("initialValue").GetUInt32())

    [<Fact>]
    let ``Inspect parser rejects unknown duplicate missing wrong numeric hash float and range values`` () =
        let canonical: string =
            frozenImage "a"
            |> ImageDocuments.inspect
            |> unwrap "inspect"
            |> writeInspect
            |> unwrap "write"

        let missing = JsonNode.Parse(canonical).AsObject()
        missing.Remove("format") |> ignore
        let wrongType = JsonNode.Parse(canonical).AsObject()
        wrongType["image"] <- JsonValue.Create("wrong")
        let unknownNested = JsonNode.Parse(canonical).AsObject()
        unknownNested["image"].AsObject().Add("unknown", null)

        let malformed =
            [ "{"
              canonical.Replace("  \"format\":", "  \"future\": true,\n  \"format\":")
              canonical.Replace("  \"format\":", "  \"format\": \"sc.inspect/v1\",\n  \"format\":")
              missing.ToJsonString()
              wrongType.ToJsonString()
              replaceOnce "\"formatVersion\": 1" "\"formatVersion\": 1.0" canonical
              canonical.Replace("sha256:9197", "sha256:9197".ToUpperInvariant())
              canonical.Replace("f64:3ff0000000000000", "1.0")
              replaceOnce "\"start\": 0" "\"start\": -1" canonical
              replaceOnce "\"end\": 32" "\"end\": 445" canonical
              unknownNested.ToJsonString() ]

        malformed
        |> List.iter (fun json -> json |> parseInspect |> Result.isError |> Assert.True)

    [<Fact>]
    let ``Inspect parser requires canonical array order but accepts object order and writer restores it`` () =
        let canonical: string =
            frozenImage "a"
            |> ImageDocuments.inspect
            |> unwrap "inspect"
            |> writeInspect
            |> unwrap "write"

        let node = JsonNode.Parse(canonical).AsObject()
        let format = node["format"].DeepClone()
        node.Remove("format") |> ignore
        node.Add("format", format)
        let shuffledObject = node.ToJsonString()

        Assert.Equal(
            canonical,
            shuffledObject
            |> parseInspect
            |> unwrap "parse shuffled"
            |> writeInspect
            |> unwrap "rewrite"
        )

        let ordered = JsonNode.Parse(canonical).AsObject()
        let slots = ordered["poolSlots"].AsArray()
        let first = slots.[0].DeepClone()
        let second = slots.[1].DeepClone()
        slots.[0] <- second
        slots.[1] <- first
        ordered.ToJsonString() |> parseInspect |> Result.isError |> Assert.True

    [<Fact>]
    let ``Project compilation carries canonical typed map with stable identities provenance and exact ranges`` () =
        withCopiedProjects (fun compiledA compiledB ->
            Assert.True(compiledA.MapDocument.IsSome)
            Assert.True(compiledA.MapJson.IsSome)
            let mapA: MapDocument = compiledA.MapDocument.Value
            Assert.Equal(compiledA.MapJson.Value, writeMap mapA |> unwrap "write map")

            Assert.Equal(
                compiledA.MapJson.Value,
                compiledA.MapJson.Value
                |> parseMap
                |> unwrap "parse map"
                |> writeMap
                |> unwrap "rewrite map"
            )

            let root = parseJson compiledA.MapJson.Value
            Assert.Equal("sc.map/v1", root.GetProperty("format").GetString())

            Assert.Equal(
                "sha256:9197bf85693f823f3623f9562a2a892468dc461a1c7cdaf4f60a6dc91cad6d1e",
                root.GetProperty("imageSha256").GetString()
            )

            Assert.Equal(
                "sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947",
                root.GetProperty("poolAbiHash").GetString()
            )

            let source = root.GetProperty("sources").[0]
            Assert.Equal("schema-a", source.GetProperty("key").GetString())
            Assert.Equal("schema_a.dbc", source.GetProperty("path").GetString())
            Assert.Equal("dbc", source.GetProperty("type").GetString())

            Assert.Equal<string list>(
                [ "pool:1"; "pool:2"; "pool:3" ],
                root.GetProperty("poolSlots").EnumerateArray()
                |> Seq.map (fun item -> item.GetProperty("key").GetString())
                |> Seq.toList
            )

            Assert.Equal("rx:1", root.GetProperty("rxMessages").[0].GetProperty("key").GetString())
            Assert.Equal("rx:1/pool:1", root.GetProperty("rxPrograms").[0].GetProperty("key").GetString())
            Assert.Equal("tx:33", root.GetProperty("txMessages").[0].GetProperty("key").GetString())

            Assert.Equal<string list>(
                [ "tx:33/pool:2"; "tx:33/pool:3" ],
                root.GetProperty("txPrograms").EnumerateArray()
                |> Seq.map (fun item -> item.GetProperty("key").GetString())
                |> Seq.toList
            )

            Assert.Equal(
                "conversion:f64:3ff0000000000000:f64:0000000000000000:0",
                root.GetProperty("conversions").[0].GetProperty("key").GetString()
            )

            Assert.Equal("rx:1", root.GetProperty("rxProtectionPlans").[0].GetProperty("key").GetString())
            Assert.Equal("tx:33", root.GetProperty("txProtectionPlans").[0].GetProperty("key").GetString())
            Assert.Equal("rx:1", root.GetProperty("rxCounters").[0].GetProperty("key").GetString())
            Assert.Equal("tx:33", root.GetProperty("txCounters").[0].GetProperty("key").GetString())
            Assert.Equal("rx:1/span:0", root.GetProperty("coverageSpans").[0].GetProperty("key").GetString())
            Assert.Equal("tx:33/span:0", root.GetProperty("coverageSpans").[1].GetProperty("key").GetString())
            Assert.Equal("tx:33", root.GetProperty("txTemplates").[0].GetProperty("key").GetString())

            Assert.Equal(
                "sha256:6b1a5bdf3255bff17e12195bea2fd4703ae6427e06f2e701d7fde231e05312f2",
                (parseJson compiledB.MapJson.Value).GetProperty("imageSha256").GetString()
            ))

    [<Fact>]
    let ``Map parser is strict for unknown duplicate numeric hash float range identity and source order`` () =
        withCopiedProjects (fun compiledA _ ->
            let canonical: string = compiledA.MapJson.Value

            let invalid =
                [ canonical.Replace("  \"format\":", "  \"future\": true,\n  \"format\":")
                  canonical.Replace("  \"format\":", "  \"format\": \"sc.map/v1\",\n  \"format\":")
                  replaceOnce "\"semanticId\": 1" "\"semanticId\": 1.0" canonical
                  canonical.Replace("sha256:9197", "sha256:9197".ToUpperInvariant())
                  canonical.Replace("f64:3ff0000000000000", "f64:3FF0000000000000")
                  replaceOnce "\"start\": 0" "\"start\": 445" canonical
                  canonical.Replace("\"key\": \"pool:2\"", "\"key\": \"pool:1\"") ]

            invalid
            |> List.iter (fun json -> parseMap json |> Result.isError |> Assert.True)

            let node = JsonNode.Parse(canonical).AsObject()
            let pools = node["poolSlots"].AsArray()
            let first = pools.[0].DeepClone()
            let second = pools.[1].DeepClone()
            pools.[0] <- second
            pools.[1] <- first
            node.ToJsonString() |> parseMap |> Result.isError |> Assert.True)
