namespace Signal.CANdy.Core

open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir

module Wire =

    /// A normalized wire-level signal independent of source syntax.
    type WireSignal =
        { Name: string
          StartBit: uint16
          Length: uint16
          Factor: float
          Offset: float
          Minimum: float option
          Maximum: float option
          Unit: string
          IsSigned: bool
          ByteOrder: ByteOrder
          MultiplexerIndicator: string option
          MultiplexerSwitchValue: int option }

    /// A normalized CAN or CAN FD message.
    type WireMessage =
        { Name: string
          Id: uint32
          IsExtended: bool
          Length: uint16
          Signals: WireSignal list }

    /// The canonical wire model consumed by later binding stages.
    type WireIr = { Messages: WireMessage list }

    let private coveredBits (signal: Signal) =
        let start = int signal.StartBit
        let length = int signal.Length

        match signal.ByteOrder with
        | Little -> [ for bit in 0 .. length - 1 -> start + bit ]
        | Big ->
            let byte0 = start / 8
            let bit0 = start % 8

            [ for bit in 0 .. length - 1 ->
                  let mutable currentByte = byte0
                  let mutable currentBit = bit0 - bit

                  while currentBit < 0 do
                      currentBit <- currentBit + 8
                      currentByte <- currentByte + 1

                  currentByte * 8 + currentBit ]

    let private normalizeSignal (messageLength: uint16) (signal: Signal) =
        let bits = coveredBits signal
        let frameBits = int messageLength * 8

        if signal.Length = 0us then
            Error(InvalidValue(sprintf "Signal '%s' has zero length." signal.Name))
        elif messageLength > 64us then
            Error(InvalidValue(sprintf "Message '%s' exceeds the 64-byte CAN FD limit." signal.Name))
        elif bits |> List.exists (fun bit -> bit < 0 || bit >= frameBits) then
            Error(
                InvalidValue(
                    sprintf
                        "Signal '%s' exceeds the %d-byte frame payload."
                        signal.Name
                        (int messageLength)
                )
            )
        else
            let normalizedStart =
                match signal.ByteOrder with
                | Little -> int signal.StartBit
                | Big -> bits |> List.min

            Ok
                { Name = signal.Name
                  StartBit = uint16 normalizedStart
                  Length = signal.Length
                  Factor = signal.Factor
                  Offset = signal.Offset
                  Minimum = signal.Minimum
                  Maximum = signal.Maximum
                  Unit = signal.Unit
                  IsSigned = signal.IsSigned
                  ByteOrder = signal.ByteOrder
                  MultiplexerIndicator = signal.MultiplexerIndicator
                  MultiplexerSwitchValue = signal.MultiplexerSwitchValue }

    let private normalizeMessage (message: Message) =
        if message.Length > 64us then
            Error(InvalidValue(sprintf "Message '%s' exceeds the 64-byte CAN FD limit." message.Name))
        else
            let rec normalizeSignals remaining normalized =
                match remaining with
                | [] -> Ok(List.rev normalized)
                | signal :: rest ->
                    match normalizeSignal message.Length signal with
                    | Error error -> Error error
                    | Ok normalizedSignal -> normalizeSignals rest (normalizedSignal :: normalized)

            match normalizeSignals message.Signals [] with
            | Error error -> Error error
            | Ok signals ->
                Ok
                    { Name = message.Name
                      Id = message.Id
                      IsExtended = message.IsExtended
                      Length = message.Length
                      Signals = signals }

    /// Adapt the existing public IR to the normalized wire IR.
    let ofIr (ir: Ir) : Result<WireIr, ValidationError list> =
        let rec normalizeMessages remaining normalized =
            match remaining with
            | [] -> Ok { Messages = List.rev normalized }
            | message :: rest ->
                match normalizeMessage message with
                | Error error -> Error [ error ]
                | Ok normalizedMessage -> normalizeMessages rest (normalizedMessage :: normalized)

        normalizeMessages ir.Messages []
