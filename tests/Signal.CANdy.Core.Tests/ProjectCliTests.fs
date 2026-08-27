namespace Signal.CANdy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open Xunit

module ProjectCliTests =

    type private ProcessResult =
        { ExitCode: int
          Stdout: string
          Stderr: string }

    let private repoRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private cliDll = Path.Combine(AppContext.BaseDirectory, "Signal.CANdy.CLI.dll")

    let private manifest target =
        $"""format: sc.project/v1
name: scimg-protection-demo
pool:
  definition: pool.json
wireSources:
  - name: protection
    type: dbc
    path: protection_demo.dbc
binding: binding.json
target: {target}
outputs:
  image: build/protection_demo.scimg
  header: build/scimg_protection_demo.h
  inspect: build/protection_demo.inspect.json
"""

    let private capability stateBytes =
        $"""{{
  "format": "sc.runtime-capabilities/v1",
  "runtimeImageMajor": 1,
  "runtimeImageMinor": 0,
  "runtimeAbi": "ilp32",
  "features": [
    "rx",
    "tx",
    "crc8-sae-j1850",
    "crc16-ccitt-false",
    "rx-counter",
    "tx-counter"
  ],
  "poolAbiHash": "sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947",
  "limits": {{
    "maxImageBytes": 428,
    "maxRuntimeStateBytes": {stateBytes},
    "maxRuntimeScratchBytes": 8,
    "maxRxMessages": 1,
    "maxRxPrograms": 1,
    "maxTxMessages": 1,
    "maxTxPrograms": 2,
    "maxPoolSlots": 3,
    "maxConversions": 1,
    "maxNestedMuxRecords": 0,
    "maxMuxDepth": 0,
    "maxQualityEntries": 0,
    "maxProtectionPlans": 2,
    "maxTxCounters": 1,
    "maxRxCounters": 1,
    "maxCoverageSpans": 2,
    "maxTxTemplateBytes": 8,
    "maxPayloadBytes": 8
  }}
}}
"""

    let private withTempDirectory action =
        let root =
            Path.Combine(Path.GetTempPath(), "signal-candy-cli-tests-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        try
            action root
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)

    let private prepareProject root =
        let source = Path.Combine(repoRoot, "examples", "scimg_protection_demo")

        [ "pool.json"; "binding.json"; "protection_demo.dbc" ]
        |> List.iter (fun name -> File.Copy(Path.Combine(source, name), Path.Combine(root, name)))

        File.WriteAllText(Path.Combine(root, "cc1a.runtime.json"), capability 28)
        File.WriteAllText(Path.Combine(root, "cc1a-insufficient.runtime.json"), capability 27)
        File.WriteAllText(Path.Combine(root, "project.yaml"), manifest "cc1a.runtime.json")

        File.WriteAllText(Path.Combine(root, "project-insufficient.yaml"), manifest "cc1a-insufficient.runtime.json")

    let private runCli workingDirectory arguments =
        Assert.True(File.Exists(cliDll), sprintf "Built CLI not found at %s" cliDll)
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.ArgumentList.Add(cliDll)
        arguments |> List.iter startInfo.ArgumentList.Add

        use child = Process.Start(startInfo)
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()

        if not (child.WaitForExit(30000)) then
            child.Kill(true)
            Assert.Fail("CLI process exceeded the 30 second test timeout.")

        { ExitCode = child.ExitCode
          Stdout = stdout.GetAwaiter().GetResult()
          Stderr = stderr.GetAwaiter().GetResult() }

    let private sha256File path =
        File.ReadAllBytes(path)
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private snapshot root =
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        |> Seq.map (fun path -> Path.GetRelativePath(root, path), sha256File path)
        |> Map.ofSeq

    let private temporaryFiles root =
        Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories)
        |> Seq.filter (fun path -> Path.GetFileName(path).Contains(".signal-candy-", StringComparison.Ordinal))
        |> Seq.toList

    [<Fact>]
    let ``Project help is exposed by the real CLI driver`` () =
        withTempDirectory (fun root ->
            let result = runCli root [ "project"; "--help" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Empty(result.Stderr)
            Assert.Contains("Project commands:", result.Stdout)
            Assert.Contains("project validate <manifest.yaml>", result.Stdout)
            Assert.Contains("project build <manifest.yaml>", result.Stdout))

    [<Fact>]
    let ``Project grammar errors return exit two`` () =
        withTempDirectory (fun root ->
            [ [ "project" ]
              [ "project"; "validate" ]
              [ "project"; "build" ]
              [ "project"; "unknown"; "project.yaml" ]
              [ "project"; "validate"; "project.yaml"; "extra" ]
              [ "project"; "build"; "project.yaml"; "--extra" ] ]
            |> List.iter (fun arguments ->
                let result = runCli root arguments
                Assert.Equal(2, result.ExitCode)
                Assert.Empty(result.Stdout)
                Assert.NotEmpty(result.Stderr)))

    [<Fact>]
    let ``Project validate succeeds without any filesystem side effect`` () =
        withTempDirectory (fun root ->
            prepareProject root
            let before = snapshot root
            let unrelatedCwd = Path.Combine(root, "unrelated", "cwd")
            Directory.CreateDirectory(unrelatedCwd) |> ignore

            let result =
                runCli unrelatedCwd [ "project"; "validate"; Path.Combine(root, "project.yaml") ]

            let after = snapshot root
            Assert.Equal(0, result.ExitCode)
            Assert.Empty(result.Stderr)
            Assert.Contains("image=428 bytes", result.Stdout)
            Assert.Contains("state=28 bytes", result.Stdout)
            Assert.Contains("scratch=8 bytes", result.Stdout)
            let unchanged = before = after
            Assert.True(unchanged)
            Assert.False(Directory.Exists(Path.Combine(root, "build")))
            Assert.Empty(temporaryFiles root))

    [<Fact>]
    let ``Bad manifest is exit three and missing input is exit four with no output`` () =
        withTempDirectory (fun root ->
            prepareProject root
            File.WriteAllText(Path.Combine(root, "bad.yaml"), manifest "cc1a.runtime.json" + "unknown: value\n")
            File.WriteAllText(Path.Combine(root, "missing.yaml"), manifest "missing.runtime.json")

            let invalid = runCli root [ "project"; "build"; "bad.yaml" ]
            Assert.Equal(3, invalid.ExitCode)
            Assert.Empty(invalid.Stdout)
            Assert.NotEmpty(invalid.Stderr)
            Assert.False(Directory.Exists(Path.Combine(root, "build")))

            let missing = runCli root [ "project"; "build"; "missing.yaml" ]
            Assert.Equal(4, missing.ExitCode)
            Assert.Empty(missing.Stdout)
            Assert.NotEmpty(missing.Stderr)
            Assert.False(Directory.Exists(Path.Combine(root, "build")))
            Assert.Empty(temporaryFiles root))

    [<Fact>]
    let ``Insufficient state capability is exit three and creates no output`` () =
        withTempDirectory (fun root ->
            prepareProject root

            let result =
                runCli root [ "project"; "build"; Path.Combine(root, "project-insufficient.yaml") ]

            Assert.Equal(3, result.ExitCode)
            Assert.Empty(result.Stdout)

            Assert.Equal(
                "error[SC2207] target.maxRuntimeStateBytes: required 28, supported 27\n",
                result.Stderr.Replace("\r\n", "\n")
            )

            Assert.False(Directory.Exists(Path.Combine(root, "build")))
            Assert.Empty(temporaryFiles root))

    [<Fact>]
    let ``Project build is direct-byte-identical and deterministic across CWDs`` () =
        withTempDirectory (fun root ->
            let first = Path.Combine(root, "first")
            let second = Path.Combine(root, "second")
            let direct = Path.Combine(root, "direct")
            Directory.CreateDirectory(first) |> ignore
            Directory.CreateDirectory(second) |> ignore
            Directory.CreateDirectory(direct) |> ignore
            prepareProject first
            prepareProject second

            let directImage = Path.Combine(direct, "direct.scimg")
            let directInspect = Path.Combine(direct, "direct.inspect.json")

            let directResult =
                runCli
                    direct
                    [ "scimg"
                      "-d"
                      Path.Combine(first, "protection_demo.dbc")
                      "-p"
                      Path.Combine(first, "pool.json")
                      "-b"
                      Path.Combine(first, "binding.json")
                      "-o"
                      directImage
                      "--inspect"
                      directInspect ]

            Assert.Equal(0, directResult.ExitCode)

            let firstCwd = Path.Combine(root, "cwd-a")
            let secondCwd = Path.Combine(root, "cwd-b")
            Directory.CreateDirectory(firstCwd) |> ignore
            Directory.CreateDirectory(secondCwd) |> ignore

            let firstResult =
                runCli firstCwd [ "project"; "build"; Path.Combine(first, "project.yaml") ]

            let secondResult =
                runCli secondCwd [ "project"; "build"; Path.Combine(second, "project.yaml") ]

            Assert.Equal(0, firstResult.ExitCode)
            Assert.Equal(0, secondResult.ExitCode)
            Assert.Empty(firstResult.Stderr)
            Assert.Empty(secondResult.Stderr)

            let firstImage = Path.Combine(first, "build", "protection_demo.scimg")
            let secondImage = Path.Combine(second, "build", "protection_demo.scimg")
            let firstHeader = Path.Combine(first, "build", "scimg_protection_demo.h")
            let secondHeader = Path.Combine(second, "build", "scimg_protection_demo.h")
            let firstInspect = Path.Combine(first, "build", "protection_demo.inspect.json")
            let secondInspect = Path.Combine(second, "build", "protection_demo.inspect.json")

            Assert.Equal<byte>(File.ReadAllBytes(directImage), File.ReadAllBytes(firstImage))
            Assert.Equal<byte>(File.ReadAllBytes(directInspect), File.ReadAllBytes(firstInspect))
            Assert.Equal<byte>(File.ReadAllBytes(firstImage), File.ReadAllBytes(secondImage))
            Assert.Equal<byte>(File.ReadAllBytes(firstHeader), File.ReadAllBytes(secondHeader))
            Assert.Equal<byte>(File.ReadAllBytes(firstInspect), File.ReadAllBytes(secondInspect))
            Assert.Equal(428L, FileInfo(firstImage).Length)
            Assert.Equal(3105L, FileInfo(firstHeader).Length)
            Assert.Equal("26e6f8529af6c840d294a87cb967a490b9cd78394b2c9911fee32681660fe7df", sha256File firstImage)
            Assert.Equal("f07304bebbf627d64955c77221e786470d0d5abe49b449a13b024af5d17dc3bb", sha256File firstHeader)
            Assert.Equal("9b5fe2f0f050456afe339b33286446fec980416dc83534959193d1deb4fca434", sha256File firstInspect)
            Assert.Empty(temporaryFiles root))
