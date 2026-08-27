namespace Signal.CANdy.Core.Tests

open System
open System.IO
open System.Security.Cryptography
open Xunit
open Signal.CANdy.Core
open Signal.CANdy.Core.PoolAbi
open Signal.CANdy.Core.RuntimeBuild
open Signal.CANdy.Core.RuntimeCapabilities
open Signal.CANdy.Core.RuntimeRequirements

module RuntimeBuildTests =

    let private repoRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private fixturePath name =
        Path.Combine(repoRoot, "examples", "scimg_protection_demo", name)

    let private unwrap description result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "%s failed: %A" description errors

    let private sha256 (bytes: byte array) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private protectionInputs () =
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

        { Pool = pool
          Wires = [ "protection", wire ]
          Bindings = bindings }

    let private protectionBuild () =
        protectionInputs () |> compile |> unwrap "runtime compile"

    let private directBuild () =
        let inputs = protectionInputs ()
        let wire = inputs.Wires.Head |> snd
        let linked = Linked.link inputs.Pool wire inputs.Bindings |> unwrap "direct link"
        let image = Scimg.lower linked |> unwrap "direct lower"
        let bytes = Scimg.write image |> unwrap "direct write"
        let inspection = Scimg.inspect bytes |> unwrap "direct inspect"
        bytes, inspection

    let private allFeatures =
        set
            [ Rx
              Tx
              Multiplexing
              NestedMux
              RxQuality
              CanFd
              ExtendedCan
              Motorola
              Affine
              Crc8SaeJ1850
              Crc16CcittFalse
              CrcDataId
              RxCounter
              TxCounter ]

    let private targetFor (requirements: RuntimeRequirements) =
        { RuntimeImageMajor = requirements.RuntimeImageMajor
          RuntimeImageMinor = requirements.RuntimeImageMinor
          RuntimeAbi = Ilp32
          Features = allFeatures
          PoolAbiHash = Some requirements.PoolAbiHash
          Limits =
            { MaxImageBytes = requirements.ImageBytes
              MaxRuntimeStateBytes = requirements.RuntimeStateBytes
              MaxRuntimeScratchBytes = requirements.RuntimeScratchBytes
              MaxRxMessages = requirements.RxMessages
              MaxRxPrograms = requirements.RxPrograms
              MaxTxMessages = requirements.TxMessages
              MaxTxPrograms = requirements.TxPrograms
              MaxPoolSlots = requirements.PoolSlots
              MaxConversions = requirements.Conversions
              MaxNestedMuxRecords = requirements.NestedMuxRecords
              MaxMuxDepth = requirements.MuxDepth
              MaxQualityEntries = requirements.QualityEntries
              MaxProtectionPlans = requirements.ProtectionPlans
              MaxTxCounters = requirements.TxCounters
              MaxRxCounters = requirements.RxCounters
              MaxCoverageSpans = requirements.CoverageSpans
              MaxTxTemplateBytes = requirements.TxTemplateBytes
              MaxPayloadBytes = requirements.PayloadBytes } }

    [<Fact>]
    let ``Shared runtime compiler freezes protection bytes hash and requirements`` () =
        let compiled = protectionBuild ()
        Assert.Equal(428, compiled.ImageBytes.Length)
        Assert.Equal("26e6f8529af6c840d294a87cb967a490b9cd78394b2c9911fee32681660fe7df", sha256 compiled.ImageBytes)

        Assert.Equal(
            "9b5fe2f0f050456afe339b33286446fec980416dc83534959193d1deb4fca434",
            sha256 (Text.Encoding.UTF8.GetBytes compiled.InspectJson)
        )

        Assert.Equal(
            "sha256:3cff36849f7b67cae1fa24a1ec6711993e1a4e2c477e613f3701fa41e005e947",
            PoolAbi.format compiled.PoolAbiHash
        )

        Assert.Equal(1us, compiled.Requirements.RuntimeImageMajor)
        Assert.Equal(0us, compiled.Requirements.RuntimeImageMinor)
        Assert.Equal(428u, compiled.Requirements.ImageBytes)
        Assert.Equal(28u, compiled.Requirements.RuntimeStateBytes)
        Assert.Equal(8u, compiled.Requirements.RuntimeScratchBytes)
        Assert.Equal(1u, compiled.Requirements.RxMessages)
        Assert.Equal(1u, compiled.Requirements.RxPrograms)
        Assert.Equal(1u, compiled.Requirements.TxMessages)
        Assert.Equal(2u, compiled.Requirements.TxPrograms)
        Assert.Equal(3u, compiled.Requirements.PoolSlots)
        Assert.Equal(1u, compiled.Requirements.Conversions)
        Assert.Equal(0u, compiled.Requirements.NestedMuxRecords)
        Assert.Equal(0u, compiled.Requirements.MuxDepth)
        Assert.Equal(0u, compiled.Requirements.QualityEntries)
        Assert.Equal(2u, compiled.Requirements.ProtectionPlans)
        Assert.Equal(1u, compiled.Requirements.TxCounters)
        Assert.Equal(1u, compiled.Requirements.RxCounters)
        Assert.Equal(2u, compiled.Requirements.CoverageSpans)
        Assert.Equal(8u, compiled.Requirements.TxTemplateBytes)
        Assert.Equal(8u, compiled.Requirements.PayloadBytes)

        let expectedFeatures =
            set [ Rx; Tx; Crc8SaeJ1850; Crc16CcittFalse; RxCounter; TxCounter ]

        let featuresMatch = expectedFeatures = compiled.Requirements.Features
        Assert.True(featuresMatch)

    [<Fact>]
    let ``Resolved project loading compiles and validates entirely in memory`` () =
        let root =
            Path.Combine(Path.GetTempPath(), "signal-candy-load-project-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        try
            [ "protection_demo.dbc"; "pool.json"; "binding.json" ]
            |> List.iter (fun name -> File.Copy(fixturePath name, Path.Combine(root, name)))

            let target = protectionBuild () |> _.Requirements |> targetFor

            File.WriteAllText(Path.Combine(root, "target.json"), writeCanonical target |> unwrap "capability write")

            let yaml =
                """format: sc.project/v1
name: load-project
pool:
  definition: pool.json
wireSources:
  - name: protection
    type: dbc
    path: protection_demo.dbc
binding: binding.json
target: target.json
outputs:
  image: build/image.scimg
"""

            let manifestPath = Path.Combine(root, "project.yaml")
            File.WriteAllText(manifestPath, yaml)

            let resolved =
                ProjectManifest.parse yaml
                |> unwrap "manifest parse"
                |> ProjectManifest.resolve manifestPath
                |> unwrap "manifest resolve"

            let loaded, loadedTarget = loadAndCompile resolved |> unwrap "project load"
            Assert.Equal(428, loaded.ImageBytes.Length)
            Assert.Equal(target, loadedTarget)
            Assert.False(Directory.Exists(Path.Combine(root, "build")))
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``Shared compiler is byte identical to the direct linker lowerer writer path`` () =
        let compiled = protectionBuild ()
        let directBytes, directInspect = directBuild ()
        Assert.Equal<byte>(directBytes, compiled.ImageBytes)
        Assert.Equal(directInspect, compiled.InspectJson)

    [<Fact>]
    let ``Runtime compiler rejects duplicate source message names and CAN keys`` () =
        let inputs = protectionInputs ()
        let first = inputs.Wires.Head |> snd

        let duplicateName =
            { first with
                Messages =
                    [ { first.Messages.Head with
                          CanId = first.Messages.Head.CanId + 1u } ] }

        let duplicateCan =
            { first with
                Messages =
                    [ { first.Messages.Head with
                          Name = first.Messages.Head.Name + "_other" } ] }

        compile
            { inputs with
                Wires = [ "first", first; "second", duplicateName ] }
        |> Result.isError
        |> Assert.True

        compile
            { inputs with
                Wires = [ "first", first; "second", duplicateCan ] }
        |> Result.isError
        |> Assert.True

    [<Fact>]
    let ``Target feature matrix reports each missing semantic feature`` () =
        let compiled = protectionBuild ()

        let synthetic =
            { compiled with
                Requirements =
                    { compiled.Requirements with
                        Features = allFeatures } }

        let target = targetFor synthetic.Requirements

        allFeatures
        |> Set.iter (fun feature ->
            let insufficient =
                { target with
                    Features = Set.remove feature target.Features }

            match validateTarget insufficient synthetic with
            | Error errors ->
                Assert.Contains(
                    errors,
                    fun error ->
                        match error with
                        | MissingRuntimeFeature actual -> actual = feature
                        | _ -> false
                )
            | Ok _ -> Assert.Fail(sprintf "Feature %A unexpectedly passed" feature))

    [<Fact>]
    let ``All eighteen target limits pass at equality and fail one below with exact resource`` () =
        let compiled = protectionBuild ()

        let requirements =
            { compiled.Requirements with
                ImageBytes = 1u
                RuntimeStateBytes = 1u
                RuntimeScratchBytes = 1u
                RxMessages = 1u
                RxPrograms = 1u
                TxMessages = 1u
                TxPrograms = 1u
                PoolSlots = 1u
                Conversions = 1u
                NestedMuxRecords = 1u
                MuxDepth = 1u
                QualityEntries = 1u
                ProtectionPlans = 1u
                TxCounters = 1u
                RxCounters = 1u
                CoverageSpans = 1u
                TxTemplateBytes = 1u
                PayloadBytes = 1u }

        let synthetic =
            { compiled with
                Requirements = requirements }

        let exact = targetFor requirements
        validateTarget exact synthetic |> Result.isOk |> Assert.True

        let cases =
            [ ImageBytes, fun limits -> { limits with MaxImageBytes = 0u }
              RuntimeStateBytes,
              fun limits ->
                  { limits with
                      MaxRuntimeStateBytes = 0u }
              RuntimeScratchBytes,
              fun limits ->
                  { limits with
                      MaxRuntimeScratchBytes = 0u }
              RxMessages, fun limits -> { limits with MaxRxMessages = 0u }
              RxPrograms, fun limits -> { limits with MaxRxPrograms = 0u }
              TxMessages, fun limits -> { limits with MaxTxMessages = 0u }
              TxPrograms, fun limits -> { limits with MaxTxPrograms = 0u }
              PoolSlots, fun limits -> { limits with MaxPoolSlots = 0u }
              Conversions, fun limits -> { limits with MaxConversions = 0u }
              NestedMuxRecords, fun limits -> { limits with MaxNestedMuxRecords = 0u }
              MuxDepth, fun limits -> { limits with MaxMuxDepth = 0u }
              QualityEntries, fun limits -> { limits with MaxQualityEntries = 0u }
              ProtectionPlans, fun limits -> { limits with MaxProtectionPlans = 0u }
              TxCounters, fun limits -> { limits with MaxTxCounters = 0u }
              RxCounters, fun limits -> { limits with MaxRxCounters = 0u }
              CoverageSpans, fun limits -> { limits with MaxCoverageSpans = 0u }
              TxTemplateBytes, fun limits -> { limits with MaxTxTemplateBytes = 0u }
              PayloadBytes, fun limits -> { limits with MaxPayloadBytes = 0u } ]

        cases
        |> List.iter (fun (resource, lower) ->
            let insufficient =
                { exact with
                    Limits = lower exact.Limits }

            match validateTarget insufficient synthetic with
            | Error errors ->
                Assert.Contains(
                    errors,
                    fun error ->
                        match error with
                        | RuntimeLimitExceeded(actual, required, supported) ->
                            actual = resource && required = 1u && supported = 0u
                        | _ -> false
                )
            | Ok _ -> Assert.Fail(sprintf "Resource %A unexpectedly passed" resource))

    [<Fact>]
    let ``Target version compatibility uses exact major and minimum minor`` () =
        let compiled = protectionBuild ()
        let target = targetFor compiled.Requirements
        validateTarget target compiled |> Result.isOk |> Assert.True

        validateTarget { target with RuntimeImageMinor = 1us } compiled
        |> Result.isOk
        |> Assert.True

        validateTarget { target with RuntimeImageMajor = 0us } compiled
        |> Result.isError
        |> Assert.True

        validateTarget { target with RuntimeImageMajor = 2us } compiled
        |> Result.isError
        |> Assert.True

        let future =
            { compiled with
                Requirements =
                    { compiled.Requirements with
                        RuntimeImageMinor = 1us } }

        validateTarget target future |> Result.isError |> Assert.True

    [<Fact>]
    let ``Target hash omission and equality pass while mismatch fails`` () =
        let compiled = protectionBuild ()
        let exact = targetFor compiled.Requirements
        validateTarget exact compiled |> Result.isOk |> Assert.True

        validateTarget { exact with PoolAbiHash = None } compiled
        |> Result.isOk
        |> Assert.True

        let mismatch =
            PoolAbi.parse "sha256:0000000000000000000000000000000000000000000000000000000000000000"
            |> unwrap "hash parse"

        validateTarget
            { exact with
                PoolAbiHash = Some mismatch }
            compiled
        |> Result.isError
        |> Assert.True
