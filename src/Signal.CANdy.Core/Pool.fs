namespace Signal.CANdy.Core

open System
open System.IO
open System.Text
open System.Text.Json
open Signal.CANdy.Core.Errors

module Pool =

    /// Storage representation used by a pool signal.
    type StorageType =
        | U8
        | U16
        | U32
        | U64
        | I8
        | I16
        | I32
        | I64
        | F32
        | F64

    /// Data-flow direction of a pool signal.
    type Direction =
        | Rx
        | Tx

    /// A signal in the compiled pool contract.
    type PoolSignal =
        { Name: string
          SemanticId: uint32
          Storage: StorageType
          Unit: string
          Direction: Direction
          Min: float option
          Max: float option
          Default: float option }

    /// The compiled pool contract for an application.
    type PoolContract =
        { Name: string
          Signals: PoolSignal list }

    let private duplicateErrors key createError signals =
        let collect (seen, errors) signal =
            let value = key signal

            if Set.contains value seen then
                seen, createError signal :: errors
            else
                Set.add value seen, errors

        signals |> List.fold collect (Set.empty, []) |> snd |> List.rev

    let private validateSignal (signal: PoolSignal) =
        [ if String.IsNullOrWhiteSpace(signal.Name) then
              MissingField "Signal name"

          match signal.Min, signal.Max with
          | Some minimum, Some maximum when minimum > maximum -> InvalidRange signal.Name
          | _ -> ()

          match signal.Min, signal.Max, signal.Default with
          | Some minimum, Some maximum, Some defaultValue when defaultValue < minimum || defaultValue > maximum ->
              DefaultOutOfRange signal.Name
          | _ -> () ]

    /// Validate names, semantic IDs, and numeric bounds in a pool contract.
    let validate (contract: PoolContract) : Result<PoolContract, ValidationError list> =
        let poolErrors =
            [ if String.IsNullOrWhiteSpace(contract.Name) then
                  MissingField "Pool name" ]

        let signalErrors = contract.Signals |> List.collect validateSignal

        let semanticIdErrors =
            contract.Signals
            |> duplicateErrors _.SemanticId (fun signal -> DuplicateSemanticId(signal.SemanticId, signal.Name))

        let nameErrors =
            contract.Signals
            |> duplicateErrors _.Name (fun signal -> DuplicateName signal.Name)

        let errors = poolErrors @ signalErrors @ semanticIdErrors @ nameErrors

        if List.isEmpty errors then Ok contract else Error errors

    type private ResultBuilder() =
        member _.Bind(result, binder) = Result.bind binder result
        member _.Return(value) = Ok value
        member _.ReturnFrom(result) = result

    let private result = ResultBuilder()

    let private storageTypeFromString (value: string) =
        match value with
        | "u8" -> Ok U8
        | "u16" -> Ok U16
        | "u32" -> Ok U32
        | "u64" -> Ok U64
        | "i8" -> Ok I8
        | "i16" -> Ok I16
        | "i32" -> Ok I32
        | "i64" -> Ok I64
        | "f32" -> Ok F32
        | "f64" -> Ok F64
        | _ -> Error(sprintf "Unknown storage type '%s'" value)

    let private storageTypeToString storage =
        match storage with
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

    let private directionFromString value =
        match value with
        | "rx" -> Ok Rx
        | "tx" -> Ok Tx
        | _ -> Error(sprintf "Unknown direction '%s'" value)

    let private directionToString direction =
        match direction with
        | Rx -> "rx"
        | Tx -> "tx"

    let private objectProperties context (element: JsonElement) =
        if element.ValueKind = JsonValueKind.Object then
            Ok(element.EnumerateObject() |> Seq.toList)
        else
            Error(sprintf "%s must be a JSON object" context)

    let private ensureAllowedProperties (context: string) (allowed: string list) (properties: JsonProperty list) =
        let unknownProperty =
            properties
            |> List.tryFind (fun property -> not (List.contains property.Name allowed))

        let duplicateProperty =
            properties
            |> List.countBy (fun property -> property.Name)
            |> List.tryFind (fun (_, count) -> count > 1)

        match unknownProperty, duplicateProperty with
        | Some property, _ -> Error(sprintf "%s contains unknown key '%s'" context property.Name)
        | None, Some(name, _) -> Error(sprintf "%s contains duplicate key '%s'" context name)
        | None, None -> Ok()

    let private tryProperty (name: string) (properties: JsonProperty list) =
        properties
        |> List.tryFind (fun property -> property.Name = name)
        |> Option.map (fun property -> property.Value)

    let private requiredProperty (context: string) (name: string) (properties: JsonProperty list) =
        match tryProperty name properties with
        | Some value -> Ok value
        | None -> Error(sprintf "%s is missing required key '%s'" context name)

    let private requiredString (context: string) (name: string) (properties: JsonProperty list) =
        result {
            let! value = requiredProperty context name properties

            if value.ValueKind = JsonValueKind.String then
                return value.GetString()
            else
                return! Error(sprintf "%s key '%s' must be a string" context name)
        }

    let private optionalString (context: string) (name: string) (defaultValue: string) (properties: JsonProperty list) =
        match tryProperty name properties with
        | None -> Ok defaultValue
        | Some value when value.ValueKind = JsonValueKind.String -> Ok(value.GetString())
        | Some _ -> Error(sprintf "%s key '%s' must be a string" context name)

    let private requiredUInt32 (context: string) (name: string) (properties: JsonProperty list) =
        result {
            let! value = requiredProperty context name properties

            match value.TryGetUInt32() with
            | true, number -> return number
            | false, _ -> return! Error(sprintf "%s key '%s' must be a uint32" context name)
        }

    let private optionalFloat (context: string) (name: string) (properties: JsonProperty list) =
        match tryProperty name properties with
        | None -> Ok None
        | Some value ->
            match value.TryGetDouble() with
            | true, number -> Ok(Some number)
            | false, _ -> Error(sprintf "%s key '%s' must be a number" context name)

    let private parseSignal (index: int) (element: JsonElement) =
        let context = sprintf "signals[%d]" index

        result {
            let! properties = objectProperties context element

            do!
                ensureAllowedProperties
                    context
                    [ "name"
                      "semanticId"
                      "storage"
                      "unit"
                      "direction"
                      "min"
                      "max"
                      "default" ]
                    properties

            let! name = requiredString context "name" properties
            let! semanticId = requiredUInt32 context "semanticId" properties
            let! storageName = requiredString context "storage" properties
            let! storage = storageTypeFromString storageName
            let! unit = optionalString context "unit" "" properties
            let! directionName = requiredString context "direction" properties
            let! direction = directionFromString directionName
            let! minimum = optionalFloat context "min" properties
            let! maximum = optionalFloat context "max" properties
            let! defaultValue = optionalFloat context "default" properties

            return
                { Name = name
                  SemanticId = semanticId
                  Storage = storage
                  Unit = unit
                  Direction = direction
                  Min = minimum
                  Max = maximum
                  Default = defaultValue }
        }

    let private parseSignals (context: string) (properties: JsonProperty list) =
        result {
            let! value = requiredProperty context "signals" properties

            if value.ValueKind <> JsonValueKind.Array then
                return! Error(sprintf "%s key 'signals' must be an array" context)
            else
                let rec parseAll index elements =
                    match elements with
                    | [] -> Ok []
                    | element :: rest ->
                        result {
                            let! signal = parseSignal index element
                            let! remaining = parseAll (index + 1) rest
                            return signal :: remaining
                        }

                return! value.EnumerateArray() |> Seq.toList |> parseAll 0
        }

    let private validateVersion (context: string) (properties: JsonProperty list) =
        match tryProperty "version" properties with
        | None -> Ok()
        | Some value when value.ValueKind <> JsonValueKind.String ->
            Error(sprintf "%s key 'version' must be a string" context)
        | Some value when value.GetString() <> "1" ->
            Error(sprintf "%s has unsupported version '%s'" context (value.GetString()))
        | Some _ -> Ok()

    /// Parse a strict JSON pool definition and validate the resulting contract.
    let parsePoolDefinition (json: string) : Result<PoolContract, ValidationError list> =
        try
            use document =
                JsonDocument.Parse(
                    json,
                    JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false)
                )

            let parsed =
                result {
                    let context = "Pool definition"
                    let! properties = objectProperties context document.RootElement
                    do! ensureAllowedProperties context [ "name"; "version"; "signals" ] properties
                    do! validateVersion context properties
                    let! name = requiredString context "name" properties
                    let! signals = parseSignals context properties
                    return { Name = name; Signals = signals }
                }

            match parsed with
            | Ok contract -> validate contract
            | Error details -> Error[InvalidJson details]
        with ex ->
            Error[InvalidJson ex.Message]

    let private writeOptionalNumber (writer: Utf8JsonWriter) (name: string) (value: float option) =
        value |> Option.iter (fun number -> writer.WriteNumber(name, number))

    /// Validate a pool contract and write its deterministic version 1 JSON manifest.
    let writeManifest (contract: PoolContract) : Result<string, ValidationError list> =
        match validate contract with
        | Error errors -> Error errors
        | Ok validContract ->
            try
                use stream = new MemoryStream()
                let options = JsonWriterOptions(Indented = true)

                use writer = new Utf8JsonWriter(stream, options)
                writer.WriteStartObject()
                writer.WriteString("name", validContract.Name)
                writer.WriteString("version", "1")
                writer.WritePropertyName("signals")
                writer.WriteStartArray()

                validContract.Signals
                |> List.iter (fun signal ->
                    writer.WriteStartObject()
                    writer.WriteString("name", signal.Name)
                    writer.WriteNumber("semanticId", signal.SemanticId)
                    writer.WriteString("storage", storageTypeToString signal.Storage)
                    writer.WriteString("unit", signal.Unit)
                    writer.WriteString("direction", directionToString signal.Direction)
                    writeOptionalNumber writer "min" signal.Min
                    writeOptionalNumber writer "max" signal.Max
                    writeOptionalNumber writer "default" signal.Default
                    writer.WriteEndObject())

                writer.WriteEndArray()
                writer.WriteEndObject()
                writer.Flush()

                let json = Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n")
                Ok(json + "\n")
            with ex ->
                Error[InvalidJson ex.Message]
