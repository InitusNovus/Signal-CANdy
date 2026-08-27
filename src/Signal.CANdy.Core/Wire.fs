namespace Signal.CANdy.Core

open System
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir

module Wire =

    /// A signal's role in classic DBC multiplexing.
    type MuxRole =
        | Unconditional
        | Selector
        | Branch of expected: int

    /// A normalized wire-level signal independent of source syntax.
    /// Value tables remain available in Ir but are display metadata and are not carried into Wire IR v1.
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
          Mux: MuxRole
          Receivers: string list }

    /// A normalized CAN or CAN FD message.
    type WireMessage =
        { Name: string
          CanId: uint32
          IsExtended: bool
          LengthBytes: uint16
          Signals: WireSignal list }

    /// The canonical wire model consumed by later binding stages.
    type WireModel = { Messages: WireMessage list }

    // Read-only compatibility for downstream stages that predate the canonical v1 field names.
    type WireSignal with
        member signal.Length = signal.LengthBits

    type WireMessage with
        member message.Id = message.CanId
        member message.Length = message.LengthBytes

    type WireIr = WireModel

    /// DbcParserLib 1.7.0 returns Motorola StartBit unchanged from the DBC's
    /// MSB-first sawtooth coordinate (for example, `263|8@0+` is returned as 263).
    /// Convert the signal's DBC-stream final bit to the runtime's LSB-first frame coordinate.
    let private normalizeStartBit (signal: Signal) =
        match signal.ByteOrder with
        | Little -> int signal.StartBit
        | Big ->
            let finalBit = int signal.StartBit + int signal.Length - 1
            (finalBit / 8) * 8 + (7 - finalBit % 8)

    let private unsupportedMux signalName details =
        UnsupportedFeature(sprintf "Signal '%s' uses unsupported multiplexing: %s" signalName details)

    let private muxRole (signal: Signal) : Result<MuxRole, ValidationError> =
        match signal.MultiplexerIndicator, signal.MultiplexerSwitchValue with
        | None, None -> Ok Unconditional
        | None, Some _ -> Error(unsupportedMux signal.Name "a switch value without an indicator")
        | Some "M", _ -> Ok Selector
        // Dbc.fs represents m<N> as indicator "m" plus MultiplexerSwitchValue.
        | Some "m", Some expected -> Ok(Branch expected)
        | Some "m", None -> Error(unsupportedMux signal.Name "branch indicator is missing a switch value")
        // Also accept an Ir supplied directly with the complete DBC m<N> token.
        | Some indicator, switchValue when indicator.StartsWith("m", StringComparison.Ordinal) ->
            match Int32.TryParse(indicator.AsSpan(1)) with
            | true, expected ->
                match switchValue with
                | Some actual when actual <> expected ->
                    Error(unsupportedMux signal.Name "branch indicator and switch value disagree")
                | _ -> Ok(Branch expected)
            | false, _ -> Error(unsupportedMux signal.Name (sprintf "indicator '%s'" indicator))
        | Some indicator, _ -> Error(unsupportedMux signal.Name (sprintf "indicator '%s'" indicator))

    let private normalizeSignal (message: Message) (signal: Signal) =
        let startBit = normalizeStartBit signal
        let endBit = startBit + int signal.Length
        let frameBits = int message.Length * 8

        let errors =
            [ if endBit > frameBits then
                  SignalExceedsFrame(message.Name, signal.Name, endBit, frameBits)

              // CRC remains outside runtime image v1. Counter metadata may pass
              // through normalization; only an explicit TX binding gives it state.
              if signal.CrcMeta.IsSome then
                  UnsupportedFeature "CRC/counter signals are not supported in Wire IR v1"

              match muxRole signal with
              | Error error -> error
              | Ok _ -> () ]

        let mux = muxRole signal |> Result.defaultValue Unconditional

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
          Mux = mux
          Receivers = signal.Receivers },
        errors

    let private normalizeMessage (message: Message) =
        let messageErrors =
            [ if message.Length > 64us then
                  MessageTooLong(message.Name, int message.Length) ]

        let signals, signalErrors =
            message.Signals |> List.map (normalizeSignal message) |> List.unzip

        { Name = message.Name
          CanId = message.Id
          IsExtended = message.IsExtended
          LengthBytes = message.Length
          Signals = signals },
        messageErrors @ List.concat signalErrors

    /// Adapt the source Ir to Wire IR v1, normalizing bit coordinates and accumulating every diagnostic.
    let toWireModel (ir: Ir) : Result<WireModel, ValidationError list> =
        let messages, errors = ir.Messages |> List.map normalizeMessage |> List.unzip

        let allErrors = List.concat errors

        if List.isEmpty allErrors then
            Ok { Messages = messages }
        else
            Error allErrors
