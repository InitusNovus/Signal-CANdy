namespace Signal.CANdy.Core.Tests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core.Dbc
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Wire

module WireTests =

    let private examplePath name =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", name))

    let private createTempDbcFile (content: string) =
        let path = Path.GetTempFileName()
        File.WriteAllText(path, content)
        path

    let private parseWire path =
        match parseDbcFile path with
        | Error error -> failwithf "Expected DBC parse to succeed, got: %A" error
        | Ok ir ->
            match toWireModel ir with
            | Ok wire -> wire
            | Error errors -> failwithf "Expected Wire normalization to succeed, got: %A" errors

    let private findMessage name (wire: WireModel) =
        wire.Messages |> List.find (fun message -> message.Name = name)

    let private findSignal name (message: WireMessage) =
        message.Signals |> List.find (fun signal -> signal.Name = name)

    let private signal name startBit length : Signal =
        { Name = name
          StartBit = startBit
          Length = length
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

    let private message name length (signals: Signal list) : Message =
        { Name = name
          Id = 100u
          IsExtended = false
          Length = length
          Signals = signals
          Sender = "Vector__XXX"
          Receivers = []
          CrcCounterMode = None }

    let private containsError predicate result =
        match result with
        | Error errors -> List.exists predicate errors
        | Ok _ -> false

    [<Fact>]
    let ``sample.dbc normalizes little-endian signals`` () =
        let wire = parseWire (examplePath "sample.dbc")
        let message = findMessage "MESSAGE_1" wire
        let first = findSignal "Signal_1" message
        let second = findSignal "Signal_2" message

        first.StartBit |> should equal 0us
        first.LengthBits |> should equal 8us
        first.ByteOrder |> should equal Little
        second.StartBit |> should equal 8us
        second.LengthBits |> should equal 16us

    [<Fact>]
    let ``motorola_lsb_suite normalizes big-endian starts`` () =
        let wire = parseWire (examplePath "motorola_lsb_suite.dbc")
        let message = findMessage "LSB_TEST" wire
        let firstByte = findSignal "LSB_BE_8" message
        let secondByte = findSignal "LSB_BE_8_B1" message
        let signedFourthByte = findSignal "LSB_BE_S8_B3" message

        firstByte.StartBit |> should equal 0us
        firstByte.ByteOrder |> should equal Big
        secondByte.StartBit |> should equal 8us
        secondByte.ByteOrder |> should equal Big
        signedFourthByte.StartBit |> should equal 24us
        signedFourthByte.ByteOrder |> should equal Big
        signedFourthByte.IsSigned |> should equal true

    [<Fact>]
    let ``canfd_test normalizes mid-frame motorola`` () =
        let wire = parseWire (examplePath "canfd_test.dbc")
        let message = findMessage "FD_MSG" wire
        let motorola = findSignal "FD_Sig_Mid_BE" message
        let high = findSignal "FD_Sig_High" message

        motorola.StartBit |> should equal 265us
        motorola.LengthBits |> should equal 8us
        motorola.ByteOrder |> should equal Big
        high.StartBit |> should equal 480us
        high.LengthBits |> should equal 16us
        high.ByteOrder |> should equal Little
        message.LengthBytes |> should equal 64us

    [<Fact>]
    let ``multiplex_suite maps mux roles`` () =
        let wire = parseWire (examplePath "multiplex_suite.dbc")
        let message = findMessage "MUX_MSG" wire

        (findSignal "MuxSwitch" message).Mux |> should equal Selector
        (findSignal "Sig_m1" message).Mux |> should equal (Branch 1)
        (findSignal "Sig_m2" message).Mux |> should equal (Branch 2)
        (findSignal "Base_8" message).Mux |> should equal Unconditional

    [<Fact>]
    let ``signal exceeding DLC is rejected`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:
BO_ 800 FRAME: 8 Vector__XXX
 SG_ TooWide : 60|16@1+ (1,0) [0|65535] "" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            let parsed = DbcParserLib.Parser.ParseFromPath(path)
            let parsedMessage = parsed.Messages |> Seq.exactlyOne
            let parsedSignal = parsedMessage.Signals |> Seq.exactlyOne

            let ir: Ir =
                { Messages =
                    [ message
                          parsedMessage.Name
                          parsedMessage.DLC
                          [ signal parsedSignal.Name parsedSignal.StartBit parsedSignal.Length ] ] }

            toWireModel ir
            |> containsError (function
                | SignalExceedsFrame("FRAME", "TooWide", 76, 64) -> true
                | _ -> false)
            |> should equal true
        finally
            File.Delete(path)

    [<Fact>]
    let ``message longer than 64 bytes is rejected`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:
BO_ 900 BIG: 65 Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Error error -> failwithf "Expected DBC parse to succeed, got: %A" error
            | Ok ir ->
                toWireModel ir
                |> containsError (function
                    | MessageTooLong("BIG", 65) -> true
                    | _ -> false)
                |> should equal true
        finally
            File.Delete(path)

    [<Fact>]
    let ``unsupported mux range indicator is rejected`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:
BO_ 901 RANGE: 8 Vector__XXX
 SG_ X m1-2 : 0|8@1+ (1,0) [0|255] "" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            let indicator =
                File.ReadLines(path)
                |> Seq.find (fun line -> line.TrimStart().StartsWith("SG_", StringComparison.Ordinal))
                |> fun line -> line.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries).[2]

            let rangedSignal =
                { signal "X" 0us 8us with
                    MultiplexerIndicator = Some indicator }

            toWireModel { Messages = [ message "RANGE" 8us [ rangedSignal ] ] }
            |> containsError (function
                | UnsupportedFeature details when details.Contains("m1-2", StringComparison.Ordinal) -> true
                | _ -> false)
            |> should equal true
        finally
            File.Delete(path)

    [<Fact>]
    let ``CRC-configured signals are rejected in v1`` () =
        let crcSignal =
            { signal "PayloadCrc" 0us 8us with
                IsCrc = true }

        toWireModel { Messages = [ message "CRC_FRAME" 8us [ crcSignal ] ] }
        |> containsError (function
            | UnsupportedFeature "CRC/counter signals are not supported in Wire IR v1" -> true
            | _ -> false)
        |> should equal true
