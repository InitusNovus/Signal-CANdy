namespace Signal.CANdy.Core

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

        if duplicateNames.IsEmpty then
            Ok bindings
        else
            Error duplicateNames
