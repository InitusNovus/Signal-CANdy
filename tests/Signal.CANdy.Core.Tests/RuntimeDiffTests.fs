namespace Signal.CANdy.Core.Tests

open System
open System.IO
open System.Text.Json
open Xunit
open Signal.CANdy.Core
open Signal.CANdy.Core.ImageDocuments
open Signal.CANdy.Core.PoolAbi
open Signal.CANdy.Core.ProjectManifest
open Signal.CANdy.Core.RuntimeBuild
open Signal.CANdy.Core.RuntimeDiff

module RuntimeDiffTests =

    let private repoRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private demoRoot = Path.Combine(repoRoot, "examples", "scimg_activation_demo")

    let private unwrap description result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "%s failed: %A" description errors

    let private parseJson (json: string) =
        JsonDocument.Parse(json).RootElement.Clone()

    let private withBuilds action =
        let root =
            Path.Combine(Path.GetTempPath(), "signal-candy-issue24-diff-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        try
            [ "pool.json"
              "schema_a.dbc"
              "schema_b.dbc"
              "binding_a.json"
              "binding_b.json"
              "cc1a-test-1.runtime.json" ]
            |> List.iter (fun name -> File.Copy(Path.Combine(demoRoot, name), Path.Combine(root, name)))

            let load suffix =
                let yaml =
                    $"""format: sc.project/v1
name: diff-{suffix}
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

                let path = Path.Combine(root, sprintf "project_%s.yaml" suffix)
                File.WriteAllText(path, yaml)

                let resolved =
                    ProjectManifest.parse yaml
                    |> Result.bind (ProjectManifest.resolve path)
                    |> unwrap ("resolve " + suffix)

                loadAndCompile resolved |> unwrap ("load " + suffix) |> fst

            action (load "a") (load "b")
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)

    let private input
        (before: CompiledRuntime)
        (after: CompiledRuntime)
        (beforeMap: MapDocument option)
        (afterMap: MapDocument option)
        : DiffInput =
        { BeforeInspect = ImageDocuments.inspect before.ImageBytes |> unwrap "before inspect"
          AfterInspect = ImageDocuments.inspect after.ImageBytes |> unwrap "after inspect"
          BeforeMap = beforeMap
          AfterMap = afterMap
          BeforeActivation = Some before.Activation
          AfterActivation = Some after.Activation }

    let private canonical input =
        let document: DiffDocument = diff input |> unwrap "diff"
        writeDiff document |> unwrap "write diff"

    let private changeScalar name (change: JsonElement) =
        change.GetProperty("fields").EnumerateArray()
        |> Seq.find (fun field -> field.GetProperty("field").GetString() = name)

    [<Fact>]
    let ``A to B diff has exact class reason resources and three changed entities`` () =
        withBuilds (fun before after ->
            let json =
                canonical (input before after (Some before.MapDocument.Value) (Some after.MapDocument.Value))

            let root = parseJson json
            Assert.Equal("sc.diff/v1", root.GetProperty("format").GetString())

            Assert.Equal(
                "sha256:9197bf85693f823f3623f9562a2a892468dc461a1c7cdaf4f60a6dc91cad6d1e",
                root.GetProperty("before").GetProperty("imageSha256").GetString()
            )

            Assert.Equal(
                "sha256:6b1a5bdf3255bff17e12195bea2fd4703ae6427e06f2e701d7fde231e05312f2",
                root.GetProperty("after").GetProperty("imageSha256").GetString()
            )

            let activation = root.GetProperty("activation")
            Assert.Equal("compatible-reset-required", activation.GetProperty("class").GetString())
            Assert.Single(activation.GetProperty("reasons").EnumerateArray()) |> ignore

            Assert.Equal(
                "schema-content-changed",
                activation.GetProperty("reasons").[0].GetProperty("token").GetString()
            )

            Assert.Equal(JsonValueKind.Null, activation.GetProperty("reasons").[0].GetProperty("subject").ValueKind)

            let resources = root.GetProperty("resources")
            Assert.Equal(18, resources.GetArrayLength())

            for resource in resources.EnumerateArray() do
                Assert.Equal(resource.GetProperty("before").GetUInt32(), resource.GetProperty("after").GetUInt32())
                Assert.Equal(0, resource.GetProperty("delta").GetInt32())

            let changes = root.GetProperty("changes")
            Assert.Equal(3, changes.GetArrayLength())

            Assert.Equal<string list>(
                [ "rx-message"; "tx-message"; "tx-counter" ],
                changes.EnumerateArray()
                |> Seq.map (fun change -> change.GetProperty("entity").GetString())
                |> Seq.toList
            )

            Assert.All(
                changes.EnumerateArray(),
                fun change -> Assert.Equal("changed", change.GetProperty("change").GetString())
            )

            Assert.Equal("rx:1", changes.[0].GetProperty("key").GetString())
            Assert.Equal("tx:33", changes.[1].GetProperty("key").GetString())
            Assert.Equal("tx:33", changes.[2].GetProperty("key").GetString())

            let rxCan = changeScalar "canId" changes.[0]
            Assert.Equal("806", rxCan.GetProperty("before").GetString())
            Assert.Equal("822", rxCan.GetProperty("after").GetString())
            let txCan = changeScalar "canId" changes.[1]
            Assert.Equal("805", txCan.GetProperty("before").GetString())
            Assert.Equal("821", txCan.GetProperty("after").GetString())
            let initial = changeScalar "initialValue" changes.[2]
            Assert.Equal("0", initial.GetProperty("before").GetString())
            Assert.Equal("9", initial.GetProperty("after").GetString())
            Assert.DoesNotContain('\r', json)
            Assert.EndsWith("\n", json)
            Assert.Equal(json, json |> parseDiff |> unwrap "parse" |> writeDiff |> unwrap "rewrite"))

    [<Fact>]
    let ``B to A is the exact directional inverse in deterministic order`` () =
        withBuilds (fun before after ->
            let forward =
                canonical (input before after (Some before.MapDocument.Value) (Some after.MapDocument.Value))
                |> parseJson

            let reverse =
                canonical (input after before (Some after.MapDocument.Value) (Some before.MapDocument.Value))
                |> parseJson

            let forwardChanges = forward.GetProperty("changes")
            let reverseChanges = reverse.GetProperty("changes")
            Assert.Equal(forwardChanges.GetArrayLength(), reverseChanges.GetArrayLength())

            for index in 0 .. forwardChanges.GetArrayLength() - 1 do
                let first = forwardChanges.[index]
                let second = reverseChanges.[index]
                Assert.Equal(first.GetProperty("entity").GetString(), second.GetProperty("entity").GetString())
                Assert.Equal(first.GetProperty("key").GetString(), second.GetProperty("key").GetString())
                let firstFields = first.GetProperty("fields")
                let secondFields = second.GetProperty("fields")
                Assert.Equal(firstFields.GetArrayLength(), secondFields.GetArrayLength())

                for fieldIndex in 0 .. firstFields.GetArrayLength() - 1 do
                    let left = firstFields.[fieldIndex]
                    let right = secondFields.[fieldIndex]
                    Assert.Equal(left.GetProperty("field").GetString(), right.GetProperty("field").GetString())
                    Assert.Equal(left.GetProperty("before").GetRawText(), right.GetProperty("after").GetRawText())
                    Assert.Equal(left.GetProperty("after").GetRawText(), right.GetProperty("before").GetRawText()))

    [<Fact>]
    let ``Identical and mapless nonidentical images use exact activation classes`` () =
        withBuilds (fun before after ->
            let identical =
                canonical (input before before (Some before.MapDocument.Value) (Some before.MapDocument.Value))
                |> parseJson

            Assert.Equal("identical", identical.GetProperty("activation").GetProperty("class").GetString())
            Assert.Equal(0, identical.GetProperty("activation").GetProperty("reasons").GetArrayLength())
            Assert.Equal(0, identical.GetProperty("changes").GetArrayLength())

            let mapless = canonical (input before after None None) |> parseJson
            Assert.Equal("unknown-without-map", mapless.GetProperty("activation").GetProperty("class").GetString())

            Assert.Equal(
                "source-map-missing",
                mapless.GetProperty("activation").GetProperty("reasons").[0].GetProperty("token").GetString()
            ))

    [<Fact>]
    let ``Pool ABI mismatch has precedence over otherwise compatible schema changes`` () =
        withBuilds (fun before after ->
            let alternate =
                PoolAbi.parse "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                |> unwrap "alternate hash"

            let alteredMap =
                { after.MapDocument.Value with
                    PoolAbiHash = alternate }

            let alteredAfter: CompiledRuntime =
                { after with
                    Activation =
                        { after.Activation with
                            PoolAbiHash = alternate } }

            let root =
                canonical (input before alteredAfter (Some before.MapDocument.Value) (Some alteredMap))
                |> parseJson

            Assert.Equal("incompatible-pool-abi", root.GetProperty("activation").GetProperty("class").GetString())

            Assert.Equal(
                "pool-abi-mismatch",
                root.GetProperty("activation").GetProperty("reasons").[0].GetProperty("token").GetString()
            ))

    [<Fact>]
    let ``Runtime version feature and resource mismatch reasons are stable and ordered`` () =
        withBuilds (fun before after ->
            let target = before.MapDocument.Value.Target

            let incompatibleTarget =
                { target with
                    RuntimeImageMajor = 0us
                    Features = Set.empty
                    Limits =
                        { target.Limits with
                            MaxImageBytes = 0u } }

            let beforeMap =
                { before.MapDocument.Value with
                    Target = incompatibleTarget }

            let root =
                canonical (input before after (Some beforeMap) (Some after.MapDocument.Value))
                |> parseJson

            Assert.Equal("incompatible-runtime", root.GetProperty("activation").GetProperty("class").GetString())

            let tokens =
                root.GetProperty("activation").GetProperty("reasons").EnumerateArray()
                |> Seq.map (fun reason -> reason.GetProperty("token").GetString())
                |> Seq.toList

            Assert.Equal("runtime-version-unsupported", tokens.Head)
            Assert.Contains("runtime-feature-unsupported", tokens)
            Assert.Contains("runtime-resource-limit-exceeded", tokens))

    [<Fact>]
    let ``Diff parser rejects unknown duplicate bad hash numeric ordering and duplicate identities`` () =
        withBuilds (fun before after ->
            let canonicalJson: string =
                canonical (input before after (Some before.MapDocument.Value) (Some after.MapDocument.Value))

            [ canonicalJson.Replace("  \"format\":", "  \"future\": true,\n  \"format\":")
              canonicalJson.Replace("  \"format\":", "  \"format\": \"sc.diff/v1\",\n  \"format\":")
              canonicalJson.Replace("sha256:9197", "SHA256:9197")
              canonicalJson.Replace("\"delta\": 0", "\"delta\": 0.0")
              canonicalJson.Replace("\"entity\": \"tx-message\"", "\"entity\": \"rx-message\"") ]
            |> List.iter (fun json -> parseDiff json |> Result.isError |> Assert.True))
