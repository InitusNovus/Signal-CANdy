namespace Signal.CANdy.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.ImageDocuments
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.PoolAbi
open Signal.CANdy.Core.ProjectManifest
open Signal.CANdy.Core.RuntimeCapabilities
open Signal.CANdy.Core.RuntimeRequirements
open Signal.CANdy.Core.Scimg
open Signal.CANdy.Core.Wire

module RuntimeBuild =

    type RuntimeBuildInputs =
        { Pool: PoolContract
          Wires: (string * WireIr) list
          Bindings: BindingSet }

    type ActivationDescriptor =
        { RuntimeImageMajor: uint16
          RuntimeImageMinor: uint16
          RuntimeAbi: RuntimeAbi
          Features: Set<RuntimeFeature>
          ImageFeatureFlags: uint16
          PoolAbiHash: PoolAbiHash
          ImageSha256: string
          ImageBytes: uint32
          RuntimeStateBytes: uint32
          RuntimeScratchBytes: uint32
          PoolSlots: uint32 }

    type CompiledRuntime =
        { Pool: PoolContract
          Linked: LinkedSchema
          Image: RuntimeImage
          ImageBytes: byte array
          InspectJson: string
          PoolAbiHash: PoolAbiHash
          Requirements: RuntimeRequirements
          Activation: ActivationDescriptor
          ActivationJson: string
          MapDocument: MapDocument option
          MapJson: string option }

    type RuntimeBuildError = RuntimeBuildError of string

    let private activationError message = Error[RuntimeBuildError message]

    let writeActivationDescriptor (descriptor: ActivationDescriptor) =
        if not (Regex.IsMatch(descriptor.ImageSha256, "^sha256:[0-9a-f]{64}$")) then
            activationError "Activation image hash is invalid."
        else
            let tokens =
                RuntimeCapabilities.featurePairs
                |> List.choose (fun (feature, token) ->
                    if descriptor.Features.Contains feature then
                        Some token
                    else
                        None)

            let featureLines =
                tokens
                |> List.mapi (fun index token ->
                    sprintf "    \"%s\"%s" token (if index + 1 < tokens.Length then "," else ""))

            [ "{"
              "  \"format\": \"sc.activation/v1\","
              sprintf "  \"runtimeImageMajor\": %d," descriptor.RuntimeImageMajor
              sprintf "  \"runtimeImageMinor\": %d," descriptor.RuntimeImageMinor
              "  \"runtimeAbi\": \"ilp32\","
              "  \"features\": ["
              yield! featureLines
              "  ],"
              sprintf "  \"imageFeatureFlags\": %d," descriptor.ImageFeatureFlags
              sprintf "  \"poolAbiHash\": \"%s\"," (PoolAbi.format descriptor.PoolAbiHash)
              sprintf "  \"imageSha256\": \"%s\"," descriptor.ImageSha256
              sprintf "  \"imageBytes\": %u," descriptor.ImageBytes
              sprintf "  \"runtimeStateBytes\": %u," descriptor.RuntimeStateBytes
              sprintf "  \"runtimeScratchBytes\": %u," descriptor.RuntimeScratchBytes
              sprintf "  \"poolSlots\": %u" descriptor.PoolSlots
              "}" ]
            |> String.concat "\n"
            |> fun text -> Ok(text + "\n")

    let private activationKeys =
        [ "format"
          "runtimeImageMajor"
          "runtimeImageMinor"
          "runtimeAbi"
          "features"
          "imageFeatureFlags"
          "poolAbiHash"
          "imageSha256"
          "imageBytes"
          "runtimeStateBytes"
          "runtimeScratchBytes"
          "poolSlots" ]

    let parseActivationDescriptor (json: string) =
        try
            use document =
                JsonDocument.Parse(
                    json,
                    JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false)
                )

            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                activationError "Activation descriptor must be an object."
            else
                let properties = root.EnumerateObject() |> Seq.toList
                let names = properties |> List.map _.Name

                if names |> List.exists (fun name -> not (List.contains name activationKeys)) then
                    activationError "Activation descriptor contains an unknown key."
                elif names.Length <> (names |> List.distinct).Length then
                    activationError "Activation descriptor contains a duplicate key."
                elif activationKeys |> List.exists (fun key -> not (List.contains key names)) then
                    activationError "Activation descriptor is missing a required key."
                else
                    let value (name: string) : JsonElement =
                        properties |> List.find (_.Name >> (=) name) |> _.Value

                    let uintValue (maxValue: uint64) (name: string) =
                        let element = value name
                        let raw = element.GetRawText()

                        if not (Regex.IsMatch(raw, "^(0|[1-9][0-9]*)$")) then
                            raise (FormatException(name + " must be a lexical unsigned integer."))

                        let parsed = UInt64.Parse(raw)

                        if parsed > maxValue then
                            raise (OverflowException(name))

                        parsed

                    let stringValue (name: string) : string =
                        let element = value name

                        if element.ValueKind <> JsonValueKind.String then
                            raise (FormatException(name))

                        element.GetString()

                    if stringValue "format" <> "sc.activation/v1" then
                        raise (FormatException("Invalid activation format."))

                    if stringValue "runtimeAbi" <> "ilp32" then
                        raise (FormatException("Invalid runtime ABI."))

                    let featuresElement = value "features"

                    if featuresElement.ValueKind <> JsonValueKind.Array then
                        raise (FormatException("features"))

                    let tokens =
                        featuresElement.EnumerateArray()
                        |> Seq.map (fun item ->
                            if item.ValueKind <> JsonValueKind.String then
                                raise (FormatException("features"))

                            item.GetString())
                        |> Seq.toList

                    if tokens.Length <> (tokens |> List.distinct).Length then
                        raise (FormatException("Feature tokens must be unique."))

                    let features =
                        tokens
                        |> List.map (fun token ->
                            RuntimeCapabilities.featurePairs
                            |> List.tryFind (snd >> (=) token)
                            |> Option.map fst
                            |> Option.defaultWith (fun () -> raise (FormatException("Unknown feature."))))
                        |> Set.ofList

                    let poolHash =
                        PoolAbi.parse (stringValue "poolAbiHash")
                        |> Result.defaultWith (sprintf "%A" >> FormatException >> raise)

                    let imageHash = stringValue "imageSha256"

                    if not (Regex.IsMatch(imageHash, "^sha256:[0-9a-f]{64}$")) then
                        raise (FormatException("Invalid image hash."))

                    Ok
                        { RuntimeImageMajor = uint16 (uintValue 65535UL "runtimeImageMajor")
                          RuntimeImageMinor = uint16 (uintValue 65535UL "runtimeImageMinor")
                          RuntimeAbi = Ilp32
                          Features = features
                          ImageFeatureFlags = uint16 (uintValue 65535UL "imageFeatureFlags")
                          PoolAbiHash = poolHash
                          ImageSha256 = imageHash
                          ImageBytes = uint32 (uintValue (uint64 UInt32.MaxValue) "imageBytes")
                          RuntimeStateBytes = uint32 (uintValue (uint64 UInt32.MaxValue) "runtimeStateBytes")
                          RuntimeScratchBytes = uint32 (uintValue (uint64 UInt32.MaxValue) "runtimeScratchBytes")
                          PoolSlots = uint32 (uintValue (uint64 UInt32.MaxValue) "poolSlots") }
        with ex ->
            activationError ex.Message

    type RuntimeResource =
        | ImageBytes
        | RuntimeStateBytes
        | RuntimeScratchBytes
        | RxMessages
        | RxPrograms
        | TxMessages
        | TxPrograms
        | PoolSlots
        | Conversions
        | NestedMuxRecords
        | MuxDepth
        | QualityEntries
        | ProtectionPlans
        | TxCounters
        | RxCounters
        | CoverageSpans
        | TxTemplateBytes
        | PayloadBytes

    type CapabilityMismatch =
        | RuntimeVersionMismatch of uint16 * uint16 * uint16 * uint16
        | MissingRuntimeFeature of RuntimeFeature
        | RuntimeLimitExceeded of RuntimeResource * uint32 * uint32
        | PoolAbiMismatch of string * string

    type private ResultBuilder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return value = Ok value
        member _.ReturnFrom value = value
        member _.Zero() = Ok()
        member _.Delay generator = generator
        member _.Run generator = generator ()
        member _.Combine(first, second) = Result.bind (fun () -> second ()) first

        member _.For(values, body) =
            values
            |> Seq.fold (fun state value -> Result.bind (fun () -> body value) state) (Ok())

    let private result = ResultBuilder()

    type ProjectBuildError =
        | InputIo of string * string
        | InputValidation of string
        | RuntimeFailure of RuntimeBuildError
        | TargetMismatch of CapabilityMismatch

    let private buildError errors =
        errors |> List.map (sprintf "%A" >> RuntimeBuildError)

    let compile (inputs: RuntimeBuildInputs) =
        result {
            let! wire = Wire.merge inputs.Wires |> Result.mapError buildError
            let! linked = Linked.link inputs.Pool wire inputs.Bindings |> Result.mapError buildError
            let! image = Scimg.lower linked |> Result.mapError buildError
            let! bytes = Scimg.write image |> Result.mapError buildError
            let! inspection = Scimg.inspect bytes |> Result.mapError buildError
            let! hash = PoolAbi.compute inputs.Pool |> Result.mapError buildError

            let! requirements =
                RuntimeRequirements.derive inputs.Pool linked image bytes
                |> Result.mapError buildError

            let activation =
                { RuntimeImageMajor = requirements.RuntimeImageMajor
                  RuntimeImageMinor = requirements.RuntimeImageMinor
                  RuntimeAbi = Ilp32
                  Features = requirements.Features
                  ImageFeatureFlags = uint16 bytes.[10] ||| (uint16 bytes.[11] <<< 8)
                  PoolAbiHash = hash
                  ImageSha256 =
                    "sha256:"
                    + (SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant())
                  ImageBytes = requirements.ImageBytes
                  RuntimeStateBytes = requirements.RuntimeStateBytes
                  RuntimeScratchBytes = requirements.RuntimeScratchBytes
                  PoolSlots = requirements.PoolSlots }

            let! activationJson = writeActivationDescriptor activation

            return
                { Pool = inputs.Pool
                  Linked = linked
                  Image = image
                  ImageBytes = bytes
                  InspectJson = inspection
                  PoolAbiHash = hash
                  Requirements = requirements
                  Activation = activation
                  ActivationJson = activationJson
                  MapDocument = None
                  MapJson = None }
        }

    let private limitCases (limits: RuntimeLimits) (required: RuntimeRequirements) =
        [ ImageBytes, required.ImageBytes, limits.MaxImageBytes
          RuntimeStateBytes, required.RuntimeStateBytes, limits.MaxRuntimeStateBytes
          RuntimeScratchBytes, required.RuntimeScratchBytes, limits.MaxRuntimeScratchBytes
          RxMessages, required.RxMessages, limits.MaxRxMessages
          RxPrograms, required.RxPrograms, limits.MaxRxPrograms
          TxMessages, required.TxMessages, limits.MaxTxMessages
          TxPrograms, required.TxPrograms, limits.MaxTxPrograms
          PoolSlots, required.PoolSlots, limits.MaxPoolSlots
          Conversions, required.Conversions, limits.MaxConversions
          NestedMuxRecords, required.NestedMuxRecords, limits.MaxNestedMuxRecords
          MuxDepth, required.MuxDepth, limits.MaxMuxDepth
          QualityEntries, required.QualityEntries, limits.MaxQualityEntries
          ProtectionPlans, required.ProtectionPlans, limits.MaxProtectionPlans
          TxCounters, required.TxCounters, limits.MaxTxCounters
          RxCounters, required.RxCounters, limits.MaxRxCounters
          CoverageSpans, required.CoverageSpans, limits.MaxCoverageSpans
          TxTemplateBytes, required.TxTemplateBytes, limits.MaxTxTemplateBytes
          PayloadBytes, required.PayloadBytes, limits.MaxPayloadBytes ]

    let validateTarget (target: RuntimeCapabilities) (compiled: CompiledRuntime) =
        let required = compiled.Requirements
        let errors = ResizeArray<CapabilityMismatch>()

        if
            target.RuntimeImageMajor <> required.RuntimeImageMajor
            || target.RuntimeImageMinor < required.RuntimeImageMinor
        then
            errors.Add(
                RuntimeVersionMismatch(
                    required.RuntimeImageMajor,
                    required.RuntimeImageMinor,
                    target.RuntimeImageMajor,
                    target.RuntimeImageMinor
                )
            )

        for feature, _ in RuntimeCapabilities.featurePairs do
            if required.Features.Contains feature && not (target.Features.Contains feature) then
                errors.Add(MissingRuntimeFeature feature)

        for resource, needed, supported in limitCases target.Limits required do
            if needed > supported then
                errors.Add(RuntimeLimitExceeded(resource, needed, supported))

        match target.PoolAbiHash with
        | Some supported when supported <> compiled.PoolAbiHash ->
            errors.Add(PoolAbiMismatch(PoolAbi.format compiled.PoolAbiHash, PoolAbi.format supported))
        | _ -> ()

        if errors.Count = 0 then
            Ok compiled
        else
            Error(List.ofSeq errors)

    let loadAndCompile (project: ResolvedProject) =
        let read source path =
            try
                Ok(File.ReadAllText(path))
            with ex ->
                Error[InputIo(source, ex.Message)]

        result {
            let poolPath =
                match project.Pool with
                | ResolvedDefinition p
                | ResolvedManifest p -> p

            let! poolText = read "pool" poolPath

            let! pool =
                Pool.parsePoolDefinition poolText
                |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])

            let mutable wires = []

            for source in project.WireSources do
                let! ir =
                    Dbc.parseDbcFile source.Path
                    |> Result.mapError (fun error -> [ InputValidation(sprintf "%A" error) ])

                let! wire =
                    Wire.toWireModel ir
                    |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])

                wires <- wires @ [ source.Name, wire ]

            let! bindingText = read "binding" project.Binding

            let! bindings =
                Binding.parseBindingSet bindingText
                |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])

            let! targetText = read "target" project.Target

            let! target =
                RuntimeCapabilities.parse targetText
                |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])

            let! compiled =
                compile
                    { Pool = pool
                      Wires = wires
                      Bindings = bindings }
                |> Result.mapError (List.map RuntimeFailure)

            let! valid = validateTarget target compiled |> Result.mapError (List.map TargetMismatch)

            let! mapped =
                match project.Outputs.Map with
                | None -> Ok valid
                | Some _ ->
                    let layout =
                        Scimg.readDetailed valid.ImageBytes
                        |> Result.map _.Layout
                        |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])

                    layout
                    |> Result.bind (fun imageLayout ->
                        let sources =
                            (project.WireSources, wires)
                            ||> List.map2 (fun source (_, wire) ->
                                { Key = source.Name
                                  Path = Path.GetRelativePath(project.RootDirectory, source.Path).Replace('\\', '/')
                                  Wire = wire })

                        ImageDocuments.createMap
                            pool
                            valid.Linked
                            valid.Image
                            valid.ImageBytes
                            imageLayout
                            valid.PoolAbiHash
                            valid.Requirements
                            target
                            sources
                        |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])
                        |> Result.bind (fun mapDocument ->
                            ImageDocuments.writeMap mapDocument
                            |> Result.mapError (fun errors -> [ InputValidation(sprintf "%A" errors) ])
                            |> Result.map (fun mapJson ->
                                { valid with
                                    MapDocument = Some mapDocument
                                    MapJson = Some mapJson })))

            return mapped, target
        }
