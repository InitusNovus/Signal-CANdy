namespace Signal.CANdy.Core

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Signal.CANdy.Core.PoolAbi

module RuntimeCapabilities =

    type RuntimeAbi = Ilp32

    type RuntimeFeature =
        | Rx
        | Tx
        | Multiplexing
        | NestedMux
        | RxQuality
        | CanFd
        | ExtendedCan
        | Motorola
        | Affine
        | Crc8SaeJ1850
        | Crc16CcittFalse
        | CrcDataId
        | RxCounter
        | TxCounter

    type RuntimeLimits =
        { MaxImageBytes: uint32
          MaxRuntimeStateBytes: uint32
          MaxRuntimeScratchBytes: uint32
          MaxRxMessages: uint32
          MaxRxPrograms: uint32
          MaxTxMessages: uint32
          MaxTxPrograms: uint32
          MaxPoolSlots: uint32
          MaxConversions: uint32
          MaxNestedMuxRecords: uint32
          MaxMuxDepth: uint32
          MaxQualityEntries: uint32
          MaxProtectionPlans: uint32
          MaxTxCounters: uint32
          MaxRxCounters: uint32
          MaxCoverageSpans: uint32
          MaxTxTemplateBytes: uint32
          MaxPayloadBytes: uint32 }

    type RuntimeCapabilities =
        { RuntimeImageMajor: uint16
          RuntimeImageMinor: uint16
          RuntimeAbi: RuntimeAbi
          Features: Set<RuntimeFeature>
          PoolAbiHash: PoolAbiHash option
          Limits: RuntimeLimits }

    type CapabilityParseError = CapabilityParseError of string
    type CapabilityError = CapabilityError of string

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

    let featurePairs =
        [ Rx, "rx"
          Tx, "tx"
          Multiplexing, "multiplexing"
          NestedMux, "nested-mux"
          RxQuality, "rx-quality"
          CanFd, "can-fd"
          ExtendedCan, "extended-can"
          Motorola, "motorola"
          Affine, "affine"
          Crc8SaeJ1850, "crc8-sae-j1850"
          Crc16CcittFalse, "crc16-ccitt-false"
          CrcDataId, "crc-data-id"
          RxCounter, "rx-counter"
          TxCounter, "tx-counter" ]

    let featureToken feature =
        featurePairs |> List.find (fst >> (=) feature) |> snd

    let private featureByToken token =
        featurePairs |> List.tryFind (snd >> (=) token) |> Option.map fst

    let private rootKeys =
        [ "format"
          "runtimeImageMajor"
          "runtimeImageMinor"
          "runtimeAbi"
          "features"
          "poolAbiHash"
          "limits" ]

    let private limitKeys =
        [ "maxImageBytes"
          "maxRuntimeStateBytes"
          "maxRuntimeScratchBytes"
          "maxRxMessages"
          "maxRxPrograms"
          "maxTxMessages"
          "maxTxPrograms"
          "maxPoolSlots"
          "maxConversions"
          "maxNestedMuxRecords"
          "maxMuxDepth"
          "maxQualityEntries"
          "maxProtectionPlans"
          "maxTxCounters"
          "maxRxCounters"
          "maxCoverageSpans"
          "maxTxTemplateBytes"
          "maxPayloadBytes" ]

    let private properties context allowed (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error(sprintf "%s must be an object." context)
        else
            let values = element.EnumerateObject() |> Seq.toList

            match values |> List.tryFind (fun p -> not (List.contains p.Name allowed)) with
            | Some p -> Error(sprintf "%s contains unknown key '%s'." context p.Name)
            | None ->
                match values |> List.countBy _.Name |> List.tryFind (snd >> (<) 1) with
                | Some(name, _) -> Error(sprintf "%s contains duplicate key '%s'." context name)
                | None -> Ok values

    let private required name (props: JsonProperty list) =
        match props |> List.tryFind (_.Name >> (=) name) with
        | Some property -> Ok property.Value
        | None -> Error(sprintf "Missing required key '%s'." name)

    let private lexicalUInt maxValue name (element: JsonElement) =
        let raw = element.GetRawText()

        if not (Regex.IsMatch(raw, "^(0|[1-9][0-9]*)$")) then
            Error(sprintf "%s must be a lexical unsigned integer." name)
        else
            match UInt64.TryParse(raw) with
            | true, value when value <= maxValue -> Ok value
            | _ -> Error(sprintf "%s is out of range." name)

    let private parseInternal (json: string) =
        use document =
            JsonDocument.Parse(
                json,
                JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false)
            )

        result {
            let! root = properties "Capability" rootKeys document.RootElement

            for key in
                [ "format"
                  "runtimeImageMajor"
                  "runtimeImageMinor"
                  "runtimeAbi"
                  "features"
                  "limits" ] do
                let! _ = required key root
                ()

            let! format = required "format" root

            if
                format.ValueKind <> JsonValueKind.String
                || format.GetString() <> "sc.runtime-capabilities/v1"
            then
                return! Error "Invalid capability format."

            let! majorElement = required "runtimeImageMajor" root
            let! minorElement = required "runtimeImageMinor" root
            let! major = lexicalUInt 65535UL "runtimeImageMajor" majorElement
            let! minor = lexicalUInt 65535UL "runtimeImageMinor" minorElement
            let! abi = required "runtimeAbi" root

            if abi.ValueKind <> JsonValueKind.String || abi.GetString() <> "ilp32" then
                return! Error "runtimeAbi must be 'ilp32'."

            let! featuresElement = required "features" root

            if featuresElement.ValueKind <> JsonValueKind.Array then
                return! Error "features must be an array."

            let tokens = featuresElement.EnumerateArray() |> Seq.toList
            let mutable parsedFeatures = []

            for item in tokens do
                if item.ValueKind <> JsonValueKind.String then
                    return! Error "Feature tokens must be strings."

                match featureByToken (item.GetString()) with
                | None -> return! Error(sprintf "Unknown feature '%s'." (item.GetRawText()))
                | Some feature -> parsedFeatures <- feature :: parsedFeatures

            if parsedFeatures.Length <> (parsedFeatures |> List.distinct).Length then
                return! Error "Feature tokens must be unique."

            let featureSet = Set.ofList parsedFeatures

            if
                featureSet.Contains NestedMux
                && not (featureSet.Contains Multiplexing && featureSet.Contains RxQuality)
            then
                return! Error "nested-mux requires multiplexing and rx-quality."

            let! hash =
                match root |> List.tryFind (_.Name >> (=) "poolAbiHash") with
                | None -> Ok None
                | Some property when property.Value.ValueKind = JsonValueKind.String ->
                    PoolAbi.parse (property.Value.GetString())
                    |> Result.map Some
                    |> Result.mapError (sprintf "%A")
                | Some _ -> Error "poolAbiHash must be a string."

            let! limitsElement = required "limits" root
            let! limitsProps = properties "Capability limits" limitKeys limitsElement

            let! numbers =
                limitKeys
                |> List.map (fun key ->
                    required key limitsProps
                    |> Result.bind (lexicalUInt (uint64 UInt32.MaxValue) key))
                |> List.fold
                    (fun state item ->
                        state
                        |> Result.bind (fun values -> item |> Result.map (fun value -> values @ [ uint32 value ])))
                    (Ok [])

            let n = List.toArray numbers

            return
                { RuntimeImageMajor = uint16 major
                  RuntimeImageMinor = uint16 minor
                  RuntimeAbi = Ilp32
                  Features = featureSet
                  PoolAbiHash = hash
                  Limits =
                    { MaxImageBytes = n.[0]
                      MaxRuntimeStateBytes = n.[1]
                      MaxRuntimeScratchBytes = n.[2]
                      MaxRxMessages = n.[3]
                      MaxRxPrograms = n.[4]
                      MaxTxMessages = n.[5]
                      MaxTxPrograms = n.[6]
                      MaxPoolSlots = n.[7]
                      MaxConversions = n.[8]
                      MaxNestedMuxRecords = n.[9]
                      MaxMuxDepth = n.[10]
                      MaxQualityEntries = n.[11]
                      MaxProtectionPlans = n.[12]
                      MaxTxCounters = n.[13]
                      MaxRxCounters = n.[14]
                      MaxCoverageSpans = n.[15]
                      MaxTxTemplateBytes = n.[16]
                      MaxPayloadBytes = n.[17] } }
        }

    let parse json =
        try
            parseInternal json |> Result.mapError (CapabilityParseError >> List.singleton)
        with ex ->
            Error[CapabilityParseError ex.Message]

    let validate capability =
        if
            capability.Features.Contains NestedMux
            && not (
                capability.Features.Contains Multiplexing
                && capability.Features.Contains RxQuality
            )
        then
            Error[CapabilityError "nested-mux requires multiplexing and rx-quality."]
        else
            Ok capability

    let writeCanonical capability =
        validate capability
        |> Result.map (fun value ->
            let l = value.Limits

            let lines =
                [ "{"
                  "  \"format\": \"sc.runtime-capabilities/v1\","
                  sprintf "  \"runtimeImageMajor\": %d," value.RuntimeImageMajor
                  sprintf "  \"runtimeImageMinor\": %d," value.RuntimeImageMinor
                  "  \"runtimeAbi\": \"ilp32\","
                  "  \"features\": [" ]

            let tokens =
                featurePairs
                |> List.choose (fun (feature, token) -> if value.Features.Contains feature then Some token else None)

            let featureLines =
                tokens
                |> List.mapi (fun index token ->
                    sprintf "    \"%s\"%s" token (if index + 1 < tokens.Length then "," else ""))

            let afterFeatures =
                [ "  ]," ]
                @ (value.PoolAbiHash
                   |> Option.map (PoolAbi.format >> sprintf "  \"poolAbiHash\": \"%s\",")
                   |> Option.toList)
                @ [ "  \"limits\": {"
                    sprintf "    \"maxImageBytes\": %u," l.MaxImageBytes
                    sprintf "    \"maxRuntimeStateBytes\": %u," l.MaxRuntimeStateBytes
                    sprintf "    \"maxRuntimeScratchBytes\": %u," l.MaxRuntimeScratchBytes
                    sprintf "    \"maxRxMessages\": %u," l.MaxRxMessages
                    sprintf "    \"maxRxPrograms\": %u," l.MaxRxPrograms
                    sprintf "    \"maxTxMessages\": %u," l.MaxTxMessages
                    sprintf "    \"maxTxPrograms\": %u," l.MaxTxPrograms
                    sprintf "    \"maxPoolSlots\": %u," l.MaxPoolSlots
                    sprintf "    \"maxConversions\": %u," l.MaxConversions
                    sprintf "    \"maxNestedMuxRecords\": %u," l.MaxNestedMuxRecords
                    sprintf "    \"maxMuxDepth\": %u," l.MaxMuxDepth
                    sprintf "    \"maxQualityEntries\": %u," l.MaxQualityEntries
                    sprintf "    \"maxProtectionPlans\": %u," l.MaxProtectionPlans
                    sprintf "    \"maxTxCounters\": %u," l.MaxTxCounters
                    sprintf "    \"maxRxCounters\": %u," l.MaxRxCounters
                    sprintf "    \"maxCoverageSpans\": %u," l.MaxCoverageSpans
                    sprintf "    \"maxTxTemplateBytes\": %u," l.MaxTxTemplateBytes
                    sprintf "    \"maxPayloadBytes\": %u" l.MaxPayloadBytes
                    "  }"
                    "}" ]

            String.concat "\n" (lines @ featureLines @ afterFeatures) + "\n")
