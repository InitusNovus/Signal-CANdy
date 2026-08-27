namespace Signal.CANdy.Core

open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.Wire

module Linked =

    /// A resolved extraction and conversion operation for one pool slot.
    type DecodePlan =
        { PoolSignalName: string
          WireSignalName: string
          PoolSlotIndex: uint16
          StartBit: uint16
          Length: uint16
          ByteOrder: ByteOrder
          IsSigned: bool
          Factor: float
          Offset: float }

    /// A linked message with all source names resolved.
    type LinkedMessage =
        { Name: string
          Id: uint32
          IsExtended: bool
          Length: uint16
          Plans: DecodePlan list }

    /// The runtime-image linker input after references are resolved.
    type LinkedSchema = { Messages: LinkedMessage list }

    let private poolSlotIndex (pool: PoolContract) name =
        pool.Signals
        |> List.tryFindIndex (fun signal -> signal.Name = name)
        |> Option.map uint16

    let private findWireMessage (wire: WireIr) name =
        wire.Messages |> List.tryFind (fun message -> message.Name = name)

    let private findWireSignal (message: WireMessage) name =
        message.Signals |> List.tryFind (fun signal -> signal.Name = name)

    let private effectiveConversion (binding: SignalBinding) (wireSignal: WireSignal) =
        match binding.Conversion with
        | Identity -> wireSignal.Factor, wireSignal.Offset
        | Affine(factor, offset) -> factor, offset

    let private resolveBinding
        (pool: PoolContract)
        (wire: WireIr)
        (binding: SignalBinding)
        : Result<WireMessage * DecodePlan, ValidationError>
        =
        match poolSlotIndex pool binding.PoolSignalName with
        | None ->
            Error(InvalidValue(sprintf "Pool signal '%s' was not found." binding.PoolSignalName))
        | Some slotIndex ->
            match findWireMessage wire binding.MessageName with
            | None ->
                Error(InvalidValue(sprintf "Wire message '%s' was not found." binding.MessageName))
            | Some message ->
                match findWireSignal message binding.WireSignalName with
                | None ->
                    Error(
                        InvalidValue(
                            sprintf
                                "Wire signal '%s' was not found in message '%s'."
                                binding.WireSignalName
                                binding.MessageName
                        )
                    )
                | Some wireSignal ->
                    let poolSignal = pool.Signals |> List.item (int slotIndex)

                    if poolSignal.Unit <> wireSignal.Unit then
                        Error(
                            InvalidValue(
                                sprintf
                                    "Unit mismatch for pool signal '%s' and wire signal '%s'."
                                    binding.PoolSignalName
                                    binding.WireSignalName
                            )
                        )
                    else
                        let factor, offset = effectiveConversion binding wireSignal

                        Ok
                            (message,
                             ({ PoolSignalName = binding.PoolSignalName
                                WireSignalName = binding.WireSignalName
                                PoolSlotIndex = slotIndex
                                StartBit = wireSignal.StartBit
                                Length = wireSignal.Length
                                ByteOrder = wireSignal.ByteOrder
                                IsSigned = wireSignal.IsSigned
                                Factor = factor
                                Offset = offset }: DecodePlan))

    /// Resolve explicit bindings into a schema suitable for runtime-image lowering.
    let link (pool: PoolContract) (wire: WireIr) (bindingSet: BindingSet) : Result<LinkedSchema, ValidationError list> =
        match Pool.validate pool with
        | Error errors -> Error errors
        | Ok _ ->
            match Binding.validate bindingSet.Bindings with
            | Error errors -> Error errors
            | Ok (bindings: SignalBinding list) ->
                let resolved: Result<WireMessage * DecodePlan, ValidationError> list =
                    bindings
                    |> List.map (fun binding -> resolveBinding pool wire binding)

                let errors =
                    resolved
                    |> List.choose (function
                        | Error error -> Some error
                        | Ok _ -> None)

                if not errors.IsEmpty then
                    Error errors
                else
                    let messages =
                        resolved
                        |> List.choose (function
                            | Error _ -> None
                            | Ok (message, plan) -> Some(message, plan))
                        |> List.groupBy (fun (message, _) -> message.Name)
                        |> List.map (fun (_, entries) ->
                            let message, _ = entries.Head

                            { Name = message.Name
                              Id = message.Id
                              IsExtended = message.IsExtended
                              Length = message.Length
                              Plans = entries |> List.map snd })

                    Ok { Messages = messages }
