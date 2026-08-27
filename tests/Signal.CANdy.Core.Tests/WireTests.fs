namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Wire

module WireTests =

    [<Fact>]
    let ``Wire adapter preserves little endian fields`` () =
        let signal =
            { Name = "Speed"
              StartBit = 8us
              Length = 16us
              Factor = 0.1
              Offset = 0.0
              Minimum = Some 0.0
              Maximum = Some 100.0
              Unit = "km/h"
              IsSigned = false
              IsCrc = false
              IsCounter = false
              ByteOrder = Little
              MultiplexerIndicator = None
              MultiplexerSwitchValue = None
              ValueTable = None
              Receivers = []
              CrcMeta = None
              CounterMeta = None }

        let message =
            { Name = "Frame"
              Id = 100u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        match ofIr { Messages = [ message ] } with
        | Error errors -> failwithf "Unexpected error: %A" errors
        | Ok wire ->
            let actual = wire.Messages.Head.Signals.Head
            actual.StartBit |> should equal 8us
            actual.Length |> should equal 16us
            actual.Factor |> should equal 0.1

    [<Fact>]
    let ``Wire adapter normalizes Motorola signal start bit`` () =
        let signal =
            { Name = "Motorola"
              StartBit = 7us
              Length = 4us
              Factor = 1.0
              Offset = 0.0
              Minimum = None
              Maximum = None
              Unit = ""
              IsSigned = false
              IsCrc = false
              IsCounter = false
              ByteOrder = Big
              MultiplexerIndicator = None
              MultiplexerSwitchValue = None
              ValueTable = None
              Receivers = []
              CrcMeta = None
              CounterMeta = None }

        let message =
            { Name = "Frame"
              Id = 100u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        match ofIr { Messages = [ message ] } with
        | Error errors -> failwithf "Unexpected error: %A" errors
        | Ok wire -> wire.Messages.Head.Signals.Head.StartBit |> should equal 4us

    [<Fact>]
    let ``Wire adapter rejects signal outside frame`` () =
        let signal =
            { Name = "TooLong"
              StartBit = 63us
              Length = 2us
              Factor = 1.0
              Offset = 0.0
              Minimum = None
              Maximum = None
              Unit = ""
              IsSigned = false
              IsCrc = false
              IsCounter = false
              ByteOrder = Little
              MultiplexerIndicator = None
              MultiplexerSwitchValue = None
              ValueTable = None
              Receivers = []
              CrcMeta = None
              CounterMeta = None }

        let message =
            { Name = "Frame"
              Id = 100u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        match ofIr { Messages = [ message ] } with
        | Ok _ -> failwith "Expected an out-of-frame signal to be rejected."
        | Error [ InvalidValue details ] -> details.Contains("exceeds") |> should equal true
        | Error errors -> failwithf "Unexpected errors: %A" errors
