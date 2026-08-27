namespace Signal.CANdy.Core

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Signal.CANdy.Core.ImageDocuments
open Signal.CANdy.Core.PoolAbi
open Signal.CANdy.Core.RuntimeBuild
open Signal.CANdy.Core.RuntimeCapabilities

module RuntimeDiff =

    type DiffInput =
        { BeforeInspect: InspectDocument
          AfterInspect: InspectDocument
          BeforeMap: MapDocument option
          AfterMap: MapDocument option
          BeforeActivation: ActivationDescriptor option
          AfterActivation: ActivationDescriptor option }

    type DiffDocument = { Root: JsonElement }
    type DiffError = DiffError of string

    let private error message = Error[DiffError message]

    let private rootOrder =
        [ "format"; "before"; "after"; "activation"; "resources"; "changes" ]

    let private resourceNames =
        [ "imageBytes"
          "runtimeStateBytes"
          "runtimeScratchBytes"
          "rxMessages"
          "rxPrograms"
          "txMessages"
          "txPrograms"
          "poolSlots"
          "conversions"
          "nestedMuxRecords"
          "muxDepth"
          "qualityEntries"
          "protectionPlans"
          "txCounters"
          "rxCounters"
          "coverageSpans"
          "txTemplateBytes"
          "payloadBytes" ]

    let private entityOrder =
        [ "pool-slot"
          "rx-message"
          "rx-program"
          "tx-message"
          "tx-program"
          "conversion"
          "nested-mux"
          "quality"
          "rx-protection"
          "tx-protection"
          "rx-counter"
          "tx-counter"
          "coverage-span"
          "tx-template" ]

    let private canonicalFrom (write: Utf8JsonWriter -> unit) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        write writer
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n"

    let private hash (document: InspectDocument) =
        document.Root.GetProperty("image").GetProperty("sha256").GetString()

    let private poolHash (map: MapDocument option) =
        map |> Option.map (fun value -> PoolAbi.format value.PoolAbiHash)

    let private scalar (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String -> Some(value.GetString())
        | JsonValueKind.True -> Some "true"
        | JsonValueKind.False -> Some "false"
        | JsonValueKind.Number -> Some(value.GetRawText())
        | _ -> Some(value.GetRawText())

    let private resources (inspect: InspectDocument) (map: MapDocument option) =
        let source =
            match map with
            | Some value -> value.Root.GetProperty("requirements")
            | None -> inspect.Root.GetProperty("resources")

        resourceNames
        |> List.map (fun name ->
            match source.TryGetProperty(name) with
            | true, value -> name, Some(value.GetUInt32())
            | _ -> name, None)

    let private targetLimit (name: string) (limits: RuntimeLimits) =
        match name with
        | "imageBytes" -> limits.MaxImageBytes
        | "runtimeStateBytes" -> limits.MaxRuntimeStateBytes
        | "runtimeScratchBytes" -> limits.MaxRuntimeScratchBytes
        | "rxMessages" -> limits.MaxRxMessages
        | "rxPrograms" -> limits.MaxRxPrograms
        | "txMessages" -> limits.MaxTxMessages
        | "txPrograms" -> limits.MaxTxPrograms
        | "poolSlots" -> limits.MaxPoolSlots
        | "conversions" -> limits.MaxConversions
        | "nestedMuxRecords" -> limits.MaxNestedMuxRecords
        | "muxDepth" -> limits.MaxMuxDepth
        | "qualityEntries" -> limits.MaxQualityEntries
        | "protectionPlans" -> limits.MaxProtectionPlans
        | "txCounters" -> limits.MaxTxCounters
        | "rxCounters" -> limits.MaxRxCounters
        | "coverageSpans" -> limits.MaxCoverageSpans
        | "txTemplateBytes" -> limits.MaxTxTemplateBytes
        | _ -> limits.MaxPayloadBytes

    type private Change =
        { Entity: string
          Key: string
          Kind: string
          Fields: (string * string option * string option) list }

    let private entityChanges
        (entity: string)
        (arrayName: string)
        (ignored: Set<string>)
        (beforeMap: MapDocument)
        (afterMap: MapDocument)
        =
        let items (map: MapDocument) =
            map.Root.GetProperty(arrayName).EnumerateArray()
            |> Seq.map (fun item -> item.GetProperty("key").GetString(), item)
            |> Map.ofSeq

        let before = items beforeMap
        let after = items afterMap

        Set.union (before |> Map.keys |> Set.ofSeq) (after |> Map.keys |> Set.ofSeq)
        |> Set.toList
        |> List.choose (fun key ->
            match Map.tryFind key before, Map.tryFind key after with
            | Some left, Some right ->
                let fields =
                    left.EnumerateObject()
                    |> Seq.filter (fun p -> p.Name <> "key" && not (Set.contains p.Name ignored))
                    |> Seq.choose (fun p ->
                        let first = scalar p.Value
                        let second = right.GetProperty(p.Name) |> scalar
                        if first = second then None else Some(p.Name, first, second))
                    |> Seq.toList

                if fields.IsEmpty then
                    None
                else
                    Some
                        { Entity = entity
                          Key = key
                          Kind = "changed"
                          Fields = fields }
            | Some left, None ->
                let fields =
                    left.EnumerateObject()
                    |> Seq.filter (fun p -> p.Name <> "key" && not (Set.contains p.Name ignored))
                    |> Seq.map (fun p -> p.Name, scalar p.Value, None)
                    |> Seq.toList

                Some
                    { Entity = entity
                      Key = key
                      Kind = "removed"
                      Fields = fields }
            | None, Some right ->
                let fields =
                    right.EnumerateObject()
                    |> Seq.filter (fun p -> p.Name <> "key" && not (Set.contains p.Name ignored))
                    |> Seq.map (fun p -> p.Name, None, scalar p.Value)
                    |> Seq.toList

                Some
                    { Entity = entity
                      Key = key
                      Kind = "added"
                      Fields = fields }
            | _ -> None)

    let private evidenceChecks (input: DiffInput) =
        let check
            (inspect: InspectDocument)
            (map: MapDocument option)
            (activation: ActivationDescriptor option)
            (side: string)
            =
            let expected = hash inspect

            match map with
            | Some value when value.Root.GetProperty("imageSha256").GetString() <> expected ->
                Some(side + " map image hash mismatch")
            | _ ->
                match activation with
                | Some value when value.ImageSha256 <> expected -> Some(side + " activation image hash mismatch")
                | Some value ->
                    match map with
                    | Some mapped when value.PoolAbiHash <> mapped.PoolAbiHash ->
                        Some(side + " activation pool ABI mismatch")
                    | _ -> None
                | None -> None

        [ check input.BeforeInspect input.BeforeMap input.BeforeActivation "before"
          check input.AfterInspect input.AfterMap input.AfterActivation "after" ]
        |> List.choose id

    let diff (input: DiffInput) =
        try
            let evidenceErrors = evidenceChecks input

            if not evidenceErrors.IsEmpty then
                error (String.concat "; " evidenceErrors)
            else
                let beforeHash = hash input.BeforeInspect
                let afterHash = hash input.AfterInspect
                let beforeResources = resources input.BeforeInspect input.BeforeMap
                let afterResources = resources input.AfterInspect input.AfterMap
                let mutable reasons: (string * string option) list = []

                let classToken =
                    if beforeHash = afterHash then
                        "identical"
                    elif
                        poolHash input.BeforeMap <> poolHash input.AfterMap
                        && input.BeforeMap.IsSome
                        && input.AfterMap.IsSome
                    then
                        reasons <- [ "pool-abi-mismatch", None ]
                        "incompatible-pool-abi"
                    else
                        match input.BeforeMap, input.AfterMap with
                        | Some beforeMap, Some afterMap ->
                            let target = beforeMap.Target
                            let req = afterMap.Root.GetProperty("requirements")
                            let major = req.GetProperty("runtimeImageMajor").GetUInt16()
                            let minor = req.GetProperty("runtimeImageMinor").GetUInt16()

                            if target.RuntimeImageMajor <> major || target.RuntimeImageMinor < minor then
                                reasons <- reasons @ [ "runtime-version-unsupported", None ]

                            let requiredTokens =
                                req.GetProperty("features").EnumerateArray()
                                |> Seq.map (fun item -> item.GetString())
                                |> Seq.toList

                            for feature, token in RuntimeCapabilities.featurePairs do
                                if List.contains token requiredTokens && not (target.Features.Contains feature) then
                                    reasons <- reasons @ [ "runtime-feature-unsupported", Some token ]

                            for name, value in afterResources do
                                match value with
                                | Some needed when needed > targetLimit name target.Limits ->
                                    reasons <- reasons @ [ "runtime-resource-limit-exceeded", Some name ]
                                | _ -> ()

                            if reasons.IsEmpty then
                                reasons <- [ "schema-content-changed", None ]
                                "compatible-reset-required"
                            else
                                "incompatible-runtime"
                        | _ ->
                            reasons <- [ "source-map-missing", None ]
                            "unknown-without-map"

                let changes =
                    match input.BeforeMap, input.AfterMap with
                    | Some beforeMap, Some afterMap ->
                        let ignored = Set.ofList [ "imageIndex"; "range"; "symbolRange" ]

                        [ yield! entityChanges "pool-slot" "poolSlots" ignored beforeMap afterMap
                          yield! entityChanges "rx-message" "rxMessages" ignored beforeMap afterMap
                          yield! entityChanges "rx-program" "rxPrograms" ignored beforeMap afterMap
                          yield! entityChanges "tx-message" "txMessages" ignored beforeMap afterMap
                          yield! entityChanges "tx-program" "txPrograms" ignored beforeMap afterMap
                          yield! entityChanges "conversion" "conversions" ignored beforeMap afterMap
                          yield! entityChanges "rx-protection" "rxProtectionPlans" ignored beforeMap afterMap
                          yield! entityChanges "tx-protection" "txProtectionPlans" ignored beforeMap afterMap
                          yield! entityChanges "rx-counter" "rxCounters" ignored beforeMap afterMap
                          yield! entityChanges "tx-counter" "txCounters" ignored beforeMap afterMap
                          yield! entityChanges "coverage-span" "coverageSpans" ignored beforeMap afterMap
                          yield! entityChanges "tx-template" "txTemplates" ignored beforeMap afterMap ]
                        |> List.sortBy (fun c -> List.findIndex ((=) c.Entity) entityOrder, c.Key)
                    | _ -> []

                let json =
                    canonicalFrom (fun writer ->
                        let optionalString (value: string option) =
                            match value with
                            | Some text -> writer.WriteStringValue(text)
                            | None -> writer.WriteNullValue()

                        let optionalNumber (value: uint32 option) =
                            match value with
                            | Some number -> writer.WriteNumberValue(number)
                            | None -> writer.WriteNullValue()

                        let side (name: string) (imageHash: string) (abi: string option) =
                            writer.WritePropertyName(name)
                            writer.WriteStartObject()
                            writer.WriteString("imageSha256", imageHash)
                            writer.WritePropertyName("poolAbiHash")
                            optionalString abi
                            writer.WriteEndObject()

                        writer.WriteStartObject()
                        writer.WriteString("format", "sc.diff/v1")
                        side "before" beforeHash (poolHash input.BeforeMap)
                        side "after" afterHash (poolHash input.AfterMap)
                        writer.WritePropertyName("activation")
                        writer.WriteStartObject()
                        writer.WriteString("class", classToken)
                        writer.WritePropertyName("reasons")
                        writer.WriteStartArray()

                        reasons
                        |> List.iter (fun (token, subject) ->
                            writer.WriteStartObject()
                            writer.WriteString("token", token)
                            writer.WritePropertyName("subject")
                            optionalString subject
                            writer.WriteEndObject())

                        writer.WriteEndArray()
                        writer.WriteEndObject()
                        writer.WritePropertyName("resources")
                        writer.WriteStartArray()

                        List.zip beforeResources afterResources
                        |> List.iter (fun ((name, before), (_, after)) ->
                            writer.WriteStartObject()
                            writer.WriteString("resource", name)
                            writer.WritePropertyName("before")
                            optionalNumber before
                            writer.WritePropertyName("after")
                            optionalNumber after
                            writer.WritePropertyName("delta")

                            match before, after with
                            | Some first, Some second -> writer.WriteNumberValue(int64 second - int64 first)
                            | _ -> writer.WriteNullValue()

                            writer.WriteEndObject())

                        writer.WriteEndArray()
                        writer.WritePropertyName("changes")
                        writer.WriteStartArray()

                        changes
                        |> List.iter (fun change ->
                            writer.WriteStartObject()
                            writer.WriteString("entity", change.Entity)
                            writer.WriteString("key", change.Key)
                            writer.WriteString("change", change.Kind)
                            writer.WritePropertyName("fields")
                            writer.WriteStartArray()

                            change.Fields
                            |> List.iter (fun (name, before, after) ->
                                writer.WriteStartObject()
                                writer.WriteString("field", name)
                                writer.WritePropertyName("before")
                                optionalString before
                                writer.WritePropertyName("after")
                                optionalString after
                                writer.WriteEndObject())

                            writer.WriteEndArray()
                            writer.WriteEndObject())

                        writer.WriteEndArray()
                        writer.WriteEndObject())

                use parsed = JsonDocument.Parse(json)
                Ok { Root = parsed.RootElement.Clone() }
        with ex ->
            error ex.Message

    let writeDiff (document: DiffDocument) =
        try
            Ok(
                canonicalFrom (fun writer ->
                    writer.WriteStartObject()

                    for name in rootOrder do
                        writer.WritePropertyName(name)
                        document.Root.GetProperty(name).WriteTo(writer)

                    writer.WriteEndObject())
            )
        with ex ->
            error ex.Message

    let parseDiff (json: string) =
        try
            use document =
                JsonDocument.Parse(
                    json,
                    JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false)
                )

            let root = document.RootElement

            let rec validate (element: JsonElement) =
                match element.ValueKind with
                | JsonValueKind.Object ->
                    let props = element.EnumerateObject() |> Seq.toList

                    if props.Length <> (props |> List.map _.Name |> List.distinct).Length then
                        raise (FormatException("Duplicate property."))

                    props |> List.iter (fun p -> validate p.Value)
                | JsonValueKind.Array -> element.EnumerateArray() |> Seq.iter validate
                | JsonValueKind.Number ->
                    if not (Regex.IsMatch(element.GetRawText(), "^(0|-?[1-9][0-9]*)$")) then
                        raise (FormatException("Noncanonical number."))
                | JsonValueKind.String ->
                    let value = element.GetString() in

                    if
                        value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        && not (Regex.IsMatch(value, "^sha256:[0-9a-f]{64}$"))
                    then
                        raise (FormatException("Invalid hash."))
                | _ -> ()

            validate root
            let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            if
                names |> List.exists (fun n -> not (List.contains n rootOrder))
                || rootOrder |> List.exists (fun n -> not (List.contains n names))
            then
                raise (FormatException("Invalid root properties."))

            if root.GetProperty("format").GetString() <> "sc.diff/v1" then
                raise (FormatException("Invalid format."))

            let changes = root.GetProperty("changes").EnumerateArray() |> Seq.toList

            let identities =
                changes
                |> List.map (fun c -> c.GetProperty("entity").GetString(), c.GetProperty("key").GetString())

            if identities.Length <> (List.distinct identities |> List.length) then
                raise (FormatException("Duplicate identity."))

            for entity, key in identities do
                if
                    (entity.StartsWith("rx-", StringComparison.Ordinal)
                     && not (key.StartsWith("rx:", StringComparison.Ordinal)))
                    || (entity.StartsWith("tx-", StringComparison.Ordinal)
                        && not (key.StartsWith("tx:", StringComparison.Ordinal)))
                then
                    raise (FormatException("Identity does not match entity."))

            let ranked =
                identities
                |> List.map (fun (entity, key) ->
                    List.tryFindIndex ((=) entity) entityOrder |> Option.defaultValue 999, key)

            if ranked <> List.sort ranked then
                raise (FormatException("Change order is invalid."))

            Ok { Root = root.Clone() }
        with ex ->
            error ex.Message
