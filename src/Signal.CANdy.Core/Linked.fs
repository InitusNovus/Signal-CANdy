namespace Signal.CANdy.Core

open System
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.Wire

module Linked =

    type LinkedMuxPredicate =
        { SelectorSlot: uint16
          SelectorProgramName: string
          Expected: uint32 }

    /// A resolved extraction and conversion operation for one RX pool slot.
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
          IsMuxSelector: bool
          MuxPath: LinkedMuxPredicate list }

    /// A linked receive message with all source names resolved.
    type LinkedMessage =
        { Name: string
          Id: uint32
          IsExtended: bool
          Length: uint16
          Plans: DecodePlan list }

    /// Stable pool ABI metadata in pool-definition order.
    type PoolSlot =
        { Name: string
          Storage: StorageType
          Direction: Direction
          Min: float option
          Max: float option
          FreshnessMs: uint32 option }

    /// A resolved pool-to-wire insertion operation.
    type EncodePlan =
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
          PhysicalMin: float option
          PhysicalMax: float option
          IsMuxSelector: bool
          MuxPath: LinkedMuxPredicate list }

    type DecodePlan with
        member plan.Mux =
            if plan.IsMuxSelector then Selector
            elif plan.MuxPath.IsEmpty then Unconditional
            else Branch(int plan.MuxPath.Head.Expected)

        member plan.MuxSelectorSlot = plan.MuxPath |> List.tryHead |> Option.map _.SelectorSlot
        member plan.MuxExpected = plan.MuxPath |> List.tryHead |> Option.map _.Expected

    type EncodePlan with
        member plan.Mux =
            if plan.IsMuxSelector then Selector
            elif plan.MuxPath.IsEmpty then Unconditional
            else Branch(int plan.MuxPath.Head.Expected)

        member plan.MuxSelectorSlot = plan.MuxPath |> List.tryHead |> Option.map _.SelectorSlot
        member plan.MuxExpected = plan.MuxPath |> List.tryHead |> Option.map _.Expected

    /// Resolved stateful counter field for one transmitted message.
    type LinkedTxCounter =
        { WireSignalName: string
          StartBit: uint16
          Length: uint16
          ByteOrder: ByteOrder
          Modulus: uint32
          Increment: uint32
          InitialValue: uint32 }

    /// A linked transmitted message selected by an explicit logical ID.
    type LinkedTxMessage =
        { Name: string
          LogicalMessageId: uint32
          CanId: uint32
          IsExtended: bool
          Length: uint16
          Plans: EncodePlan list
          Counter: LinkedTxCounter option }

    /// Runtime-image linker input after references are resolved.
    type LinkedSchema =
        { PoolSlots: PoolSlot list
          Messages: LinkedMessage list
          TxMessages: LinkedTxMessage list }

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

    let private finite value = Double.IsFinite(value)

    let private optionalFinite =
        function
        | None -> true
        | Some value -> finite value

    let private intersectBounds firstMin firstMax secondMin secondMax =
        let minimum =
            match firstMin, secondMin with
            | Some left, Some right -> Some(max left right)
            | Some value, None
            | None, Some value -> Some value
            | None, None -> None

        let maximum =
            match firstMax, secondMax with
            | Some left, Some right -> Some(min left right)
            | Some value, None
            | None, Some value -> Some value
            | None, None -> None

        minimum, maximum

    type private Resolved =
        | ResolvedRx of WireMessage * DecodePlan
        | ResolvedTx of WireMessage * EncodePlan

    let private resolveBinding
        (pool: PoolContract)
        (wire: WireIr)
        (txNames: Set<string>)
        (binding: SignalBinding)
        : Result<Resolved, ValidationError> =
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
                    let factor, offset = effectiveConversion binding wireSignal

                    if poolSignal.Unit <> wireSignal.Unit then
                        Error(
                            InvalidValue(
                                sprintf
                                    "Unit mismatch for pool signal '%s' and wire signal '%s'."
                                    binding.PoolSignalName
                                    binding.WireSignalName
                            )
                        )
                    elif
                        not (finite factor)
                        || not (finite offset)
                        || not (optionalFinite poolSignal.Min)
                        || not (optionalFinite poolSignal.Max)
                        || not (optionalFinite wireSignal.Min)
                        || not (optionalFinite wireSignal.Max)
                    then
                        Error(
                            InvalidValue(
                                sprintf
                                    "Binding for pool signal '%s' contains a non-finite value."
                                    binding.PoolSignalName
                            )
                        )
                    elif factor = 0.0 then
                        Error(
                            InvalidValue(sprintf "Binding for pool signal '%s' has factor zero." binding.PoolSignalName)
                        )
                    elif isIntegerStorage poolSignal.Storage && (factor <> 1.0 || offset <> 0.0) then
                        Error(
                            InvalidValue(
                                sprintf
                                    "Pool signal '%s' uses integer storage with a non-identity conversion."
                                    binding.PoolSignalName
                            )
                        )
                    else
                        match poolSignal.Direction with
                        | Rx when txNames.Contains(message.Name) ->
                            Error(
                                InvalidValue(
                                    sprintf
                                        "RX pool signal '%s' cannot be used by TX message '%s'."
                                        poolSignal.Name
                                        message.Name
                                )
                            )
                        | Rx ->
                            Ok(
                                ResolvedRx(
                                    message,
                                    { PoolSignalName = binding.PoolSignalName
                                      WireSignalName = binding.WireSignalName
                                      PoolSlotIndex = slotIndex
                                      StartBit = wireSignal.StartBit
                                      Length = wireSignal.LengthBits
                                      ByteOrder = wireSignal.ByteOrder
                                      IsSigned = wireSignal.IsSigned
                                      Factor = factor
                                      Offset = offset
                                      Storage = poolSignal.Storage
                                      IsMuxSelector = wireSignal.IsMuxSelector
                                      MuxPath =
                                        wireSignal.MuxPath
                                        |> List.map (fun predicate ->
                                            { SelectorSlot = UInt16.MaxValue
                                              SelectorProgramName = predicate.SelectorSignalName
                                              Expected = predicate.Expected }) }
                                )
                            )
                        | Tx when not (txNames.Contains(message.Name)) ->
                            Error(
                                InvalidValue(
                                    sprintf
                                        "TX pool signal '%s' references message '%s', which is not declared in txMessages."
                                        poolSignal.Name
                                        message.Name
                                )
                            )
                        | Tx ->
                            let physicalMin, physicalMax =
                                intersectBounds poolSignal.Min poolSignal.Max wireSignal.Min wireSignal.Max

                            match physicalMin, physicalMax with
                            | Some minimum, Some maximum when minimum > maximum ->
                                Error(
                                    InvalidValue(
                                        sprintf
                                            "Pool and wire ranges do not intersect for TX signal '%s'."
                                            poolSignal.Name
                                    )
                                )
                            | _ ->
                                Ok(
                                    ResolvedTx(
                                        message,
                                        { PoolSignalName = binding.PoolSignalName
                                          WireSignalName = binding.WireSignalName
                                          PoolSlotIndex = slotIndex
                                          StartBit = wireSignal.StartBit
                                          Length = wireSignal.LengthBits
                                          ByteOrder = wireSignal.ByteOrder
                                          IsSigned = wireSignal.IsSigned
                                          Factor = factor
                                          Offset = offset
                                          Storage = poolSignal.Storage
                                          PhysicalMin = physicalMin
                                          PhysicalMax = physicalMax
                                          IsMuxSelector = wireSignal.IsMuxSelector
                                          MuxPath =
                                            wireSignal.MuxPath
                                            |> List.map (fun predicate ->
                                                { SelectorSlot = UInt16.MaxValue
                                                  SelectorProgramName = predicate.SelectorSignalName
                                                  Expected = predicate.Expected }) }
                                    )
                                )

    let private resolveMux<'T>
        (messageName: string)
        (getName: 'T -> string)
        (getSlot: 'T -> uint16)
        (getLength: 'T -> uint16)
        (getSigned: 'T -> bool)
        (getIsSelector: 'T -> bool)
        (getFactor: 'T -> float)
        (getOffset: 'T -> float)
        (getStorage: 'T -> StorageType)
        (getPath: 'T -> LinkedMuxPredicate list)
        (setPath: LinkedMuxPredicate list -> 'T -> 'T)
        (maxDepth: int)
        (plans: 'T list)
        : Result<'T list, ValidationError list> =
        let byName = plans |> List.map (fun plan -> getName plan, plan) |> Map.ofList

        let resolvePlan plan =
            let sourcePath = getPath plan

            if sourcePath.Length > maxDepth then
                Error(InvalidValue(sprintf "Message '%s' mux path exceeds depth %d." messageName maxDepth))
            else
                let rec resolve prefix remaining =
                    match remaining with
                    | [] -> Ok prefix
                    | predicate :: rest ->
                        match byName |> Map.tryFind predicate.SelectorProgramName with
                        | None ->
                            Error(
                                InvalidValue(
                                    sprintf
                                        "Message '%s' mux selector '%s' is not bound."
                                        messageName
                                        predicate.SelectorProgramName
                                )
                            )
                        | Some selector ->
                            let selectorPrefix = getPath selector

                            let expectedPrefix =
                                prefix |> List.map (fun item -> item.SelectorProgramName, item.Expected)

                            let actualPrefix =
                                selectorPrefix |> List.map (fun item -> item.SelectorProgramName, item.Expected)

                            if actualPrefix <> expectedPrefix then
                                Error(
                                    InvalidValue(
                                        sprintf "Message '%s' mux selector path is not a canonical prefix." messageName
                                    )
                                )
                            elif
                                not (getIsSelector selector)
                                || getSigned selector
                                || getLength selector < 1us
                                || getLength selector > 32us
                                || getFactor selector <> 1.0
                                || getOffset selector <> 0.0
                                || not (isIntegerStorage (getStorage selector))
                            then
                                Error(
                                    InvalidValue(
                                        sprintf
                                            "Message '%s' mux selector must use unsigned identity integer storage."
                                            messageName
                                    )
                                )
                            else
                                let maximum =
                                    if getLength selector = 32us then
                                        uint64 UInt32.MaxValue
                                    else
                                        (1UL <<< int (getLength selector)) - 1UL

                                if uint64 predicate.Expected > maximum then
                                    Error(
                                        InvalidValue(
                                            sprintf "Message '%s' has a mux value outside selector width." messageName
                                        )
                                    )
                                else
                                    resolve
                                        (prefix
                                         @ [ { SelectorSlot = getSlot selector
                                               SelectorProgramName = predicate.SelectorProgramName
                                               Expected = predicate.Expected } ])
                                        rest

                resolve [] sourcePath |> Result.map (fun path -> setPath path plan)

        let resolved = plans |> List.map resolvePlan

        let errors =
            resolved
            |> List.choose (function
                | Error error -> Some error
                | Ok _ -> None)

        if errors.IsEmpty then
            Ok(
                resolved
                |> List.choose (function
                    | Ok value -> Some value
                    | Error _ -> None)
            )
        else
            Error errors

    let private resolveDecodeMux messageName (plans: DecodePlan list) =
        resolveMux
            messageName
            (fun (plan: DecodePlan) -> plan.WireSignalName)
            (fun plan -> plan.PoolSlotIndex)
            (fun plan -> plan.Length)
            (fun plan -> plan.IsSigned)
            (fun plan -> plan.IsMuxSelector)
            (fun plan -> plan.Factor)
            (fun plan -> plan.Offset)
            (fun plan -> plan.Storage)
            (fun plan -> plan.MuxPath)
            (fun path plan -> { plan with MuxPath = path })
            4
            plans

    let private resolveEncodeMux messageName (plans: EncodePlan list) =
        resolveMux
            messageName
            (fun (plan: EncodePlan) -> plan.WireSignalName)
            (fun plan -> plan.PoolSlotIndex)
            (fun plan -> plan.Length)
            (fun plan -> plan.IsSigned)
            (fun plan -> plan.IsMuxSelector)
            (fun plan -> plan.Factor)
            (fun plan -> plan.Offset)
            (fun plan -> plan.Storage)
            (fun plan -> plan.MuxPath)
            (fun path plan -> { plan with MuxPath = path })
            1
            plans

    let private rangesOverlap startA lengthA startB lengthB =
        uint32 startA < uint32 startB + uint32 lengthB
        && uint32 startB < uint32 startA + uint32 lengthA

    let private legalBranchOverlap (left: EncodePlan) (right: EncodePlan) =
        let rightPath =
            right.MuxPath
            |> List.map (fun predicate -> predicate.SelectorProgramName, predicate.Expected)
            |> Map.ofList

        left.MuxPath
        |> List.exists (fun predicate ->
            rightPath
            |> Map.tryFind predicate.SelectorProgramName
            |> Option.exists (fun expected -> expected <> predicate.Expected))

    let private txPlanOverlapErrors messageName (plans: EncodePlan list) =
        [ for leftIndex in 0 .. plans.Length - 1 do
              for rightIndex in leftIndex + 1 .. plans.Length - 1 do
                  let left = plans.[leftIndex]
                  let right = plans.[rightIndex]

                  if
                      rangesOverlap left.StartBit left.Length right.StartBit right.Length
                      && not (legalBranchOverlap left right)
                  then
                      InvalidValue(
                          sprintf
                              "TX message '%s' signals '%s' and '%s' overlap."
                              messageName
                              left.WireSignalName
                              right.WireSignalName
                      ) ]

    let private resolveCounter
        (wireMessage: WireMessage)
        (bindings: SignalBinding list)
        (plans: EncodePlan list)
        (counter: TxCounterBinding)
        =
        match findWireSignal wireMessage counter.WireSignalName with
        | None ->
            Error(
                InvalidValue(
                    sprintf "Counter signal '%s' was not found in message '%s'." counter.WireSignalName wireMessage.Name
                )
            )
        | Some signal ->
            let poolBound =
                bindings
                |> List.exists (fun binding ->
                    binding.MessageName = wireMessage.Name
                    && binding.WireSignalName = counter.WireSignalName)

            let overlaps =
                plans
                |> List.exists (fun plan -> rangesOverlap signal.StartBit signal.LengthBits plan.StartBit plan.Length)

            let modulusFits =
                if counter.Modulus = 0u then
                    signal.LengthBits = 32us
                elif signal.LengthBits = 32us then
                    true
                else
                    uint64 counter.Modulus <= (1UL <<< int signal.LengthBits)

            if signal.IsSigned then
                Error(InvalidValue(sprintf "Counter signal '%s' must be unsigned." signal.Name))
            elif signal.Mux <> Unconditional then
                Error(InvalidValue(sprintf "Counter signal '%s' must be unconditional." signal.Name))
            elif signal.Factor <> 1.0 || signal.Offset <> 0.0 then
                Error(InvalidValue(sprintf "Counter signal '%s' must use identity scaling." signal.Name))
            elif signal.LengthBits < 1us || signal.LengthBits > 32us then
                Error(InvalidValue(sprintf "Counter signal '%s' must be 1..32 bits." signal.Name))
            elif poolBound then
                Error(InvalidValue(sprintf "Counter signal '%s' may not also be pool-bound." signal.Name))
            elif overlaps then
                Error(InvalidValue(sprintf "Counter signal '%s' overlaps a TX signal." signal.Name))
            elif not modulusFits then
                Error(InvalidValue(sprintf "Counter profile for '%s' does not fit its wire width." signal.Name))
            else
                Ok
                    { WireSignalName = signal.Name
                      StartBit = signal.StartBit
                      Length = signal.LengthBits
                      ByteOrder = signal.ByteOrder
                      Modulus = counter.Modulus
                      Increment = counter.Increment
                      InitialValue = counter.InitialValue }

    /// Resolve explicit RX/TX bindings into a schema suitable for runtime-image lowering.
    let link (pool: PoolContract) (wire: WireIr) (bindingSet: BindingSet) : Result<LinkedSchema, ValidationError list> =
        let initialErrors =
            [ match Pool.validate pool with
              | Error errors -> yield! errors
              | Ok _ -> ()

              match Binding.validateSet bindingSet with
              | Error errors -> yield! errors
              | Ok _ -> () ]

        if not initialErrors.IsEmpty then
            Error initialErrors
        else
            let txNames =
                bindingSet.TxMessages
                |> List.map (fun message -> message.MessageName)
                |> Set.ofList

            let resolved = bindingSet.Bindings |> List.map (resolveBinding pool wire txNames)

            let resolutionErrors =
                resolved
                |> List.choose (function
                    | Error error -> Some error
                    | Ok _ -> None)

            let resolvedValues =
                resolved
                |> List.choose (function
                    | Ok value -> Some value
                    | Error _ -> None)

            let rxEntries =
                resolvedValues
                |> List.choose (function
                    | ResolvedRx(message, plan) -> Some(message, plan)
                    | _ -> None)

            let txEntries =
                resolvedValues
                |> List.choose (function
                    | ResolvedTx(message, plan) -> Some(message, plan)
                    | _ -> None)

            let duplicateRxErrors =
                rxEntries
                |> List.groupBy (fun (_, plan) -> plan.PoolSlotIndex)
                |> List.choose (fun (_, values) ->
                    if values.Length > 1 then
                        Some(
                            InvalidValue(
                                sprintf "RX pool signal '%s' has multiple writers." (snd values.Head).PoolSignalName
                            )
                        )
                    else
                        None)

            let freshnessWriterErrors =
                pool.Signals
                |> List.choose (fun signal ->
                    match signal.FreshnessMs with
                    | None -> None
                    | Some _ ->
                        let writers =
                            rxEntries
                            |> List.filter (fun (_, plan) -> plan.PoolSignalName = signal.Name)
                            |> List.length

                        if writers = 1 then
                            None
                        else
                            Some(
                                InvalidValue(
                                    sprintf
                                        "Freshness-enabled RX pool signal '%s' must have exactly one writer."
                                        signal.Name
                                )
                            ))

            let duplicateTxErrors =
                txEntries
                |> List.groupBy (fun (message, plan) -> message.Name, plan.PoolSlotIndex)
                |> List.choose (fun ((messageName, _), values) ->
                    if values.Length > 1 then
                        Some(
                            InvalidValue(
                                sprintf
                                    "TX message '%s' binds pool signal '%s' more than once."
                                    messageName
                                    (snd values.Head).PoolSignalName
                            )
                        )
                    else
                        None)

            let mixedDirectionErrors =
                let rxNames = rxEntries |> List.map (fun (message, _) -> message.Name) |> Set.ofList

                let txEntryNames =
                    txEntries |> List.map (fun (message, _) -> message.Name) |> Set.ofList

                Set.intersect rxNames txEntryNames
                |> Set.toList
                |> List.map (fun name -> InvalidValue(sprintf "Message '%s' mixes RX and TX bindings." name))

            let rxMessages, rxMuxErrors =
                rxEntries
                |> List.groupBy (fun (message, _) -> message.Name)
                |> List.map (fun (_, entries) ->
                    let message = fst entries.Head

                    match resolveDecodeMux message.Name (entries |> List.map snd) with
                    | Ok plans ->
                        Some(
                            { Name = message.Name
                              Id = message.CanId
                              IsExtended = message.IsExtended
                              Length = message.LengthBytes
                              Plans = plans }
                            : LinkedMessage
                        ),
                        []
                    | Error errors -> None, errors)
                |> List.unzip

            let txMessages, txMessageErrors =
                bindingSet.TxMessages
                |> List.map (fun declaration ->
                    match findWireMessage wire declaration.MessageName with
                    | None ->
                        None, [ InvalidValue(sprintf "Wire message '%s' was not found." declaration.MessageName) ]
                    | Some message ->
                        let plans =
                            txEntries
                            |> List.choose (fun (candidate, plan) ->
                                if candidate.Name = message.Name then Some plan else None)

                        let resolvedPlans, muxErrors =
                            match resolveEncodeMux message.Name plans with
                            | Ok value -> value, []
                            | Error errors -> plans, errors

                        let counter, counterErrors =
                            match declaration.Counter with
                            | None -> None, []
                            | Some profile ->
                                match resolveCounter message bindingSet.Bindings resolvedPlans profile with
                                | Ok value -> Some value, []
                                | Error error -> None, [ error ]

                        let emptyErrors =
                            if resolvedPlans.IsEmpty && counter.IsNone then
                                [ InvalidValue(sprintf "TX message '%s' has no signal or counter." message.Name) ]
                            else
                                []

                        let overlapErrors = txPlanOverlapErrors message.Name resolvedPlans

                        let errors = muxErrors @ counterErrors @ emptyErrors @ overlapErrors

                        if errors.IsEmpty then
                            Some
                                { Name = message.Name
                                  LogicalMessageId = declaration.LogicalMessageId
                                  CanId = message.CanId
                                  IsExtended = message.IsExtended
                                  Length = message.LengthBytes
                                  Plans = resolvedPlans
                                  Counter = counter },
                            []
                        else
                            None, errors)
                |> List.unzip

            let errors =
                resolutionErrors
                @ duplicateRxErrors
                @ freshnessWriterErrors
                @ duplicateTxErrors
                @ mixedDirectionErrors
                @ (rxMuxErrors |> List.concat)
                @ (txMessageErrors |> List.concat)

            if not errors.IsEmpty then
                Error errors
            else
                Ok
                    { PoolSlots =
                        pool.Signals
                        |> List.map (fun signal ->
                            { Name = signal.Name
                              Storage = signal.Storage
                              Direction = signal.Direction
                              Min = signal.Min
                              Max = signal.Max
                              FreshnessMs = signal.FreshnessMs })
                      Messages = rxMessages |> List.choose id
                      TxMessages = txMessages |> List.choose id }
