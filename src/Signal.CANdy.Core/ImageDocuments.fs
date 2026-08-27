namespace Signal.CANdy.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.PoolAbi
open Signal.CANdy.Core.RuntimeCapabilities
open Signal.CANdy.Core.RuntimeRequirements
open Signal.CANdy.Core.Scimg
open Signal.CANdy.Core.Wire

module ImageDocuments =

    type DocumentError = DocumentError of string

    type InspectDocument = { Root: JsonElement }

    type MapDocument =
        { Root: JsonElement
          PoolAbiHash: PoolAbiHash
          Target: RuntimeCapabilities }

    type MapSource =
        { Key: string
          Path: string
          Wire: WireIr }

    let private error message = Error[DocumentError message]
    let private jsonOptions = JsonWriterOptions(Indented = true)

    let private shaPattern =
        Regex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private floatPattern =
        Regex("^f64:[0-9a-f]{16}$", RegexOptions.CultureInvariant)

    let private uintPattern = Regex("^(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)
    let private intPattern = Regex("^(0|-?[1-9][0-9]*)$", RegexOptions.CultureInvariant)

    let private canonicalFrom (write: Utf8JsonWriter -> unit) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, jsonOptions)
        write writer
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n"

    let private cloneRoot (json: string) =
        use document = JsonDocument.Parse(json)
        document.RootElement.Clone()

    let private range (writer: Utf8JsonWriter) (value: ImageRange) =
        writer.WriteStartObject()
        writer.WriteNumber("start", value.Start)
        writer.WriteNumber("end", value.End)
        writer.WriteEndObject()

    let private optionalRange (writer: Utf8JsonWriter) (name: string) value =
        writer.WritePropertyName(name)

        match value with
        | Some item -> range writer item
        | None -> writer.WriteNullValue()

    let private f64 value =
        sprintf "f64:%016x" (uint64 (BitConverter.DoubleToInt64Bits value))

    let private storageToken =
        function
        | 0uy -> "u8"
        | 1uy -> "u16"
        | 2uy -> "u32"
        | 3uy -> "u64"
        | 4uy -> "i8"
        | 5uy -> "i16"
        | 6uy -> "i32"
        | 7uy -> "i64"
        | 8uy -> "f32"
        | _ -> "f64"

    let private poolStorageToken =
        function
        | U8 -> "u8"
        | U16 -> "u16"
        | U32 -> "u32"
        | U64 -> "u64"
        | I8 -> "i8"
        | I16 -> "i16"
        | I32 -> "i32"
        | I64 -> "i64"
        | F32 -> "f32"
        | F64 -> "f64"

    let private orderToken flags =
        if (flags &&& 1uy) <> 0uy then "big" else "little"

    let private writeU16 (writer: Utf8JsonWriter) (name: string) (value: uint16) =
        writer.WriteNumber(name, uint32 value)

    let private writeU8 (writer: Utf8JsonWriter) (name: string) (value: uint8) = writer.WriteNumber(name, uint32 value)
    let private writeU16Value (writer: Utf8JsonWriter) (value: uint16) = writer.WriteNumberValue(uint32 value)

    let private writeProgram (writer: Utf8JsonWriter) (index: int) recordRange (program: ImageProgram) =
        writer.WriteStartObject()
        writer.WriteNumber("index", index)
        writer.WritePropertyName("range")
        range writer recordRange
        writeU16 writer "startBit" program.StartBit
        writeU16 writer "lengthBits" program.LengthBits
        writer.WriteString("byteOrder", orderToken program.OrderFlags)
        writer.WriteBoolean("signed", (program.OrderFlags &&& 2uy) <> 0uy)
        writer.WriteString("storage", storageToken program.Storage)
        writeU16 writer "conversionIndex" program.ConversionIndex
        writeU16 writer "slotIndex" program.SlotIndex
        writer.WritePropertyName("mux")

        if program.MuxSelectorSlot = UInt16.MaxValue then
            writer.WriteNullValue()
        else
            writer.WriteStartObject()
            writeU16 writer "selectorSlot" program.MuxSelectorSlot
            writer.WriteNumber("expected", program.MuxExpected)
            writer.WriteEndObject()

        writer.WriteNull("nestedMuxIndex")
        writer.WriteEndObject()

    let private writeRegions (writer: Utf8JsonWriter) (regions: ImageRegions) =
        writer.WriteStartObject()
        writer.WritePropertyName("header")
        range writer regions.Header
        writer.WritePropertyName("directory")
        range writer regions.Directory
        writer.WritePropertyName("rxMessages")
        range writer regions.RxMessages
        writer.WritePropertyName("rxPrograms")
        range writer regions.RxPrograms
        writer.WritePropertyName("conversions")
        range writer regions.Conversions
        writer.WritePropertyName("symbols")
        range writer regions.Symbols
        optionalRange writer "extensionHeader" regions.ExtensionHeader
        optionalRange writer "nestedMuxRecords" regions.NestedMuxRecords
        optionalRange writer "qualityEntries" regions.QualityEntries
        optionalRange writer "protectionHeader" regions.ProtectionHeader
        optionalRange writer "rxProtectionPlans" regions.RxProtectionPlans
        optionalRange writer "txProtectionPlans" regions.TxProtectionPlans
        optionalRange writer "rxCounters" regions.RxCounters
        optionalRange writer "coverageSpans" regions.CoverageSpans
        optionalRange writer "txHeader" regions.TxHeader
        optionalRange writer "txMessages" regions.TxMessages
        optionalRange writer "txPrograms" regions.TxPrograms
        optionalRange writer "txCounters" regions.TxCounters
        optionalRange writer "txTemplates" regions.TxTemplates
        writer.WritePropertyName("footer")
        range writer regions.Footer
        writer.WriteEndObject()

    let private imageFeatures flags =
        [ if (flags &&& 1us) <> 0us then
              "tx"
          if (flags &&& 2us) <> 0us then
              "rxq"
          if (flags &&& 4us) <> 0us then
              "protection" ]

    let private derivedFeatures (image: RuntimeImage) =
        let allPrograms = image.Programs @ image.TxPrograms
        let crcs = image.RxProtectionPlans @ image.TxProtectionPlans

        [ if not image.Messages.IsEmpty then
              Rx
          if not image.TxMessages.IsEmpty then
              Tx
          if allPrograms |> List.exists (fun p -> p.MuxSelectorSlot <> UInt16.MaxValue) then
              Multiplexing
          if not image.NestedMuxRecords.IsEmpty then
              NestedMux
          if not image.QualityEntries.IsEmpty then
              RxQuality
          if image.TxMessages |> List.exists (fun m -> m.PayloadLength > 8uy) then
              CanFd
          if
              (image.Messages |> List.exists (fun m -> (m.EncodedCanId &&& 0x80000000u) <> 0u))
              || (image.TxMessages
                  |> List.exists (fun m -> (m.EncodedCanId &&& 0x80000000u) <> 0u))
          then
              ExtendedCan
          if allPrograms |> List.exists (fun p -> (p.OrderFlags &&& 1uy) <> 0uy) then
              Motorola
          if image.Conversions |> List.exists _.IsAffine then
              Affine
          if crcs |> List.exists (fun p -> p.HasCrc && p.Algorithm = 1uy) then
              Crc8SaeJ1850
          if crcs |> List.exists (fun p -> p.HasCrc && p.Algorithm = 2uy) then
              Crc16CcittFalse
          if crcs |> List.exists (fun p -> p.DataId.IsSome) then
              CrcDataId
          if not image.RxCounters.IsEmpty then
              RxCounter
          if not image.TxCounters.IsEmpty then
              TxCounter ]
        |> Set.ofList

    let private writeInspectCore (validated: ValidatedImage) (bytes: byte array) =
        let image = validated.Image
        let layout = validated.Layout
        let flags = uint16 bytes.[10] ||| (uint16 bytes.[11] <<< 8)

        let hash =
            "sha256:"
            + (SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant())

        let state =
            RuntimeRequirements.runtimeStateBytes
                Ilp32
                (uint32 image.PoolSlotCount)
                (uint32 image.QualityEntries.Length)
                (uint32 image.TxCounters.Length)
                (uint32 image.RxCounters.Length)
            |> Result.defaultValue 0u

        let features = derivedFeatures image

        let resources =
            [ "imageBytes", uint32 bytes.Length
              "runtimeStateBytes", state
              "runtimeScratchBytes", RuntimeRequirements.runtimeScratchBytes image
              "rxMessages", uint32 image.Messages.Length
              "rxPrograms", uint32 image.Programs.Length
              "txMessages", uint32 image.TxMessages.Length
              "txPrograms", uint32 image.TxPrograms.Length
              "poolSlots", uint32 image.PoolSlotCount
              "conversions", uint32 image.Conversions.Length
              "nestedMuxRecords", uint32 image.NestedMuxRecords.Length
              "muxDepth",
              (image.NestedMuxRecords
               |> List.map (fun r -> uint32 r.Predicates.Length)
               |> List.fold max 0u)
              "qualityEntries", uint32 image.QualityEntries.Length
              "protectionPlans", uint32 (image.RxProtectionPlans.Length + image.TxProtectionPlans.Length)
              "txCounters", uint32 image.TxCounters.Length
              "rxCounters", uint32 image.RxCounters.Length
              "coverageSpans", uint32 image.CoverageSpans.Length
              "txTemplateBytes", uint32 image.TxTemplates.Length ]

        canonicalFrom (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("format", "sc.inspect/v1")
            writer.WritePropertyName("image")
            writer.WriteStartObject()
            writer.WriteString("sha256", hash)
            writer.WriteNumber("formatVersion", 1)
            writeU16 writer "featureFlags" flags
            writer.WritePropertyName("imageFeatures")
            writer.WriteStartArray()
            imageFeatures flags |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteNumber("totalBytes", bytes.Length)
            writer.WriteString("crc32", sprintf "0x%08X" (BitConverter.ToUInt32(bytes, bytes.Length - 4)))
            writer.WriteBoolean("crcValid", true)
            writer.WriteEndObject()
            writer.WritePropertyName("runtime")
            writer.WriteStartObject()
            writer.WriteString("abi", "ilp32")
            writer.WriteNumber("imageMajor", 1)
            writer.WriteNumber("imageMinor", 0)
            writer.WritePropertyName("requiredFeatures")
            writer.WriteStartArray()

            RuntimeCapabilities.featurePairs
            |> List.iter (fun (feature, token) ->
                if features.Contains feature then
                    writer.WriteStringValue(token))

            writer.WriteEndArray()
            writer.WriteNumber("stateBytes", state)
            writer.WriteNumber("scratchBytes", RuntimeRequirements.runtimeScratchBytes image)
            writer.WriteEndObject()
            writer.WritePropertyName("resources")
            writer.WriteStartObject()
            resources |> List.iter (fun (name, value) -> writer.WriteNumber(name, value))
            writer.WriteEndObject()
            writer.WritePropertyName("regions")
            writeRegions writer layout.Regions
            writer.WritePropertyName("poolSlots")
            writer.WriteStartArray()

            image.SignalNames
            |> List.iteri (fun index name ->
                writer.WriteStartObject()
                writer.WriteNumber("index", index)
                writer.WriteString("name", name)
                writer.WritePropertyName("symbolRange")
                range writer layout.SymbolRanges.[index]

                writer.WriteNumber(
                    "freshnessMs",
                    image.QualityEntries
                    |> List.tryItem index
                    |> Option.map _.FreshnessMs
                    |> Option.defaultValue 0u
                )

                writer.WritePropertyName("qualityRange")

                match layout.QualityEntryRanges |> List.tryItem index with
                | Some r -> range writer r
                | None -> writer.WriteNullValue()

                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WritePropertyName("conversions")
            writer.WriteStartArray()

            image.Conversions
            |> List.iteri (fun index conversion ->
                writer.WriteStartObject()
                writer.WriteNumber("index", index)
                writer.WritePropertyName("range")
                range writer layout.ConversionRanges.[index]
                writer.WriteString("kind", if conversion.IsAffine then "affine" else "identity")
                writer.WriteString("factor", f64 conversion.Factor)
                writer.WriteString("offset", f64 conversion.Offset)
                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WritePropertyName("rxMessages")
            writer.WriteStartArray()

            (image.Messages, image.MessageNames)
            ||> List.iteri2 (fun index message name ->
                writer.WriteStartObject()
                writer.WriteNumber("index", index)
                writer.WritePropertyName("range")
                range writer layout.RxMessageRanges.[index]
                writer.WriteString("name", name)
                writer.WritePropertyName("symbolRange")
                range writer layout.SymbolRanges.[image.SignalNames.Length + index]
                writer.WriteNumber("canId", message.EncodedCanId &&& 0x7FFFFFFFu)
                writer.WriteBoolean("extended", (message.EncodedCanId &&& 0x80000000u) <> 0u)
                writeU16 writer "programIndex" message.ProgramIndex
                writeU16 writer "programCount" message.ProgramCount
                writer.WritePropertyName("protectionIndex")

                if image.RxProtectionPlans.IsEmpty then
                    writer.WriteNullValue()
                else
                    writer.WriteNumberValue(index)

                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WritePropertyName("rxPrograms")
            writer.WriteStartArray()

            image.Programs
            |> List.iteri (fun index item -> writeProgram writer index layout.RxProgramRanges.[index] item)

            writer.WriteEndArray()
            writer.WritePropertyName("nestedMuxRecords")
            writer.WriteStartArray()

            image.NestedMuxRecords
            |> List.iteri (fun index item ->
                writer.WriteStartObject()
                writer.WriteNumber("index", index)
                writer.WritePropertyName("range")
                range writer layout.NestedMuxRecordRanges.[index]
                writeU16 writer "targetProgramIndex" item.TargetProgramIndex
                writer.WritePropertyName("predicates")
                writer.WriteStartArray()

                item.Predicates
                |> List.iter (fun p ->
                    writer.WriteStartObject()
                    writeU16 writer "selectorProgramIndex" p.SelectorProgramIndex
                    writeU16 writer "selectorSlot" p.SelectorSlot
                    writer.WriteNumber("expected", p.Expected)
                    writer.WriteEndObject())

                writer.WriteEndArray()
                writer.WriteEndObject())

            writer.WriteEndArray()

            let writePlans (name: string) (plans: ImageProtectionPlan list) (ranges: ImageRange list) =
                writer.WritePropertyName(name)
                writer.WriteStartArray()

                plans
                |> List.iteri (fun index p ->
                    writer.WriteStartObject()
                    writer.WriteNumber("index", index)
                    writer.WritePropertyName("range")
                    range writer ranges.[index]
                    writer.WritePropertyName("crc")

                    if p.HasCrc then
                        writer.WriteStartObject()

                        writer.WriteString(
                            "algorithm",
                            if p.Algorithm = 1uy then
                                "crc8-sae-j1850"
                            else
                                "crc16-ccitt-false"
                        )

                        writeU16 writer "startBit" p.CrcStartBit
                        writeU16 writer "lengthBits" (uint16 p.CrcWidthBytes * 8us)
                        writer.WriteString("byteOrder", if p.CrcBigEndian then "big" else "little")
                        writer.WritePropertyName("dataId")

                        match p.DataId with
                        | Some value -> writer.WriteNumberValue(value)
                        | None -> writer.WriteNullValue()

                        writeU16 writer "coverageSpanIndex" p.SpanIndex
                        writeU8 writer "coverageSpanCount" p.SpanCount
                        writer.WriteEndObject()
                    else
                        writer.WriteNullValue()

                    writer.WritePropertyName("counterIndex")

                    if p.HasCounter then
                        writeU16Value writer p.CounterIndex
                    else
                        writer.WriteNullValue()

                    writer.WriteEndObject())

                writer.WriteEndArray()

            writePlans "rxProtectionPlans" image.RxProtectionPlans layout.RxProtectionPlanRanges
            writePlans "txProtectionPlans" image.TxProtectionPlans layout.TxProtectionPlanRanges
            writer.WritePropertyName("rxCounters")
            writer.WriteStartArray()

            image.RxCounters
            |> List.iteri (fun index c ->
                writer.WriteStartObject()
                writer.WriteNumber("index", index)
                writer.WritePropertyName("range")
                range writer layout.RxCounterRanges.[index]
                writeU16 writer "startBit" c.StartBit
                writeU16 writer "lengthBits" c.LengthBits
                writer.WriteString("byteOrder", if c.BigEndian then "big" else "little")
                writer.WriteNumber("modulus", c.Modulus)
                writer.WriteNumber("increment", c.Increment)
                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WritePropertyName("coverageSpans")
            writer.WriteStartArray()

            image.CoverageSpans
            |> List.iteri (fun index span ->
                writer.WriteStartObject()
                writer.WriteNumber("index", index)
                writer.WritePropertyName("range")
                range writer layout.CoverageSpanRanges.[index]
                writeU8 writer "byteStart" span.ByteOffset
                writeU16 writer "byteEnd" (uint16 span.ByteOffset + uint16 span.ByteCount)
                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WritePropertyName("txMessages")
            writer.WriteStartArray()

            image.TxMessages
            |> List.iteri (fun index m ->
                writer.WriteStartObject()
                writer.WriteNumber("index", index)
                writer.WritePropertyName("range")
                range writer layout.TxMessageRanges.[index]
                writer.WriteNumber("logicalMessageId", m.LogicalMessageId)
                writer.WriteNumber("canId", m.EncodedCanId &&& 0x7FFFFFFFu)
                writer.WriteBoolean("extended", (m.EncodedCanId &&& 0x80000000u) <> 0u)
                writeU8 writer "payloadBytes" m.PayloadLength
                writer.WriteString("frameKind", if m.PayloadLength > 8uy then "fd" else "classic")
                writeU16 writer "programIndex" m.ProgramIndex
                writeU16 writer "programCount" m.ProgramCount
                writer.WritePropertyName("counterIndex")

                if m.CounterIndex = UInt16.MaxValue then
                    writer.WriteNullValue()
                else
                    writeU16Value writer m.CounterIndex
                    writer.WritePropertyName("templateRange")
                    range writer layout.TxTemplateRanges.[index]
                    writer.WritePropertyName("protectionIndex")

                    if image.TxProtectionPlans.IsEmpty then
                        writer.WriteNullValue()
                    else
                        writer.WriteNumberValue(index)
                        writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WritePropertyName("txPrograms")
            writer.WriteStartArray()

            image.TxPrograms
            |> List.iteri (fun index item -> writeProgram writer index layout.TxProgramRanges.[index] item)

            writer.WriteEndArray()
            writer.WritePropertyName("txCounters")
            writer.WriteStartArray()

            image.TxCounters
            |> List.iteri (fun index c ->
                writer.WriteStartObject()
                writer.WriteNumber("index", index)
                writer.WritePropertyName("range")
                range writer layout.TxCounterRanges.[index]
                writeU16 writer "startBit" c.StartBit
                writeU16 writer "lengthBits" c.LengthBits
                writer.WriteString("byteOrder", if c.BigEndian then "big" else "little")
                writer.WriteNumber("modulus", c.Modulus)
                writer.WriteNumber("increment", c.Increment)
                writer.WriteNumber("initialValue", c.InitialValue)
                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WritePropertyName("txTemplates")
            writer.WriteStartArray()

            layout.TxTemplateRanges
            |> List.iteri (fun index r ->
                let start = int r.Start - int layout.Regions.TxTemplates.Value.Start in
                writer.WriteStartObject()
                writer.WriteNumber("messageIndex", index)
                writer.WritePropertyName("range")
                range writer r

                writer.WriteString(
                    "bytes",
                    Convert
                        .ToHexString(image.TxTemplates.[start .. start + int (r.End - r.Start) - 1])
                        .ToLowerInvariant()
                )

                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WriteEndObject())

    let inspect bytes =
        Scimg.readDetailed bytes
        |> Result.mapError (List.map (sprintf "%A" >> DocumentError))
        |> Result.map (fun validated -> ({ Root = cloneRoot (writeInspectCore validated bytes) }: InspectDocument))

    let private inspectRootOrder =
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
          "txTemplates" ]

    let private mapRootOrder =
        [ "format"
          "imageSha256"
          "poolAbiHash"
          "target"
          "requirements"
          "sources"
          "tables"
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
          "txTemplates" ]

    let private writeElementCanonical rootOrder substitutions (root: JsonElement) =
        canonicalFrom (fun writer ->
            let rec emit isRoot (element: JsonElement) =
                match element.ValueKind with
                | JsonValueKind.Object ->
                    writer.WriteStartObject()
                    let props = element.EnumerateObject() |> Seq.toList

                    let ordered =
                        if isRoot then
                            rootOrder
                            |> List.choose (fun name -> props |> List.tryFind (_.Name >> (=) name))
                        else
                            props

                    for property in ordered do
                        writer.WritePropertyName(property.Name)

                        match substitutions |> Map.tryFind property.Name with
                        | Some replacement when isRoot -> replacement writer
                        | _ -> emit false property.Value

                    writer.WriteEndObject()
                | JsonValueKind.Array ->
                    writer.WriteStartArray()
                    element.EnumerateArray() |> Seq.iter (emit false)
                    writer.WriteEndArray()
                | _ -> element.WriteTo(writer)

            emit true root)

    let writeInspect (document: InspectDocument) =
        try
            Ok(writeElementCanonical inspectRootOrder Map.empty document.Root)
        with ex ->
            error ex.Message

    let private validateDocument expectedFormat rootKeys (json: string) =
        try
            if isNull json || json.StartsWith("\uFEFF", StringComparison.Ordinal) then
                raise (FormatException("BOM is not allowed."))

            use document =
                JsonDocument.Parse(
                    json,
                    JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false)
                )

            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                raise (FormatException("Document must be an object."))

            let rec walk (element: JsonElement) =
                match element.ValueKind with
                | JsonValueKind.Object ->
                    let properties = element.EnumerateObject() |> Seq.toList

                    if properties.Length <> (properties |> List.map _.Name |> List.distinct).Length then
                        raise (FormatException("Duplicate property."))

                    properties
                    |> List.iter (fun p ->
                        if
                            (p.Name = "factor" || p.Name = "offset")
                            && (p.Value.ValueKind <> JsonValueKind.String
                                || not (floatPattern.IsMatch(p.Value.GetString())))
                        then
                            raise (FormatException("Invalid float token."))

                        walk p.Value)
                | JsonValueKind.Array -> element.EnumerateArray() |> Seq.iter walk
                | JsonValueKind.Number ->
                    let raw = element.GetRawText()

                    if not (uintPattern.IsMatch(raw) || intPattern.IsMatch(raw)) then
                        raise (FormatException("Noncanonical number."))
                | JsonValueKind.String ->
                    let value = element.GetString()

                    if
                        value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        && not (shaPattern.IsMatch value)
                    then
                        raise (FormatException("Invalid hash."))

                    if
                        value.StartsWith("f64:", StringComparison.OrdinalIgnoreCase)
                        && not (floatPattern.IsMatch value)
                    then
                        raise (FormatException("Invalid float token."))
                | _ -> ()

            walk root
            let properties = root.EnumerateObject() |> Seq.toList
            let names = properties |> List.map _.Name

            if names |> List.exists (fun name -> not (List.contains name rootKeys)) then
                raise (FormatException("Unknown root property."))

            if rootKeys |> List.exists (fun key -> not (List.contains key names)) then
                raise (FormatException("Missing root property."))

            if root.GetProperty("format").GetString() <> expectedFormat then
                raise (FormatException("Invalid format."))

            Ok(root.Clone())
        with ex ->
            error ex.Message

    let private validateRangesAndOrder (root: JsonElement) =
        try
            let total = root.GetProperty("image").GetProperty("totalBytes").GetUInt32()

            let rec ranges (element: JsonElement) =
                match element.ValueKind with
                | JsonValueKind.Object ->
                    let names = element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

                    if names.Contains "start" || names.Contains "end" then
                        if names <> Set.ofList [ "start"; "end" ] then
                            raise (FormatException("Invalid range object."))

                        let first = element.GetProperty("start").GetUInt32()
                        let last = element.GetProperty("end").GetUInt32()

                        if first > last || last > total then
                            raise (FormatException("Range is outside image."))

                    element.EnumerateObject() |> Seq.iter (fun p -> ranges p.Value)
                | JsonValueKind.Array -> element.EnumerateArray() |> Seq.iter ranges
                | _ -> ()

            ranges root

            let imageKeys =
                [ "sha256"
                  "formatVersion"
                  "featureFlags"
                  "imageFeatures"
                  "totalBytes"
                  "crc32"
                  "crcValid" ]
                |> Set.ofList

            let actual =
                root.GetProperty("image").EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

            if actual <> imageKeys then
                raise (FormatException("Invalid image properties."))

            for name in inspectRootOrder |> List.skip 5 do
                let array = root.GetProperty(name)

                if array.ValueKind = JsonValueKind.Array then
                    let values = array.EnumerateArray() |> Seq.toList

                    let identities =
                        values
                        |> List.choose (fun item ->
                            if item.TryGetProperty("index") |> fst then
                                Some(sprintf "%010u" (item.GetProperty("index").GetUInt32()))
                            elif item.TryGetProperty("key") |> fst then
                                Some(item.GetProperty("key").GetString())
                            else
                                None)

                    if
                        identities <> List.sort identities
                        || identities.Length <> (List.distinct identities).Length
                    then
                        raise (FormatException("Array identity order is invalid."))

            Ok()
        with ex ->
            error ex.Message

    let parseInspect json =
        validateDocument "sc.inspect/v1" inspectRootOrder json
        |> Result.bind (fun root ->
            validateRangesAndOrder root
            |> Result.map (fun () -> ({ Root = root }: InspectDocument)))

    let private writeTarget (writer: Utf8JsonWriter) (target: RuntimeCapabilities) =
        let canonical =
            RuntimeCapabilities.writeCanonical target
            |> Result.defaultWith (fun e -> failwithf "%A" e)

        use document = JsonDocument.Parse(canonical)
        document.RootElement.WriteTo(writer)

    let private writeRequirements (writer: Utf8JsonWriter) (r: RuntimeRequirements) =
        writer.WriteStartObject()
        writeU16 writer "runtimeImageMajor" r.RuntimeImageMajor
        writeU16 writer "runtimeImageMinor" r.RuntimeImageMinor
        writer.WritePropertyName("features")
        writer.WriteStartArray()

        RuntimeCapabilities.featurePairs
        |> List.iter (fun (f, t) ->
            if r.Features.Contains f then
                writer.WriteStringValue(t))

        writer.WriteEndArray()

        [ "imageBytes", r.ImageBytes
          "runtimeStateBytes", r.RuntimeStateBytes
          "runtimeScratchBytes", r.RuntimeScratchBytes
          "rxMessages", r.RxMessages
          "rxPrograms", r.RxPrograms
          "txMessages", r.TxMessages
          "txPrograms", r.TxPrograms
          "poolSlots", r.PoolSlots
          "conversions", r.Conversions
          "nestedMuxRecords", r.NestedMuxRecords
          "muxDepth", r.MuxDepth
          "qualityEntries", r.QualityEntries
          "protectionPlans", r.ProtectionPlans
          "txCounters", r.TxCounters
          "rxCounters", r.RxCounters
          "coverageSpans", r.CoverageSpans
          "txTemplateBytes", r.TxTemplateBytes
          "payloadBytes", r.PayloadBytes ]
        |> List.iter (fun (n, v) -> writer.WriteNumber(n, v))

        writer.WriteEndObject()

    let private sourceForMessage (sources: MapSource list) (name: string) =
        sources
        |> List.find (fun source -> source.Wire.Messages |> List.exists (_.Name >> (=) name))

    let createMap
        (pool: PoolContract)
        (linked: LinkedSchema)
        (image: RuntimeImage)
        (bytes: byte array)
        (layout: ImageLayout)
        (hash: PoolAbiHash)
        (requirements: RuntimeRequirements)
        (target: RuntimeCapabilities)
        (sources: MapSource list)
        =
        try
            let poolBySlot = pool.Signals |> List.toArray

            let rxOrdered =
                linked.Messages
                |> List.sortBy (fun m -> if m.IsExtended then 0x80000000u ||| m.Id else m.Id)

            let txOrdered = linked.TxMessages |> List.sortBy _.LogicalMessageId

            let rxKey (message: LinkedMessage) =
                "rx:"
                + (message.Plans
                   |> List.map (fun p -> string poolBySlot.[int p.PoolSlotIndex].SemanticId)
                   |> List.sort
                   |> String.concat ",")

            let txKey (message: LinkedTxMessage) = "tx:" + string message.LogicalMessageId

            let json =
                canonicalFrom (fun writer ->
                    writer.WriteStartObject()
                    writer.WriteString("format", "sc.map/v1")

                    writer.WriteString(
                        "imageSha256",
                        "sha256:"
                        + (SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant())
                    )

                    writer.WriteString("poolAbiHash", PoolAbi.format hash)
                    writer.WritePropertyName("target")
                    writeTarget writer target
                    writer.WritePropertyName("requirements")
                    writeRequirements writer requirements
                    writer.WritePropertyName("sources")
                    writer.WriteStartArray()

                    sources
                    |> List.iter (fun s ->
                        writer.WriteStartObject()
                        writer.WriteString("key", s.Key)
                        writer.WriteString("path", s.Path.Replace('\\', '/'))
                        writer.WriteString("type", "dbc")
                        writer.WriteEndObject())

                    writer.WriteEndArray()
                    writer.WritePropertyName("tables")
                    writeRegions writer layout.Regions
                    writer.WritePropertyName("poolSlots")
                    writer.WriteStartArray()

                    pool.Signals
                    |> List.iteri (fun index p ->
                        writer.WriteStartObject()
                        writer.WriteString("key", "pool:" + string p.SemanticId)
                        writer.WriteNumber("semanticId", p.SemanticId)
                        writer.WriteString("name", p.Name)
                        writer.WriteString("storage", poolStorageToken p.Storage)
                        writer.WriteString("unit", p.Unit)
                        writer.WriteString("direction", if p.Direction = Direction.Rx then "rx" else "tx")

                        let optional (name: string) value =
                            writer.WritePropertyName(name)

                            match value with
                            | Some v -> writer.WriteStringValue(f64 v)
                            | None -> writer.WriteNullValue() in

                        optional "min" p.Min
                        optional "max" p.Max
                        optional "default" p.Default
                        writer.WriteNumber("freshnessMs", p.FreshnessMs |> Option.defaultValue 0u)
                        writer.WriteNumber("imageIndex", index)
                        writer.WritePropertyName("symbolRange")
                        range writer layout.SymbolRanges.[index]
                        writer.WriteEndObject())

                    writer.WriteEndArray()
                    writer.WritePropertyName("conversions")
                    writer.WriteStartArray()

                    image.Conversions
                    |> List.iteri (fun index c ->
                        writer.WriteStartObject()
                        writer.WriteString("key", sprintf "conversion:%s:%s:0" (f64 c.Factor) (f64 c.Offset))
                        writer.WriteString("factor", f64 c.Factor)
                        writer.WriteString("offset", f64 c.Offset)
                        writer.WriteNumber("imageIndex", index)
                        writer.WritePropertyName("range")
                        range writer layout.ConversionRanges.[index]
                        writer.WriteEndObject())

                    writer.WriteEndArray()
                    writer.WritePropertyName("rxMessages")
                    writer.WriteStartArray()

                    rxOrdered
                    |> List.iteri (fun index m ->
                        let source = sourceForMessage sources m.Name in
                        writer.WriteStartObject()
                        writer.WriteString("key", rxKey m)
                        writer.WriteString("source", source.Key)
                        writer.WriteString("sourcePath", source.Path.Replace('\\', '/'))
                        writer.WriteString("name", m.Name)
                        writer.WriteNumber("canId", m.Id)
                        writer.WriteBoolean("extended", m.IsExtended)
                        writeU16 writer "payloadBytes" m.Length
                        writer.WriteNumber("imageIndex", index)
                        writer.WritePropertyName("range")
                        range writer layout.RxMessageRanges.[index]
                        writer.WriteEndObject())

                    writer.WriteEndArray()
                    writer.WritePropertyName("rxPrograms")
                    writer.WriteStartArray()
                    let mutable rxIndex = 0 in

                    rxOrdered
                    |> List.iter (fun m ->
                        m.Plans
                        |> List.sortBy _.PoolSlotIndex
                        |> List.iter (fun p ->
                            writer.WriteStartObject()

                            writer.WriteString(
                                "key",
                                sprintf "%s/pool:%u" (rxKey m) poolBySlot.[int p.PoolSlotIndex].SemanticId
                            )

                            writer.WriteNumber("imageIndex", rxIndex)
                            writer.WritePropertyName("range")
                            range writer layout.RxProgramRanges.[rxIndex]
                            writer.WriteEndObject()
                            rxIndex <- rxIndex + 1))

                    writer.WriteEndArray()
                    writer.WritePropertyName("nestedMuxRecords")
                    writer.WriteStartArray()
                    writer.WriteEndArray()

                    let simpleKeys (name: string) (keys: string list) (ranges: ImageRange list) =
                        writer.WritePropertyName(name)
                        writer.WriteStartArray()

                        keys
                        |> List.iteri (fun i key ->
                            writer.WriteStartObject()
                            writer.WriteString("key", key)
                            writer.WriteNumber("imageIndex", i)
                            writer.WritePropertyName("range")
                            range writer ranges.[i]
                            writer.WriteEndObject())

                        writer.WriteEndArray()

                    simpleKeys "rxProtectionPlans" (rxOrdered |> List.map rxKey) layout.RxProtectionPlanRanges
                    simpleKeys "txProtectionPlans" (txOrdered |> List.map txKey) layout.TxProtectionPlanRanges

                    simpleKeys
                        "rxCounters"
                        (rxOrdered
                         |> List.choose (fun m ->
                             m.Protection |> Option.bind _.Counter |> Option.map (fun _ -> rxKey m)))
                        layout.RxCounterRanges

                    let spanKeys =
                        [ yield!
                              rxOrdered
                              |> List.choose (fun m ->
                                  m.Protection |> Option.bind _.Crc |> Option.map (fun _ -> rxKey m + "/span:0"))
                          yield!
                              txOrdered
                              |> List.choose (fun m -> m.Crc |> Option.map (fun _ -> txKey m + "/span:0")) ] in

                    simpleKeys "coverageSpans" spanKeys layout.CoverageSpanRanges
                    writer.WritePropertyName("txMessages")
                    writer.WriteStartArray()

                    txOrdered
                    |> List.iteri (fun index m ->
                        let source = sourceForMessage sources m.Name in
                        writer.WriteStartObject()
                        writer.WriteString("key", txKey m)
                        writer.WriteString("source", source.Key)
                        writer.WriteString("sourcePath", source.Path.Replace('\\', '/'))
                        writer.WriteString("name", m.Name)
                        writer.WriteNumber("logicalMessageId", m.LogicalMessageId)
                        writer.WriteNumber("canId", m.CanId)
                        writer.WriteBoolean("extended", m.IsExtended)
                        writeU16 writer "payloadBytes" m.Length
                        writer.WriteNumber("imageIndex", index)
                        writer.WritePropertyName("range")
                        range writer layout.TxMessageRanges.[index]
                        writer.WriteEndObject())

                    writer.WriteEndArray()
                    writer.WritePropertyName("txPrograms")
                    writer.WriteStartArray()
                    let mutable txIndex = 0 in

                    txOrdered
                    |> List.iter (fun m ->
                        m.Plans
                        |> List.sortBy _.PoolSlotIndex
                        |> List.iter (fun p ->
                            writer.WriteStartObject()

                            writer.WriteString(
                                "key",
                                sprintf "%s/pool:%u" (txKey m) poolBySlot.[int p.PoolSlotIndex].SemanticId
                            )

                            writer.WriteNumber("imageIndex", txIndex)
                            writer.WritePropertyName("range")
                            range writer layout.TxProgramRanges.[txIndex]
                            writer.WriteEndObject()
                            txIndex <- txIndex + 1))

                    writer.WriteEndArray()
                    writer.WritePropertyName("txCounters")
                    writer.WriteStartArray()
                    let mutable counterIndex = 0 in

                    txOrdered
                    |> List.iter (fun m ->
                        match m.Counter with
                        | Some c ->
                            writer.WriteStartObject()
                            writer.WriteString("key", txKey m)
                            writeU16 writer "startBit" c.StartBit
                            writeU16 writer "lengthBits" c.Length
                            writer.WriteString("byteOrder", if c.ByteOrder = Ir.Big then "big" else "little")
                            writer.WriteNumber("modulus", c.Modulus)
                            writer.WriteNumber("increment", c.Increment)
                            writer.WriteNumber("initialValue", c.InitialValue)
                            writer.WriteNumber("imageIndex", counterIndex)
                            writer.WritePropertyName("range")
                            range writer layout.TxCounterRanges.[counterIndex]
                            writer.WriteEndObject()
                            counterIndex <- counterIndex + 1
                        | None -> ())

                    writer.WriteEndArray()
                    simpleKeys "txTemplates" (txOrdered |> List.map txKey) layout.TxTemplateRanges
                    writer.WriteEndObject())

            Ok
                { Root = cloneRoot json
                  PoolAbiHash = hash
                  Target = target }
        with ex ->
            error ex.Message

    let writeMap document =
        try
            let substitutions =
                Map.ofList
                    [ "poolAbiHash",
                      (fun (w: Utf8JsonWriter) -> w.WriteStringValue(PoolAbi.format document.PoolAbiHash))
                      "target", (fun w -> writeTarget w document.Target) ]

            Ok(writeElementCanonical mapRootOrder substitutions document.Root)
        with ex ->
            error ex.Message

    let parseMap json =
        validateDocument "sc.map/v1" mapRootOrder json
        |> Result.bind (fun root ->
            try
                let hash =
                    PoolAbi.parse (root.GetProperty("poolAbiHash").GetString())
                    |> Result.defaultWith (fun e -> failwithf "%A" e)

                let target =
                    RuntimeCapabilities.parse (root.GetProperty("target").GetRawText())
                    |> Result.defaultWith (fun e -> failwithf "%A" e)

                let imageHash = root.GetProperty("imageSha256").GetString()

                if not (shaPattern.IsMatch imageHash) then
                    raise (FormatException("Invalid image hash."))

                let total =
                    root.GetProperty("tables").GetProperty("footer").GetProperty("end").GetUInt32()

                let rec validateRanges (element: JsonElement) =
                    match element.ValueKind with
                    | JsonValueKind.Object ->
                        match element.TryGetProperty("start"), element.TryGetProperty("end") with
                        | (true, first), (true, last) when
                            first.GetUInt32() <= last.GetUInt32() && last.GetUInt32() <= total
                            ->
                            ()
                        | (true, _), (true, _) -> raise (FormatException("Range is outside image."))
                        | _ -> ()

                        element.EnumerateObject()
                        |> Seq.iter (fun property -> validateRanges property.Value)
                    | JsonValueKind.Array -> element.EnumerateArray() |> Seq.iter validateRanges
                    | _ -> ()

                validateRanges root

                for name in mapRootOrder |> List.skip 7 do
                    let items = root.GetProperty(name).EnumerateArray() |> Seq.toList
                    let keys = items |> List.map (fun item -> item.GetProperty("key").GetString())

                    if keys.Length <> (List.distinct keys |> List.length) then
                        raise (FormatException("Duplicate identity."))

                let poolKeys =
                    root.GetProperty("poolSlots").EnumerateArray()
                    |> Seq.map (fun item -> item.GetProperty("key").GetString())
                    |> Seq.toList

                if poolKeys <> List.sort poolKeys then
                    raise (FormatException("Identity order is invalid."))

                Ok
                    { Root = root
                      PoolAbiHash = hash
                      Target = target }
            with ex ->
                error ex.Message)
