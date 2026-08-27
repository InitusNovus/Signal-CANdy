namespace Signal.CANdy.Core.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text
open Xunit
open Signal.CANdy.Core
open Signal.CANdy.Core.ArtifactWriter
open Signal.CANdy.Core.PoolAbi
open Signal.CANdy.Core.ProjectManifest
open Signal.CANdy.Core.RuntimeBuild
open Signal.CANdy.Core.RuntimeCapabilities

module ActivationDescriptorTests =

    let private repoRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private fixturePath name =
        Path.Combine(repoRoot, "examples", "scimg_protection_demo", name)

    let private unwrap description result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "%s failed: %A" description errors

    let private protectionBuild () =
        let ir = Dbc.parseDbcFile (fixturePath "protection_demo.dbc") |> unwrap "DBC parse"
        let wire = Wire.toWireModel ir |> unwrap "wire normalization"

        let pool =
            File.ReadAllText(fixturePath "pool.json")
            |> Pool.parsePoolDefinition
            |> unwrap "pool parse"

        let bindings =
            File.ReadAllText(fixturePath "binding.json")
            |> Binding.parseBindingSet
            |> unwrap "binding parse"

        compile
            { Pool = pool
              Wires = [ "protection", wire ]
              Bindings = bindings }
        |> unwrap "runtime compile"

    let private canonical =
        """{
  "format": "sc.activation/v1",
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
  "imageFeatureFlags": 5,
  "poolAbiHash": "sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947",
  "imageSha256": "sha256:26e6f8529af6c840d294a87cb967a490b9cd78394b2c9911fee32681660fe7df",
  "imageBytes": 428,
  "runtimeStateBytes": 28,
  "runtimeScratchBytes": 8,
  "poolSlots": 3
}
"""
        |> fun value -> value.Replace("\r\n", "\n")

    [<Fact>]
    let ``Compiled runtime carries the exact activation descriptor and canonical JSON`` () =
        let compiled = protectionBuild ()
        let descriptor: ActivationDescriptor = compiled.Activation

        Assert.Equal(1us, descriptor.RuntimeImageMajor)
        Assert.Equal(0us, descriptor.RuntimeImageMinor)
        Assert.Equal(Ilp32, descriptor.RuntimeAbi)

        let expectedFeatures =
            set [ Rx; Tx; Crc8SaeJ1850; Crc16CcittFalse; RxCounter; TxCounter ]

        let featuresMatch = expectedFeatures = descriptor.Features
        Assert.True(featuresMatch)

        Assert.Equal(5us, descriptor.ImageFeatureFlags)

        Assert.Equal(
            "sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947",
            PoolAbi.format descriptor.PoolAbiHash
        )

        Assert.Equal("sha256:26e6f8529af6c840d294a87cb967a490b9cd78394b2c9911fee32681660fe7df", descriptor.ImageSha256)

        Assert.Equal(428u, descriptor.ImageBytes)
        Assert.Equal(28u, descriptor.RuntimeStateBytes)
        Assert.Equal(8u, descriptor.RuntimeScratchBytes)
        Assert.Equal(3u, descriptor.PoolSlots)
        Assert.Equal(canonical, compiled.ActivationJson)
        Assert.Equal(canonical, writeActivationDescriptor descriptor |> unwrap "activation write")

    [<Fact>]
    let ``Activation descriptor parser round trips canonical values and rejects noncanonical structure`` () =
        let parsed = parseActivationDescriptor canonical |> unwrap "activation parse"
        Assert.Equal(canonical, writeActivationDescriptor parsed |> unwrap "activation rewrite")

        [ "{"
          canonical.Replace("  \"runtimeImageMajor\": 1,", "  \"runtimeImageMajor\": 1.0,")
          canonical.Replace("  \"runtimeImageMinor\": 0,", "  \"runtimeImageMinor\": -1,")
          canonical.Replace("  \"format\":", "  \"future\": true,\n  \"format\":")
          canonical.Replace("  \"format\":", "  \"format\": \"sc.activation/v1\",\n  \"format\":")
          canonical.Replace("    \"rx\",", "    \"rx\",\n    \"rx\",")
          canonical.Replace("sha256:26e6f8", "sha256:26E6F8")
          canonical.Replace("  \"poolSlots\": 3\n", "") ]
        |> List.iter (fun json -> parseActivationDescriptor json |> Result.isError |> Assert.True)

    let private projectYaml activation =
        """format: sc.project/v1
name: activation-demo
pool:
  definition: pool.json
wireSources:
  - name: protection
    type: dbc
    path: protection.dbc
binding: binding.json
target: target.json
outputs:
  image: build/demo.scimg
  header: build/demo.h
  inspect: build/demo.inspect.json
"""
        + activation

    let private withTempDirectory action =
        let root =
            Path.Combine(Path.GetTempPath(), "signal-candy-activation-tests-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        try
            action root
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)

    let private prepareProjectRoot root =
        [ "project.yaml"; "pool.json"; "protection.dbc"; "binding.json"; "target.json" ]
        |> List.iter (fun name -> File.WriteAllText(Path.Combine(root, name), "fixture"))

    let private resolveYaml root yaml =
        let path = Path.Combine(root, "project.yaml")
        File.WriteAllText(path, yaml)
        ProjectManifest.parse yaml |> Result.bind (ProjectManifest.resolve path)

    [<Fact>]
    let ``Project activation output is optional extension checked and manifest relative`` () =
        let oldManifest = ProjectManifest.parse (projectYaml "") |> unwrap "old manifest"
        Assert.Equal(None, oldManifest.Outputs.Activation)

        let withActivation =
            ProjectManifest.parse (projectYaml "  activation: build/demo.activation.json\n")
            |> unwrap "activation manifest"

        Assert.Equal(Some "build/demo.activation.json", withActivation.Outputs.Activation)

        [ "  activation: build/demo.json\n"
          "  activation: build/demo.activation.JSON.txt\n" ]
        |> List.iter (fun activation ->
            projectYaml activation |> ProjectManifest.parse |> Result.isError |> Assert.True)

        withTempDirectory (fun root ->
            prepareProjectRoot root

            let resolved =
                resolveYaml root (projectYaml "  activation: build/demo.activation.json\n")
                |> unwrap "activation resolve"

            Assert.Equal(Some(Path.Combine(root, "build", "demo.activation.json")), resolved.Outputs.Activation)
            Assert.False(Directory.Exists(Path.Combine(root, "build"))))

    [<Fact>]
    let ``Project resolution rejects activation input and case only output collisions`` () =
        withTempDirectory (fun root ->
            prepareProjectRoot root
            File.Move(Path.Combine(root, "target.json"), Path.Combine(root, "target.activation.json"))

            projectYaml "  activation: target.activation.json\n"
            |> _.Replace("target: target.json", "target: target.activation.json")
            |> resolveYaml root
            |> Result.isError
            |> Assert.True)

        withTempDirectory (fun root ->
            prepareProjectRoot root

            projectYaml "  activation: build/DEMO.ACTIVATION.JSON\n"
            |> _.Replace("build/demo.inspect.json", "build/demo.activation.json")
            |> resolveYaml root
            |> Result.isError
            |> Assert.True)

    type private CommitStream(commit: byte array -> unit) =
        inherit MemoryStream()

        override this.Dispose(disposing) =
            if disposing then
                commit (this.ToArray())

            base.Dispose(disposing)

    type private FailingMoveFileSystem(failMove: int) =
        let files = Dictionary<string, byte array>(StringComparer.OrdinalIgnoreCase)
        let directories = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let mutable moveCount = 0

        member _.Files = files
        member _.Directories = directories

        interface IArtifactFileSystem with
            member _.FileExists path = files.ContainsKey(path)
            member _.DirectoryExists path = directories.Contains(path)
            member _.CreateDirectory path = directories.Add(path) |> ignore

            member _.CreateNew path =
                new CommitStream(fun bytes -> files.[path] <- bytes) :> Stream

            member _.Move(source, destination) =
                moveCount <- moveCount + 1

                if moveCount = failMove then
                    raise (IOException("injected activation move failure"))

                let bytes = files.[source]
                files.Remove(source) |> ignore
                files.Add(destination, bytes)

            member _.DeleteFile path = files.Remove(path) |> ignore
            member _.DeleteDirectory path = directories.Remove(path) |> ignore

    [<Fact>]
    let ``Activation artifact participates in all or nothing publication`` () =
        let fs = FailingMoveFileSystem(4)

        [ { Kind = Image
            Destination = "out/demo.scimg"
            Content = [| 1uy |] }
          { Kind = Header
            Destination = "out/demo.h"
            Content = [| 2uy |] }
          { Kind = Inspect
            Destination = "out/demo.inspect.json"
            Content = [| 3uy |] }
          { Kind = Activation
            Destination = "out/demo.activation.json"
            Content = Encoding.UTF8.GetBytes(canonical) } ]
        |> writeAtomicWith fs
        |> Result.isError
        |> Assert.True

        Assert.Empty(fs.Files)
        Assert.Empty(fs.Directories)

    [<Fact>]
    let ``Generated project header embeds the exact public activation descriptor`` () =
        let compiled = protectionBuild ()

        let headerBytes: byte array =
            renderActivationHeader "activation-demo" "build/protection_demo.scimg" compiled
            |> unwrap "activation header"

        let header = Encoding.ASCII.GetString(headerBytes)

        Assert.DoesNotContain("\r", header)
        Assert.EndsWith("\n", header)
        Assert.Contains("#include \"signal_candy_runtime.h\"", header)
        Assert.Contains("GSCIMG_PROTECTION_DEMO_RUNTIME_STATE_BYTES 28u", header)
        Assert.Contains("GSCIMG_PROTECTION_DEMO_RUNTIME_SCRATCH_BYTES 8u", header)
        Assert.Contains("GSCIMG_PROTECTION_DEMO_POOL_SLOTS 3u", header)
        Assert.Contains("gScimgProtectionDemoImageSha256[32]", header)
        Assert.Contains("gScimgProtectionDemoPoolAbiSha256[32]", header)

        Assert.Contains(
            "0x26, 0xE6, 0xF8, 0x52, 0x9A, 0xF6, 0xC8, 0x40, 0xD2, 0x94, 0xA8, 0x7C, 0xB9, 0x67, 0xA4, 0x90,",
            header
        )

        Assert.Contains(
            "0x3C, 0xFF, 0x36, 0x84, 0x9F, 0x7B, 0x67, 0xCA, 0xE1, 0xFA, 0x24, 0xA1, 0xEC, 0x67, 0x11, 0x99,",
            header
        )

        Assert.Contains("const sc_activation_descriptor_t gScimgProtectionDemoActivationDescriptor", header)
        Assert.Contains(".descriptor_major = SC_ACTIVATION_DESCRIPTOR_VERSION_MAJOR", header)
        Assert.Contains(".image = gScimgProtectionDemoBytes", header)
        Assert.Contains(".image_size = GSCIMG_PROTECTION_DEMO_BYTE_COUNT", header)
        Assert.Contains(".runtime_abi = SC_RUNTIME_ABI_ILP32", header)
        Assert.Contains(".required_features = UINT32_C(0x00003603)", header)
        Assert.Contains(".runtime_state_bytes = GSCIMG_PROTECTION_DEMO_RUNTIME_STATE_BYTES", header)
        Assert.Contains(".runtime_scratch_bytes = GSCIMG_PROTECTION_DEMO_RUNTIME_SCRATCH_BYTES", header)
        Assert.Contains(".pool_slots = GSCIMG_PROTECTION_DEMO_POOL_SLOTS", header)
