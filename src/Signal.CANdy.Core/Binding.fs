namespace Signal.CANdy.Core

open System.Text.Json
open Signal.CANdy.Core.Errors

module Binding =

    /// Conversion policy applied after wire extraction.
    type Conversion =
        | Identity
        | Affine of factor: float * offset: float

    /// Explicit relationship between one pool signal and one wire signal.
    type SignalBinding =
        { PoolSignalName: string
          MessageName: string
          WireSignalName: string
          Conversion: Conversion }

    /// Bindings are deliberately separate from both source models.
    type BindingSet = { Bindings: SignalBinding list }

    let validate (bindings: SignalBinding list) : Result<SignalBinding list, ValidationError list> =
        let duplicateNames =
            bindings
            |> List.groupBy (fun binding -> binding.PoolSignalName)
            |> List.choose (fun (name, values) ->
                if List.length values > 1 then
                    Some(InvalidValue(sprintf "Pool signal '%s' has multiple bindings." name))
                else
                    None)

        let conversionErrors =
            bindings
            |> List.choose (fun binding ->
                match binding.Conversion with
                | Affine(0.0, _) ->
                    Some(
                        InvalidValue(
                            sprintf
                                "Pool signal '%s' has an affine conversion with factor zero."
                                binding.PoolSignalName
                        )
                    )
                | _ -> None)

        let errors = duplicateNames @ conversionErrors

        if errors.IsEmpty then Ok bindings else Error errors

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

                if factor = 0.0 then
                    return! Error(sprintf "%s key 'factor' must be non-zero" context)
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

    let private parseBindings (properties: JsonProperty list) =
        result {
            let! value = requiredProperty "Binding set" "bindings" properties

            if value.ValueKind <> JsonValueKind.Array then
                return! Error("Binding set key 'bindings' must be an array")
            else
                let rec parseAll index elements =
                    match elements with
                    | [] -> Ok []
                    | element :: rest ->
                        result {
                            let! binding = parseBinding index element
                            let! remaining = parseAll (index + 1) rest
                            return binding :: remaining
                        }

                return! value.EnumerateArray() |> Seq.toList |> parseAll 0
        }

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
                    do! ensureAllowedProperties "Binding set" [ "version"; "bindings" ] properties
                    let! version = requiredString "Binding set" "version" properties

                    if version <> "1" then
                        return! Error(sprintf "Binding set has unsupported version '%s'" version)
                    else
                        let! bindings = parseBindings properties
                        return { Bindings = bindings }
                }

            match parsed with
            | Error details -> Error[InvalidJson details]
            | Ok bindingSet ->
                match validate bindingSet.Bindings with
                | Ok bindings -> Ok { Bindings = bindings }
                | Error errors -> Error errors
        with ex ->
            Error[InvalidJson ex.Message]
