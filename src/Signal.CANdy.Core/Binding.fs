namespace Signal.CANdy.Core

open System.Text.Json
open Signal.CANdy.Core.Errors

module Binding =

    /// Conversion policy applied between raw wire values and pool values.
    type Conversion =
        | Identity
        | Affine of factor: float * offset: float

    /// Explicit relationship between one pool signal and one wire signal.
    type SignalBinding =
        { PoolSignalName: string
          MessageName: string
          WireSignalName: string
          Conversion: Conversion }

    type CrcAlgorithm =
        | Crc8SaeJ1850
        | Crc16CcittFalse

    type CrcBinding =
        { WireSignalName: string
          Algorithm: CrcAlgorithm
          ByteStart: uint8
          ByteEndInclusive: uint8
          DataId: uint16 option }

    type CounterBinding =
        { WireSignalName: string
          Modulus: uint32
          Increment: uint32 }

    type RxMessageBinding =
        { MessageName: string
          Crc: CrcBinding option
          Counter: CounterBinding option }

    /// Explicit stateful counter profile for one transmitted message.
    type TxCounterBinding =
        { WireSignalName: string
          Modulus: uint32
          Increment: uint32
          InitialValue: uint32 }

    /// Explicit logical identifier and optional protection for one transmitted message.
    type TxMessageBinding =
        { MessageName: string
          LogicalMessageId: uint32
          Crc: CrcBinding option
          Counter: TxCounterBinding option }

    /// Bindings are deliberately separate from both source models.
    type BindingSet =
        { Bindings: SignalBinding list
          RxMessages: RxMessageBinding list
          TxMessages: TxMessageBinding list }

    /// Validate conversion contracts. Direction-aware duplicate checks happen in the linker.
    let validate (bindings: SignalBinding list) : Result<SignalBinding list, ValidationError list> =
        let conversionErrors =
            bindings
            |> List.choose (fun binding ->
                match binding.Conversion with
                | Affine(factor, _) when
                    factor = 0.0 || System.Double.IsNaN(factor) || System.Double.IsInfinity(factor)
                    ->
                    Some(InvalidValue(sprintf "Pool signal '%s' has an invalid affine factor." binding.PoolSignalName))
                | Affine(_, offset) when System.Double.IsNaN(offset) || System.Double.IsInfinity(offset) ->
                    Some(InvalidValue(sprintf "Pool signal '%s' has an invalid affine offset." binding.PoolSignalName))
                | _ -> None)

        if conversionErrors.IsEmpty then
            Ok bindings
        else
            Error conversionErrors

    let private counterValuesError messageName modulus increment initialValue =
        if modulus = 1u then
            Some(InvalidValue(sprintf "Message '%s' counter modulus must be zero or at least two." messageName))
        elif increment = 0u then
            Some(InvalidValue(sprintf "Message '%s' counter increment must be non-zero." messageName))
        elif modulus <> 0u && increment >= modulus then
            Some(InvalidValue(sprintf "Message '%s' counter increment must be less than modulus." messageName))
        elif modulus <> 0u && initialValue |> Option.exists (fun value -> value >= modulus) then
            Some(InvalidValue(sprintf "Message '%s' counter initial value must be less than modulus." messageName))
        else
            None

    let private counterProfileError messageName (counter: TxCounterBinding) =
        counterValuesError messageName counter.Modulus counter.Increment (Some counter.InitialValue)

    let private rxCounterProfileError messageName (counter: CounterBinding) =
        counterValuesError messageName counter.Modulus counter.Increment None

    /// Validate message-level TX identities and counter profiles.
    let validateSet (bindingSet: BindingSet) : Result<BindingSet, ValidationError list> =
        let duplicateRxErrors =
            bindingSet.RxMessages
            |> List.groupBy (fun message -> message.MessageName)
            |> List.choose (fun (name, values) ->
                if values.Length > 1 then
                    Some(InvalidValue(sprintf "RX message '%s' is declared more than once." name))
                else
                    None)

        let duplicateMessageErrors =
            bindingSet.TxMessages
            |> List.groupBy (fun message -> message.MessageName)
            |> List.choose (fun (name, values) ->
                if values.Length > 1 then
                    Some(InvalidValue(sprintf "TX message '%s' is declared more than once." name))
                else
                    None)

        let duplicateIdErrors =
            bindingSet.TxMessages
            |> List.groupBy (fun message -> message.LogicalMessageId)
            |> List.choose (fun (logicalId, values) ->
                if values.Length > 1 then
                    Some(InvalidValue(sprintf "TX logical message ID %u is declared more than once." logicalId))
                else
                    None)

        let counterErrors =
            (bindingSet.TxMessages
             |> List.choose (fun message -> message.Counter |> Option.bind (counterProfileError message.MessageName)))
            @ (bindingSet.RxMessages
               |> List.choose (fun message ->
                   message.Counter |> Option.bind (rxCounterProfileError message.MessageName)))

        let emptyRxErrors =
            bindingSet.RxMessages
            |> List.choose (fun message ->
                if message.Crc.IsNone && message.Counter.IsNone then
                    Some(
                        InvalidValue(
                            sprintf "RX message '%s' must declare CRC or counter protection." message.MessageName
                        )
                    )
                else
                    None)

        let bindingErrors =
            match validate bindingSet.Bindings with
            | Ok _ -> []
            | Error errors -> errors

        let errors =
            bindingErrors
            @ duplicateRxErrors
            @ duplicateMessageErrors
            @ duplicateIdErrors
            @ counterErrors
            @ emptyRxErrors

        if errors.IsEmpty then Ok bindingSet else Error errors

    type private ResultBuilder() =
        member _.Bind(result, binder) = Result.bind binder result
        member _.Return(value) = Ok value
        member _.ReturnFrom(result) = result

    let private result = ResultBuilder()

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

    let private requiredNumber (context: string) (name: string) (properties: JsonProperty list) =
        result {
            let! value = requiredProperty context name properties

            if value.ValueKind <> JsonValueKind.Number then
                return! Error(sprintf "%s key '%s' must be a number" context name)
            else
                match value.TryGetDouble() with
                | true, number -> return number
                | false, _ -> return! Error(sprintf "%s key '%s' must be a number" context name)
        }

    let private requiredUInt32 (context: string) (name: string) (properties: JsonProperty list) =
        result {
            let! value = requiredProperty context name properties

            match value.TryGetUInt32() with
            | true, number -> return number
            | false, _ -> return! Error(sprintf "%s key '%s' must be a uint32" context name)
        }

    let private parseConversion index (element: JsonElement) =
        let context = sprintf "bindings[%d].conversion" index

        result {
            let! properties = objectProperties context element
            let! kind = requiredString context "kind" properties

            match kind with
            | "identity" ->
                do! ensureAllowedProperties context [ "kind" ] properties
                return Identity
            | "affine" ->
                do! ensureAllowedProperties context [ "kind"; "factor"; "offset" ] properties
                let! factor = requiredNumber context "factor" properties
                let! offset = requiredNumber context "offset" properties

                if
                    factor = 0.0
                    || not (System.Double.IsFinite(factor))
                    || not (System.Double.IsFinite(offset))
                then
                    return! Error(sprintf "%s must contain finite values and a non-zero factor" context)
                else
                    return Affine(factor, offset)
            | _ -> return! Error(sprintf "%s has unknown kind '%s'" context kind)
        }

    let private parseBinding index (element: JsonElement) =
        let context = sprintf "bindings[%d]" index

        result {
            let! properties = objectProperties context element

            do! ensureAllowedProperties context [ "poolSignal"; "message"; "wireSignal"; "conversion" ] properties

            let! poolSignal = requiredString context "poolSignal" properties
            let! message = requiredString context "message" properties
            let! wireSignal = requiredString context "wireSignal" properties
            let! conversionElement = requiredProperty context "conversion" properties
            let! conversion = parseConversion index conversionElement

            return
                { PoolSignalName = poolSignal
                  MessageName = message
                  WireSignalName = wireSignal
                  Conversion = conversion }
        }

    let private parseArray context parser (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Array then
            Error(sprintf "%s must be an array" context)
        else
            let rec parseAll index elements =
                match elements with
                | [] -> Ok []
                | item :: rest ->
                    result {
                        let! parsed = parser index item
                        let! remaining = parseAll (index + 1) rest
                        return parsed :: remaining
                    }

            element.EnumerateArray() |> Seq.toList |> parseAll 0

    let private parseBindings (properties: JsonProperty list) =
        result {
            let! value = requiredProperty "Binding set" "bindings" properties
            return! parseArray "Binding set key 'bindings'" parseBinding value
        }

    let private optionalUInt16 (context: string) (name: string) (properties: JsonProperty list) =
        match tryProperty name properties with
        | None -> Ok None
        | Some value ->
            match value.TryGetUInt16() with
            | true, number -> Ok(Some number)
            | false, _ -> Error(sprintf "%s key '%s' must be a uint16" context name)

    let private parseCrc owner index (element: JsonElement) =
        let context = sprintf "%s[%d].crc" owner index

        result {
            let! properties = objectProperties context element
            do! ensureAllowedProperties context [ "wireSignal"; "algorithm"; "byteRange"; "dataId" ] properties
            let! wireSignal = requiredString context "wireSignal" properties
            let! algorithmName = requiredString context "algorithm" properties
            let! rangeElement = requiredProperty context "byteRange" properties
            let rangeContext = context + ".byteRange"
            let! rangeProperties = objectProperties rangeContext rangeElement
            do! ensureAllowedProperties rangeContext [ "start"; "end" ] rangeProperties
            let! byteStart = requiredUInt32 rangeContext "start" rangeProperties
            let! byteEnd = requiredUInt32 rangeContext "end" rangeProperties
            let! dataId = optionalUInt16 context "dataId" properties

            let! algorithm =
                match algorithmName with
                | "crc8-sae-j1850" -> Ok Crc8SaeJ1850
                | "crc16-ccitt-false" -> Ok Crc16CcittFalse
                | _ -> Error(sprintf "%s has unknown algorithm '%s'" context algorithmName)

            if byteStart > 255u || byteEnd > 255u || byteStart > byteEnd then
                return! Error(sprintf "%s byte range is invalid" context)
            else
                return
                    { WireSignalName = wireSignal
                      Algorithm = algorithm
                      ByteStart = uint8 byteStart
                      ByteEndInclusive = uint8 byteEnd
                      DataId = dataId }
        }

    let private parseCounter index (element: JsonElement) =
        let context = sprintf "txMessages[%d].counter" index

        result {
            let! properties = objectProperties context element

            do! ensureAllowedProperties context [ "wireSignal"; "modulus"; "increment"; "initialValue" ] properties

            let! wireSignal = requiredString context "wireSignal" properties
            let! modulus = requiredUInt32 context "modulus" properties
            let! increment = requiredUInt32 context "increment" properties
            let! initialValue = requiredUInt32 context "initialValue" properties

            let counter =
                { WireSignalName = wireSignal
                  Modulus = modulus
                  Increment = increment
                  InitialValue = initialValue }

            match counterProfileError context counter with
            | Some(InvalidValue details) -> return! Error details
            | Some _ -> return! Error(sprintf "%s has an invalid profile" context)
            | None -> return counter
        }

    let private parseRxCounter index (element: JsonElement) =
        let context = sprintf "rxMessages[%d].counter" index

        result {
            let! properties = objectProperties context element
            do! ensureAllowedProperties context [ "wireSignal"; "modulus"; "increment" ] properties
            let! wireSignal = requiredString context "wireSignal" properties
            let! modulus = requiredUInt32 context "modulus" properties
            let! increment = requiredUInt32 context "increment" properties

            let counter =
                { WireSignalName = wireSignal
                  Modulus = modulus
                  Increment = increment }

            match rxCounterProfileError context counter with
            | Some(InvalidValue details) -> return! Error details
            | Some _ -> return! Error(sprintf "%s has an invalid profile" context)
            | None -> return counter
        }

    let private parseRxMessage index (element: JsonElement) =
        let context = sprintf "rxMessages[%d]" index

        result {
            let! properties = objectProperties context element
            do! ensureAllowedProperties context [ "message"; "crc"; "counter" ] properties
            let! message = requiredString context "message" properties

            let! crc =
                match tryProperty "crc" properties with
                | None -> Ok None
                | Some value -> parseCrc "rxMessages" index value |> Result.map Some

            let! counter =
                match tryProperty "counter" properties with
                | None -> Ok None
                | Some value -> parseRxCounter index value |> Result.map Some

            return
                { MessageName = message
                  Crc = crc
                  Counter = counter }
        }

    let private parseRxMessages (properties: JsonProperty list) =
        match tryProperty "rxMessages" properties with
        | None -> Ok []
        | Some value -> parseArray "Binding set key 'rxMessages'" parseRxMessage value

    let private parseTxMessage index (element: JsonElement) =
        let context = sprintf "txMessages[%d]" index

        result {
            let! properties = objectProperties context element

            do! ensureAllowedProperties context [ "message"; "logicalMessageId"; "crc"; "counter" ] properties

            let! message = requiredString context "message" properties
            let! logicalId = requiredUInt32 context "logicalMessageId" properties

            let! crc =
                match tryProperty "crc" properties with
                | None -> Ok None
                | Some value -> parseCrc "txMessages" index value |> Result.map Some

            let! counter =
                match tryProperty "counter" properties with
                | None -> Ok None
                | Some value -> parseCounter index value |> Result.map Some

            return
                { MessageName = message
                  LogicalMessageId = logicalId
                  Crc = crc
                  Counter = counter }
        }

    let private parseTxMessages (properties: JsonProperty list) =
        match tryProperty "txMessages" properties with
        | None -> Ok []
        | Some value -> parseArray "Binding set key 'txMessages'" parseTxMessage value

    /// Parse a strict version 1 JSON binding set and validate all bindings.
    let parseBindingSet (json: string) : Result<BindingSet, ValidationError list> =
        try
            use document =
                JsonDocument.Parse(
                    json,
                    JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false)
                )

            let parsed =
                result {
                    let! properties = objectProperties "Binding set" document.RootElement

                    do!
                        ensureAllowedProperties
                            "Binding set"
                            [ "version"; "bindings"; "rxMessages"; "txMessages" ]
                            properties

                    let! version = requiredString "Binding set" "version" properties

                    if version <> "1" then
                        return! Error(sprintf "Binding set has unsupported version '%s'" version)
                    else
                        let! bindings = parseBindings properties
                        let! rxMessages = parseRxMessages properties
                        let! txMessages = parseTxMessages properties

                        return
                            { Bindings = bindings
                              RxMessages = rxMessages
                              TxMessages = txMessages }
                }

            match parsed with
            | Error details -> Error[InvalidJson details]
            | Ok bindingSet -> validateSet bindingSet
        with ex ->
            Error[InvalidJson ex.Message]
