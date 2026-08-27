namespace Signal.CANdy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Xunit

module ImageCliTests =

    type private ProcessResult =
        { ExitCode: int
          Stdout: string
          Stderr: string }

    let private repoRoot () =
        match Environment.GetEnvironmentVariable("SIGNAL_CANDY_REPO_ROOT") with
        | value when not (String.IsNullOrWhiteSpace(value)) -> Path.GetFullPath(value)
        | _ -> Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private demoRoot () =
        Path.Combine(repoRoot (), "examples", "scimg_activation_demo")

    let private cliDll () =
        match Environment.GetEnvironmentVariable("SIGNAL_CANDY_CLI_DLL") with
        | value when not (String.IsNullOrWhiteSpace(value)) -> Path.GetFullPath(value)
        | _ -> Path.Combine(AppContext.BaseDirectory, "Signal.CANdy.CLI.dll")

    let private runCli workingDirectory arguments =
        let dll = cliDll ()
        Assert.True(File.Exists(dll), sprintf "Built CLI not found at %s" dll)
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.ArgumentList.Add(dll)
        arguments |> List.iter startInfo.ArgumentList.Add

        use child = Process.Start(startInfo)
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()

        if not (child.WaitForExit(30000)) then
            child.Kill(true)
            Assert.Fail("CLI process exceeded the 30 second test timeout.")

        { ExitCode = child.ExitCode
          Stdout = stdout.GetAwaiter().GetResult().Replace("\r\n", "\n")
          Stderr = stderr.GetAwaiter().GetResult().Replace("\r\n", "\n") }

    let private withRoot action =
        let root =
            Path.Combine(Path.GetTempPath(), "signal-candy-issue24-cli-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        try
            action root
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)

    let private temporaryFiles root =
        Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories)
        |> Seq.filter (fun path -> Path.GetFileName(path).Contains(".signal-candy-", StringComparison.Ordinal))
        |> Seq.toList

    let private copyFrozen root =
        for suffix in [ "a"; "b" ] do
            File.Copy(
                Path.Combine(demoRoot (), "build", sprintf "schema_%s.scimg" suffix),
                Path.Combine(root, sprintf "schema_%s.scimg" suffix)
            )

            File.Copy(
                Path.Combine(demoRoot (), "build", sprintf "schema_%s.activation.json" suffix),
                Path.Combine(root, sprintf "schema_%s.activation.json" suffix)
            )

    let private prepareMappedProject root suffix =
        let projectRoot = Path.Combine(root, suffix)
        Directory.CreateDirectory(projectRoot) |> ignore

        [ "pool.json"
          sprintf "schema_%s.dbc" suffix
          sprintf "binding_%s.json" suffix
          "cc1a-test-1.runtime.json" ]
        |> List.iter (fun name -> File.Copy(Path.Combine(demoRoot (), name), Path.Combine(projectRoot, name)))

        let yaml =
            $"""format: sc.project/v1
name: cli-diff-{suffix}
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
  activation: build/schema_{suffix}.activation.json
"""

        let manifest = Path.Combine(projectRoot, "project.yaml")
        File.WriteAllText(manifest, yaml)
        projectRoot, manifest

    let private buildMappedPair root =
        let firstRoot, firstManifest = prepareMappedProject root "a"
        let secondRoot, secondManifest = prepareMappedProject root "b"
        let first = runCli root [ "project"; "build"; firstManifest ]
        let second = runCli root [ "project"; "build"; secondManifest ]
        Assert.Equal(0, first.ExitCode)
        Assert.Equal(0, second.ExitCode)
        Assert.Empty(first.Stderr)
        Assert.Empty(second.Stderr)

        (Path.Combine(firstRoot, "build", "schema_a.scimg"),
         Path.Combine(firstRoot, "build", "schema_a.map.json"),
         Path.Combine(firstRoot, "build", "schema_a.activation.json")),
        (Path.Combine(secondRoot, "build", "schema_b.scimg"),
         Path.Combine(secondRoot, "build", "schema_b.map.json"),
         Path.Combine(secondRoot, "build", "schema_b.activation.json"))

    [<Fact>]
    let ``Image command help is exposed at every real CLI grammar level`` () =
        withRoot (fun root ->
            [ [ "image"; "--help" ], [ "image inspect"; "image diff" ]
              [ "image"; "inspect"; "--help" ], [ "image inspect <image.scimg>"; "--out" ]
              [ "image"; "diff"; "--help" ], [ "image diff <before.scimg> <after.scimg>"; "--before-map" ] ]
            |> List.iter (fun (arguments, expected) ->
                let result = runCli root arguments
                Assert.Equal(0, result.ExitCode)
                Assert.Empty(result.Stderr)
                expected |> List.iter (fun token -> Assert.Contains(token, result.Stdout))))

    [<Fact>]
    let ``Image grammar errors are exit two and never write stdout`` () =
        withRoot (fun root ->
            [ [ "image" ]
              [ "image"; "inspect" ]
              [ "image"; "inspect"; "a.scimg"; "extra" ]
              [ "image"; "inspect"; "a.scimg"; "--out" ]
              [ "image"; "diff"; "a.scimg" ]
              [ "image"; "diff"; "a.scimg"; "b.scimg"; "--bad" ]
              [ "image"; "unknown" ] ]
            |> List.iter (fun arguments ->
                let result = runCli root arguments
                Assert.Equal(2, result.ExitCode)
                Assert.Empty(result.Stdout)
                Assert.NotEmpty(result.Stderr))

            Assert.Empty(temporaryFiles root))

    [<Fact>]
    let ``Image inspect writes complete canonical JSON to stdout only`` () =
        withRoot (fun root ->
            copyFrozen root
            let result = runCli root [ "image"; "inspect"; "schema_a.scimg" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Empty(result.Stderr)
            Assert.StartsWith("{\n  \"format\": \"sc.inspect/v1\",", result.Stdout)
            Assert.EndsWith("\n", result.Stdout)
            Assert.DoesNotContain('\r', result.Stdout)
            let document = JsonDocument.Parse(result.Stdout)

            Assert.Equal(
                "sha256:9197bf85693f823f3623f9562a2a892468dc461a1c7cdaf4f60a6dc91cad6d1e",
                document.RootElement.GetProperty("image").GetProperty("sha256").GetString()
            )

            Assert.Equal(18, document.RootElement.EnumerateObject() |> Seq.length)
            Assert.Empty(temporaryFiles root))

    [<Fact>]
    let ``Image inspect out acknowledges only after atomic canonical publication`` () =
        withRoot (fun root ->
            copyFrozen root
            let output = Path.Combine(root, "out", "schema_a.inspect.json")
            let result = runCli root [ "image"; "inspect"; "schema_a.scimg"; "--out"; output ]
            Assert.Equal(0, result.ExitCode)
            Assert.Empty(result.Stderr)
            Assert.Equal(sprintf "Wrote inspect: %s\n" output, result.Stdout)
            Assert.True(File.Exists(output))
            let bytes = File.ReadAllBytes(output)
            Assert.False(bytes.Length >= 3 && bytes.[0..2] = [| 0xEFuy; 0xBBuy; 0xBFuy |])
            let text = Encoding.UTF8.GetString(bytes)
            Assert.DoesNotContain('\r', text)
            Assert.EndsWith("\n", text)
            Assert.Empty(temporaryFiles root))

    [<Fact>]
    let ``Missing and malformed inspect inputs have stable exits and no partial output`` () =
        withRoot (fun root ->
            let missingOut = Path.Combine(root, "missing.json")

            let missing =
                runCli root [ "image"; "inspect"; "missing.scimg"; "--out"; missingOut ]

            Assert.Equal(4, missing.ExitCode)
            Assert.Empty(missing.Stdout)
            Assert.NotEmpty(missing.Stderr)
            Assert.False(File.Exists(missingOut))

            File.WriteAllBytes(Path.Combine(root, "malformed.scimg"), [| 0x53uy; 0x43uy |])
            let malformedOut = Path.Combine(root, "malformed.json")

            let malformed =
                runCli root [ "image"; "inspect"; "malformed.scimg"; "--out"; malformedOut ]

            Assert.Equal(3, malformed.ExitCode)
            Assert.Empty(malformed.Stdout)
            Assert.NotEmpty(malformed.Stderr)
            Assert.False(File.Exists(malformedOut))
            Assert.Empty(temporaryFiles root))

    [<Fact>]
    let ``Existing inspect output is exit four unchanged and never staged`` () =
        withRoot (fun root ->
            copyFrozen root
            let output = Path.Combine(root, "existing.json")
            File.WriteAllText(output, "sentinel")
            let result = runCli root [ "image"; "inspect"; "schema_a.scimg"; "--out"; output ]
            Assert.Equal(4, result.ExitCode)
            Assert.Empty(result.Stdout)
            Assert.NotEmpty(result.Stderr)
            Assert.Equal("sentinel", File.ReadAllText(output))
            Assert.Empty(temporaryFiles root))

    [<Fact>]
    let ``Mapless image diff writes unknown classification as canonical stdout`` () =
        withRoot (fun root ->
            copyFrozen root
            let result = runCli root [ "image"; "diff"; "schema_a.scimg"; "schema_b.scimg" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Empty(result.Stderr)
            Assert.StartsWith("{\n  \"format\": \"sc.diff/v1\",", result.Stdout)
            let document = JsonDocument.Parse(result.Stdout)

            Assert.Equal(
                "unknown-without-map",
                document.RootElement.GetProperty("activation").GetProperty("class").GetString()
            )

            Assert.Equal(
                "source-map-missing",
                document.RootElement
                    .GetProperty("activation")
                    .GetProperty("reasons")
                    .[0].GetProperty("token")
                    .GetString()
            ))

    [<Fact>]
    let ``Real mapped A to B image diff reports exact three changes and reverse values`` () =
        withRoot (fun root ->
            let (beforeImage, beforeMap, beforeActivation), (afterImage, afterMap, afterActivation) =
                buildMappedPair root

            let arguments firstImage secondImage firstMap secondMap firstActivation secondActivation =
                [ "image"
                  "diff"
                  firstImage
                  secondImage
                  "--before-map"
                  firstMap
                  "--after-map"
                  secondMap
                  "--before-activation"
                  firstActivation
                  "--after-activation"
                  secondActivation ]

            let forward =
                runCli root (arguments beforeImage afterImage beforeMap afterMap beforeActivation afterActivation)

            let reverse =
                runCli root (arguments afterImage beforeImage afterMap beforeMap afterActivation beforeActivation)

            for result in [ forward; reverse ] do
                Assert.Equal(0, result.ExitCode)
                Assert.Empty(result.Stderr)
                let document = JsonDocument.Parse(result.Stdout)

                Assert.Equal(
                    "compatible-reset-required",
                    document.RootElement.GetProperty("activation").GetProperty("class").GetString()
                )

                Assert.Equal(3, document.RootElement.GetProperty("changes").GetArrayLength())

            Assert.Contains("\"before\": \"806\"", forward.Stdout)
            Assert.Contains("\"after\": \"822\"", forward.Stdout)
            Assert.Contains("\"before\": \"0\"", forward.Stdout)
            Assert.Contains("\"after\": \"9\"", forward.Stdout)
            Assert.Contains("\"before\": \"822\"", reverse.Stdout)
            Assert.Contains("\"after\": \"806\"", reverse.Stdout)
            Assert.Contains("\"before\": \"9\"", reverse.Stdout)
            Assert.Contains("\"after\": \"0\"", reverse.Stdout)
            Assert.Empty(temporaryFiles root))

    [<Fact>]
    let ``Image diff out rejects existing malformed evidence and leaves no partial file`` () =
        withRoot (fun root ->
            copyFrozen root
            let existing = Path.Combine(root, "existing.diff.json")
            File.WriteAllText(existing, "sentinel")

            let occupied =
                runCli root [ "image"; "diff"; "schema_a.scimg"; "schema_b.scimg"; "--out"; existing ]

            Assert.Equal(4, occupied.ExitCode)
            Assert.Equal("sentinel", File.ReadAllText(existing))

            let badMap = Path.Combine(root, "bad.map.json")
            File.WriteAllText(badMap, "{")
            let output = Path.Combine(root, "never.diff.json")

            let malformed =
                runCli
                    root
                    [ "image"
                      "diff"
                      "schema_a.scimg"
                      "schema_b.scimg"
                      "--before-map"
                      badMap
                      "--out"
                      output ]

            Assert.Equal(3, malformed.ExitCode)
            Assert.Empty(malformed.Stdout)
            Assert.NotEmpty(malformed.Stderr)
            Assert.False(File.Exists(output))
            Assert.Empty(temporaryFiles root))
