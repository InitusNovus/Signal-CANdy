namespace Signal.CANdy.Core.Tests

open System
open System.IO
open Xunit
open Xunit.Sdk
open Signal.CANdy.Core.ProjectManifest

module ProjectManifestTests =

    let private normalizeLf (text: string) = text.Replace("\r\n", "\n")

    let private validYaml =
        normalizeLf
            """format: sc.project/v1
name: scimg-protection-demo
pool:
  definition: pool.json
wireSources:
  - name: protection
    type: dbc
    path: protection_demo.dbc
binding: binding.json
target: cc1a-test-1.runtime.json
outputs:
  image: build/protection_demo.scimg
  header: build/scimg_protection_demo.h
  inspect: build/protection_demo.inspect.json
"""

    let private unwrap result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "Expected success, got %A" errors

    let private withTempDirectory action =
        let root =
            Path.Combine(Path.GetTempPath(), "signal-candy-project-tests-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        try
            action root
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)

    let private prepareRoot root =
        [ "project.yaml"
          "pool.json"
          "protection_demo.dbc"
          "binding.json"
          "cc1a-test-1.runtime.json" ]
        |> List.iter (fun name -> File.WriteAllText(Path.Combine(root, name), "fixture"))

    let private resolveYaml root yaml =
        let manifestPath = Path.Combine(root, "project.yaml")
        File.WriteAllText(manifestPath, yaml)
        parse yaml |> Result.bind (resolve manifestPath)

    [<Fact>]
    let ``Project parser accepts the exact v1 shape and pool alternatives`` () =
        let parsed = parse validYaml |> unwrap
        Assert.Equal("scimg-protection-demo", parsed.Name)
        Assert.Single(parsed.WireSources) |> ignore

        validYaml.Replace("definition: pool.json", "manifest: pool.manifest.json")
        |> parse
        |> Result.isOk
        |> Assert.True

    [<Fact>]
    let ``Project parser rejects every missing required root key`` () =
        let variants =
            [ validYaml.Replace("format: sc.project/v1\n", "")
              validYaml.Replace("name: scimg-protection-demo\n", "")
              validYaml.Replace("pool:\n  definition: pool.json\n", "")
              validYaml.Replace(
                  "wireSources:\n  - name: protection\n    type: dbc\n    path: protection_demo.dbc\n",
                  ""
              )
              validYaml.Replace("binding: binding.json\n", "")
              validYaml.Replace("target: cc1a-test-1.runtime.json\n", "")
              validYaml.Substring(0, validYaml.IndexOf("outputs:", StringComparison.Ordinal)) ]

        variants |> List.iter (fun yaml -> parse yaml |> Result.isError |> Assert.True)

    [<Fact>]
    let ``Project parser rejects missing nested required keys`` () =
        let variants =
            [ validYaml.Replace("  definition: pool.json\n", "")
              validYaml.Replace("  - name: protection\n    type: dbc\n", "  - type: dbc\n")
              validYaml.Replace("    type: dbc\n", "")
              validYaml.Replace("    path: protection_demo.dbc\n", "")
              validYaml.Replace("  image: build/protection_demo.scimg\n", "") ]

        variants |> List.iter (fun yaml -> parse yaml |> Result.isError |> Assert.True)

    [<Fact>]
    let ``Project parser rejects unknown and duplicate keys in every mapping`` () =
        let variants =
            [ validYaml.Replace("name:", "future: value\nname:")
              validYaml.Replace("name:", "name: first\nname:")
              validYaml.Replace("  definition:", "  future: value\n  definition:")
              validYaml.Replace("  definition:", "  definition: first.json\n  definition:")
              validYaml.Replace("    type:", "    future: value\n    type:")
              validYaml.Replace("    type:", "    type: dbc\n    type:")
              validYaml.Replace("  image:", "  future: value\n  image:")
              validYaml.Replace("  image:", "  image: first.scimg\n  image:") ]

        variants |> List.iter (fun yaml -> parse yaml |> Result.isError |> Assert.True)

    [<Fact>]
    let ``Project pool requires exactly one definition or manifest`` () =
        let neither = validYaml.Replace("  definition: pool.json\n", "")

        let both =
            validYaml.Replace("  definition: pool.json\n", "  definition: pool.json\n  manifest: pool.manifest.json\n")

        parse neither |> Result.isError |> Assert.True
        parse both |> Result.isError |> Assert.True

    [<Fact>]
    let ``Project parser enforces output extensions and permits optional outputs`` () =
        [ validYaml.Replace("protection_demo.scimg", "protection_demo.bin")
          validYaml.Replace("scimg_protection_demo.h", "scimg_protection_demo.hpp")
          validYaml.Replace("protection_demo.inspect.json", "protection_demo.inspect.txt") ]
        |> List.iter (fun yaml -> parse yaml |> Result.isError |> Assert.True)

        validYaml
            .Replace("  header: build/scimg_protection_demo.h\n", "")
            .Replace("  inspect: build/protection_demo.inspect.json\n", "")
        |> parse
        |> Result.isOk
        |> Assert.True

    [<Fact>]
    let ``Project parser enforces identifiers DBC source type and nonempty wires`` () =
        let variants =
            [ validYaml.Replace("name: scimg-protection-demo", "name: 1bad")
              validYaml.Replace("name: scimg-protection-demo", "name: " + String('a', 65))
              validYaml.Replace("    type: dbc", "    type: json")
              validYaml.Replace(
                  "wireSources:\n  - name: protection\n    type: dbc\n    path: protection_demo.dbc",
                  "wireSources: []"
              )
              validYaml.Replace(
                  "    path: protection_demo.dbc",
                  "    path: protection_demo.dbc\n  - name: protection\n    type: dbc\n    path: other.dbc"
              ) ]

        variants |> List.iter (fun yaml -> parse yaml |> Result.isError |> Assert.True)

    [<Fact>]
    let ``Project parser rejects aliases anchors merges tags directives and extra documents`` () =
        let variants =
            [ validYaml.Replace("pool.json", "&pool pool.json")
              validYaml.Replace("binding.json", "*pool")
              validYaml.Replace("pool:\n", "defaults: &defaults { definition: pool.json }\npool:\n  <<: *defaults\n")
              validYaml.Replace("pool.json", "!!str pool.json")
              "%YAML 1.2\n---\n" + validYaml
              validYaml + "---\nformat: sc.project/v1\n"
              validYaml.Replace("name: scimg-protection-demo", "!project name: scimg-protection-demo") ]

        variants |> List.iter (fun yaml -> parse yaml |> Result.isError |> Assert.True)

    [<Fact>]
    let ``Project parser rejects nonscalar keys values nulls and implicit typed scalars`` () =
        let variants =
            [ validYaml.Replace("format: sc.project/v1", "? [format]\n: sc.project/v1")
              validYaml.Replace("name: scimg-protection-demo", "name:\n  nested: value")
              validYaml.Replace("binding: binding.json", "binding: null")
              validYaml.Replace("name: scimg-protection-demo", "name: true")
              validYaml.Replace("  - name: protection", "  - name: 123")
              validYaml.Replace("    path: protection_demo.dbc", "    path: [protection_demo.dbc]") ]

        variants |> List.iter (fun yaml -> parse yaml |> Result.isError |> Assert.True)

    [<Theory>]
    [<InlineData("/absolute")>]
    [<InlineData("//server/share")>]
    [<InlineData("C:/absolute")>]
    [<InlineData("C:drive-relative")>]
    [<InlineData("\\\\?\\C:\\device")>]
    [<InlineData("parent/../escape")>]
    [<InlineData("./local")>]
    [<InlineData("local/./file")>]
    [<InlineData("local//file")>]
    [<InlineData("local/")>]
    [<InlineData("local\\file")>]
    let ``Project paths reject unsafe syntax`` path =
        withTempDirectory (fun root ->
            prepareRoot root

            validYaml.Replace("pool.json", path)
            |> resolveYaml root
            |> Result.isError
            |> Assert.True)

    [<Fact>]
    let ``Project paths reject embedded NUL`` () =
        withTempDirectory (fun root ->
            prepareRoot root

            validYaml.Replace("pool.json", "bad\u0000path")
            |> resolveYaml root
            |> Result.isError
            |> Assert.True)

    [<Fact>]
    let ``Project paths treat tilde and environment syntax as literal names`` () =
        withTempDirectory (fun root ->
            prepareRoot root
            File.WriteAllText(Path.Combine(root, "~"), "fixture")
            Directory.CreateDirectory(Path.Combine(root, "$HOME")) |> ignore
            File.WriteAllText(Path.Combine(root, "$HOME", "pool.json"), "fixture")

            validYaml.Replace("pool.json", "~")
            |> resolveYaml root
            |> Result.isOk
            |> Assert.True

            validYaml.Replace("pool.json", "$HOME/pool.json")
            |> resolveYaml root
            |> Result.isOk
            |> Assert.True)

    [<Fact>]
    let ``Project resolution is manifest relative and accepts a missing output parent`` () =
        withTempDirectory (fun root ->
            prepareRoot root
            let resolved = resolveYaml root validYaml |> unwrap
            Assert.Equal("scimg-protection-demo", resolved.Name)
            Assert.Single(resolved.WireSources) |> ignore
            Assert.False(Directory.Exists(Path.Combine(root, "build"))))

    [<Fact>]
    let ``Project resolution rejects duplicate input output and case-only collisions`` () =
        withTempDirectory (fun root ->
            prepareRoot root

            let variants =
                [ validYaml.Replace(
                      "    path: protection_demo.dbc",
                      "    path: protection_demo.dbc\n  - name: second\n    type: dbc\n    path: PROTECTION_DEMO.DBC"
                  )
                  validYaml.Replace("build/protection_demo.scimg", "pool.json")
                  validYaml.Replace("build/scimg_protection_demo.h", "build/PROTECTION_DEMO.SCIMG")
                  validYaml.Replace("build/protection_demo.scimg", "project.yaml") ]

            variants
            |> List.iter (fun yaml -> resolveYaml root yaml |> Result.isError |> Assert.True))

    [<Fact>]
    let ``Project resolution rejects existing outputs and a regular-file parent`` () =
        withTempDirectory (fun root ->
            prepareRoot root
            Directory.CreateDirectory(Path.Combine(root, "build")) |> ignore
            File.WriteAllText(Path.Combine(root, "build", "protection_demo.scimg"), "existing")
            resolveYaml root validYaml |> Result.isError |> Assert.True)

        withTempDirectory (fun root ->
            prepareRoot root
            File.WriteAllText(Path.Combine(root, "build"), "not a directory")
            resolveYaml root validYaml |> Result.isError |> Assert.True)

    let private createFileLink linkPath targetPath =
        try
            File.CreateSymbolicLink(linkPath, targetPath) |> ignore
        with
        | :? UnauthorizedAccessException
        | :? PlatformNotSupportedException
        | :? IOException -> raise (SkipException.ForSkip("Symbolic links are unavailable on this test host."))

    let private createDirectoryLink linkPath targetPath =
        try
            Directory.CreateSymbolicLink(linkPath, targetPath) |> ignore
        with
        | :? UnauthorizedAccessException
        | :? PlatformNotSupportedException
        | :? IOException -> raise (SkipException.ForSkip("Directory links are unavailable on this test host."))

    [<Fact>]
    let ``Project resolution rejects reparse input leaves and parents`` () =
        withTempDirectory (fun root ->
            prepareRoot root
            let real = Path.Combine(root, "real-pool.json")
            File.WriteAllText(real, "fixture")
            File.Delete(Path.Combine(root, "pool.json"))
            createFileLink (Path.Combine(root, "pool.json")) real
            resolveYaml root validYaml |> Result.isError |> Assert.True)

        withTempDirectory (fun root ->
            prepareRoot root
            let real = Path.Combine(root, "real")
            Directory.CreateDirectory(real) |> ignore
            File.WriteAllText(Path.Combine(real, "pool.json"), "fixture")
            createDirectoryLink (Path.Combine(root, "linked")) real

            validYaml.Replace("pool.json", "linked/pool.json")
            |> resolveYaml root
            |> Result.isError
            |> Assert.True)

    [<Fact>]
    let ``Project resolution rejects reparse output parents and leaves`` () =
        withTempDirectory (fun root ->
            prepareRoot root
            let real = Path.Combine(root, "real-build")
            Directory.CreateDirectory(real) |> ignore
            createDirectoryLink (Path.Combine(root, "build")) real
            resolveYaml root validYaml |> Result.isError |> Assert.True)

        withTempDirectory (fun root ->
            prepareRoot root
            Directory.CreateDirectory(Path.Combine(root, "build")) |> ignore
            let target = Path.Combine(root, "existing.scimg")
            File.WriteAllBytes(target, [| 1uy |])
            createFileLink (Path.Combine(root, "build", "protection_demo.scimg")) target
            resolveYaml root validYaml |> Result.isError |> Assert.True)
