namespace Signal.CANdy.Core

open System
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir

module Wire =

    /// Compatibility projection for classic one-level multiplexing.
    type MuxRole =
        | Unconditional
        | Selector
        | Branch of expected: int

    type MuxPredicate =
        { SelectorSignalName: string
          Expected: uint32 }

    /// A normalized wire-level signal independent of source syntax.
    type WireSignal =
        { Name: string
          StartBit: uint16
          LengthBits: uint16
          ByteOrder: ByteOrder
          IsSigned: bool
          Factor: float
          Offset: float
          Unit: string
          Min: float option
          Max: float option
          IsMuxSelector: bool
          MuxPath: MuxPredicate list
          Receivers: string list }

    /// A normalized CAN or CAN FD message.
    type WireMessage =
        { Name: string
          CanId: uint32
          IsExtended: bool
          LengthBytes: uint16
          Signals: WireSignal list }

    type WireModel = { Messages: WireMessage list }

    type WireSignal with
        member signal.Length = signal.LengthBits

        member signal.Mux =
            if signal.IsMuxSelector then
                Selector
            else
                match List.tryLast signal.MuxPath with
                | Some predicate -> Branch(int predicate.Expected)
                | None -> Unconditional

    type WireMessage with
        member message.Id = message.CanId
        member message.Length = message.LengthBytes

    type WireIr = WireModel

    let private normalizeStartBit (signal: Signal) =
        match signal.ByteOrder with
        | Little -> int signal.StartBit
        | Big ->
            let finalBit = int signal.StartBit + int signal.Length - 1
            (finalBit / 8) * 8 + (7 - finalBit % 8)

    let private unsupportedMux signalName details =
        UnsupportedFeature(sprintf "Signal '%s' uses unsupported multiplexing: %s" signalName details)

    let private isSelector (signal: Signal) =
        signal.MultiplexerIndicator = Some "M"
        || signal.MultiplexerIndicator = Some "mM"

    let private resolvePaths (message: Message) =
        let byName =
            message.Signals |> List.map (fun signal -> signal.Name, signal) |> Map.ofList

        let roots =
            message.Signals
            |> List.filter (fun signal -> signal.MultiplexerIndicator = Some "M")

        let root = roots |> List.tryHead

        let rec resolve visited (signal: Signal) =
            if Set.contains signal.Name visited then
                Error(unsupportedMux signal.Name "selector cycle")
            else
                match signal.ExtendedMuxParent with
                | Some parent when parent.SelectorSignalName = signal.Name ->
                    Error(unsupportedMux signal.Name "self-referencing selector")
                | Some parent ->
                    match byName |> Map.tryFind parent.SelectorSignalName with
                    | None ->
                        Error(unsupportedMux signal.Name (sprintf "missing parent '%s'" parent.SelectorSignalName))
                    | Some selector when not (isSelector selector) ->
                        Error(unsupportedMux signal.Name (sprintf "parent '%s' is not a selector" selector.Name))
                    | Some selector when
                        selector.IsSigned
                        || selector.Length < 1us
                        || selector.Length > 32us
                        || selector.Factor <> 1.0
                        || selector.Offset <> 0.0
                        ->
                        Error(unsupportedMux selector.Name "selector must be unsigned 1..32 bits with identity scaling")
                    | Some selector ->
                        let maximum =
                            if selector.Length = 32us then
                                uint64 UInt32.MaxValue
                            else
                                (1UL <<< int selector.Length) - 1UL

                        if uint64 parent.Expected > maximum then
                            Error(unsupportedMux signal.Name "predicate exceeds selector width")
                        else
                            resolve (Set.add signal.Name visited) selector
                            |> Result.map (fun prefix ->
                                prefix
                                @ [ { SelectorSignalName = selector.Name
                                      Expected = parent.Expected } ])
                | None ->
                    match signal.MultiplexerIndicator, signal.MultiplexerSwitchValue with
                    | None, None
                    | Some "M", None -> Ok []
                    | Some "m", Some expected when expected >= 0 ->
                        match root with
                        | Some selector ->
                            Ok
                                [ { SelectorSignalName = selector.Name
                                    Expected = uint32 expected } ]
                        | None -> Error(unsupportedMux signal.Name "branch has no root selector")
                    | Some "mM", _ -> Error(unsupportedMux signal.Name "nested selector is missing a parent")
                    | None, Some _ -> Error(unsupportedMux signal.Name "a switch value without an indicator")
                    | Some "m", None -> Error(unsupportedMux signal.Name "branch indicator is missing a switch value")
                    | Some indicator, _ -> Error(unsupportedMux signal.Name (sprintf "indicator '%s'" indicator))

        let rootErrors =
            if roots.Length > 1 then
                [ unsupportedMux message.Name "more than one root selector" ]
            else
                []

        let resolved =
            message.Signals |> List.map (fun signal -> signal, resolve Set.empty signal)

        let errors =
            rootErrors
            @ (resolved
               |> List.choose (fun (_, result) ->
                   match result with
                   | Error error -> Some error
                   | Ok path when path.Length > 4 -> Some(unsupportedMux message.Name "path depth exceeds 4")
                   | Ok _ -> None))

        if errors.IsEmpty then
            Ok(
                resolved
                |> List.map (fun (signal, result) -> signal.Name, Result.defaultValue [] result)
                |> Map.ofList
            )
        else
            Error errors

    let private normalizeSignal (message: Message) paths (signal: Signal) =
        let startBit = normalizeStartBit signal
        let endBit = startBit + int signal.Length
        let frameBits = int message.Length * 8

        let errors =
            [ if endBit > frameBits then
                  SignalExceedsFrame(message.Name, signal.Name, endBit, frameBits)

              if signal.CrcMeta.IsSome then
                  UnsupportedFeature "CRC/counter signals are not supported in Wire IR v1" ]

        { Name = signal.Name
          StartBit = uint16 startBit
          LengthBits = signal.Length
          ByteOrder = signal.ByteOrder
          IsSigned = signal.IsSigned
          Factor = signal.Factor
          Offset = signal.Offset
          Unit = signal.Unit
          Min = signal.Minimum
          Max = signal.Maximum
          IsMuxSelector = isSelector signal
          MuxPath = paths |> Map.find signal.Name
          Receivers = signal.Receivers },
        errors

    let private rangesOverlap (leftStart: uint16) (leftLength: uint16) (rightStart: uint16) (rightLength: uint16) =
        uint32 leftStart < uint32 rightStart + uint32 rightLength
        && uint32 rightStart < uint32 leftStart + uint32 leftLength

    let private mutuallyExclusive (left: WireSignal) (right: WireSignal) =
        let rightPath =
            right.MuxPath
            |> List.map (fun predicate -> predicate.SelectorSignalName, predicate.Expected)
            |> Map.ofList

        left.MuxPath
        |> List.exists (fun predicate ->
            rightPath
            |> Map.tryFind predicate.SelectorSignalName
            |> Option.exists (fun expected -> expected <> predicate.Expected))

    let private overlapErrors messageName (signals: WireSignal list) =
        [ for leftIndex in 0 .. signals.Length - 1 do
              for rightIndex in leftIndex + 1 .. signals.Length - 1 do
                  let left = signals.[leftIndex]
                  let right = signals.[rightIndex]

                  if
                      rangesOverlap left.StartBit left.LengthBits right.StartBit right.LengthBits
                      && not (mutuallyExclusive left right)
                  then
                      InvalidValue(
                          sprintf "Wire signals '%s' and '%s' overlap in message '%s'." left.Name right.Name messageName
                      ) ]

    let private normalizeMessage (message: Message) =
        let messageErrors =
            [ if message.Length > 64us then
                  MessageTooLong(message.Name, int message.Length) ]

        match resolvePaths message with
        | Error errors ->
            { Name = message.Name
              CanId = message.Id
              IsExtended = message.IsExtended
              LengthBytes = message.Length
              Signals = [] },
            messageErrors @ errors
        | Ok paths ->
            let signals, signalErrors =
                message.Signals |> List.map (normalizeSignal message paths) |> List.unzip

            { Name = message.Name
              CanId = message.Id
              IsExtended = message.IsExtended
              LengthBytes = message.Length
              Signals = signals },
            messageErrors @ List.concat signalErrors @ overlapErrors message.Name signals

    /// Merge normalized wire sources in manifest order without last-wins semantics.
    let merge (sources: (string * WireIr) list) : Result<WireIr, ValidationError list> =
        let messages = sources |> List.collect (snd >> _.Messages)

        let nameErrors =
            messages
            |> List.groupBy _.Name
            |> List.choose (fun (name, values) ->
                if values.Length > 1 then
                    Some(InvalidValue(sprintf "Duplicate wire message name '%s'." name))
                else
                    None)

        let keyErrors =
            messages
            |> List.groupBy (fun message -> message.IsExtended, message.CanId)
            |> List.choose (fun ((extended, canId), values) ->
                if values.Length > 1 then
                    Some(InvalidValue(sprintf "Duplicate wire CAN key (extended=%b, id=%u)." extended canId))
                else
                    None)

        let errors = nameErrors @ keyErrors

        if errors.IsEmpty then
            Ok { Messages = messages }
        else
            Error errors

    let toWireModel (ir: Ir) : Result<WireModel, ValidationError list> =
        let messages, errors = ir.Messages |> List.map normalizeMessage |> List.unzip
        let allErrors = List.concat errors

        if allErrors.IsEmpty then
            Ok { Messages = messages }
        else
            Error allErrors
