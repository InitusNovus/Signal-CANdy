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
          Offset: float
          Storage: StorageType
          Mux: MuxRole
          MuxSelectorSlot: uint16 option
          MuxExpected: uint32 option }

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

    let private isIntegerStorage storage =
        match storage with
        | U8
        | U16
        | U32
        | U64
        | I8
        | I16
        | I32
        | I64 -> true
        | F32
        | F64 -> false

    let private resolveBinding
        (pool: PoolContract)
        (wire: WireIr)
        (binding: SignalBinding)
        : Result<WireMessage * DecodePlan, ValidationError> =
        match poolSlotIndex pool binding.PoolSignalName with
        | None -> Error(InvalidValue(sprintf "Pool signal '%s' was not found." binding.PoolSignalName))
        | Some slotIndex ->
            match findWireMessage wire binding.MessageName with
            | None -> Error(InvalidValue(sprintf "Wire message '%s' was not found." binding.MessageName))
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

                        if isIntegerStorage poolSignal.Storage && (factor <> 1.0 || offset <> 0.0) then
                            Error(
                                InvalidValue(
                                    sprintf
                                        "Pool signal '%s' uses integer storage with a non-identity conversion."
                                        binding.PoolSignalName
                                )
                            )
                        else
                            Ok(
                                message,
                                ({ PoolSignalName = binding.PoolSignalName
                                   WireSignalName = binding.WireSignalName
                                   PoolSlotIndex = slotIndex
                                   StartBit = wireSignal.StartBit
                                   Length = wireSignal.LengthBits
                                   ByteOrder = wireSignal.ByteOrder
                                   IsSigned = wireSignal.IsSigned
                                   Factor = factor
                                   Offset = offset
                                   Storage = poolSignal.Storage
                                   Mux = wireSignal.Mux
                                   MuxSelectorSlot = None
                                   MuxExpected = None }
                                : DecodePlan)
                            )

    let private resolveMessageMux (message: WireMessage) (plans: DecodePlan list) =
        let selectors =
            plans
            |> List.filter (fun plan ->
                match plan.Mux with
                | Selector -> true
                | _ -> false)

        let hasBranches =
            plans
            |> List.exists (fun plan ->
                match plan.Mux with
                | Branch _ -> true
                | _ -> false)

        if List.length selectors > 1 then
            Error[InvalidValue(sprintf "Message '%s' has more than one bound multiplex selector." message.Name)]
        elif hasBranches && selectors.IsEmpty then
            Error[InvalidValue(sprintf "Message '%s' has multiplexed branches but no bound selector." message.Name)]
        else
            let selectorSlot = selectors |> List.tryHead |> Option.map _.PoolSlotIndex

            let resolvedPlans =
                plans
                |> List.map (fun plan ->
                    match plan.Mux, selectorSlot with
                    | Branch expected, Some slot ->
                        { plan with
                            MuxSelectorSlot = Some slot
                            MuxExpected = Some(uint32 expected) }
                    | _ -> plan)

            Ok
                { Name = message.Name
                  Id = message.CanId
                  IsExtended = message.IsExtended
                  Length = message.LengthBytes
                  Plans = resolvedPlans }

    /// Resolve explicit bindings into a schema suitable for runtime-image lowering.
    let link (pool: PoolContract) (wire: WireIr) (bindingSet: BindingSet) : Result<LinkedSchema, ValidationError list> =
        match Pool.validate pool with
        | Error errors -> Error errors
        | Ok _ ->
            match Binding.validate bindingSet.Bindings with
            | Error errors -> Error errors
            | Ok(bindings: SignalBinding list) ->
                let resolved: Result<WireMessage * DecodePlan, ValidationError> list =
                    bindings |> List.map (fun binding -> resolveBinding pool wire binding)

                let bindingErrors =
                    resolved
                    |> List.choose (function
                        | Error error -> Some error
                        | Ok _ -> None)

                if not bindingErrors.IsEmpty then
                    Error bindingErrors
                else
                    let linkedMessages =
                        resolved
                        |> List.choose (function
                            | Error _ -> None
                            | Ok entry -> Some entry)
                        |> List.groupBy (fun (message, _) -> message.Name)
                        |> List.map (fun (_, entries) ->
                            let message, _ = entries.Head
                            resolveMessageMux message (entries |> List.map snd))

                    let muxErrors =
                        linkedMessages
                        |> List.collect (function
                            | Error errors -> errors
                            | Ok _ -> [])

                    if muxErrors.IsEmpty then
                        Ok
                            { Messages =
                                linkedMessages
                                |> List.choose (function
                                    | Ok message -> Some message
                                    | Error _ -> None) }
                    else
                        Error muxErrors
