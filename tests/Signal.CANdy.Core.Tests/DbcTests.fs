namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open System
open System.IO
open DbcParserLib
open Signal.CANdy.Core.Dbc
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir

module DbcTests =

    /// Helper: write DBC content to a temp file and return its path
    let private createTempDbcFile (content: string) =
        let tempPath = Path.GetTempFileName()
        File.WriteAllText(tempPath, content)
        tempPath

    // -------------------------------------------------------
    // C-1 regression tests: IO errors must propagate, not be swallowed
    // -------------------------------------------------------

    [<Fact>]
    let ``parseDbcFile returns IoError for non-existent file`` () =
        let result = parseDbcFile "absolutely_does_not_exist.dbc"

        match result with
        | Error(ParseError.IoError _) -> () // expected
        | Error e -> failwithf "Expected IoError, got: %A" e
        | Ok _ -> failwith "Expected IoError, got Ok"

    [<Fact>]
    let ``parseDbcFile returns IoError for unreadable path`` () =
        // A directory path is not a valid DBC file ??reading it should fail
        let dirPath = Path.GetTempPath()
        let result = parseDbcFile dirPath

        match result with
        | Error(ParseError.IoError _) -> ()
        | Error e -> failwithf "Expected IoError, got: %A" e
        | Ok _ -> failwith "Expected IoError, got Ok"

    // -------------------------------------------------------
    // C-1 regression: validation still works correctly
    // -------------------------------------------------------

    [<Fact>]
    let ``parseDbcFile succeeds for valid DBC`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:

BO_ 100 MESSAGE_1: 8 Vector__XXX
 SG_ Signal_1 : 0|8@1+ (1,0) [0|255] "" Vector__XXX
 SG_ Signal_2 : 8|16@1+ (0.1,0) [0|100] "Unit" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            let result = parseDbcFile path

            match result with
            | Ok ir ->
                ir.Messages.Length |> should equal 1
                ir.Messages.[0].Signals.Length |> should equal 2
            | Error e -> failwithf "Expected success, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``parseDbcFile returns InvalidDbc for duplicate message IDs`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:

BO_ 100 MESSAGE_1: 8 Vector__XXX
 SG_ Signal_1 : 0|8@1+ (1,0) [0|255] "" Vector__XXX
BO_ 100 MESSAGE_2: 8 Vector__XXX
 SG_ Signal_2 : 8|16@1+ (0.1,0) [0|100] "Unit" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Error(ParseError.InvalidDbc msg) -> msg |> should haveSubstring "Duplicate message ID 100"
            | Error e -> failwithf "Expected InvalidDbc, got: %A" e
            | Ok _ -> failwith "Expected error for duplicate IDs"
        finally
            File.Delete(path)

    [<Fact>]
    let ``parseDbcFile returns InvalidDbc for overlapping signals`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:

BO_ 100 MESSAGE_1: 8 Vector__XXX
 SG_ Signal_1 : 0|8@1+ (1,0) [0|255] "" Vector__XXX
 SG_ Signal_2 : 7|16@1+ (0.1,0) [0|100] "Unit" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Error(ParseError.InvalidDbc msg) -> msg |> should haveSubstring "overlaps"
            | Error e -> failwithf "Expected InvalidDbc, got: %A" e
            | Ok _ -> failwith "Expected error for overlapping signals"
        finally
            File.Delete(path)

    [<Fact>]
    let ``parseDbcFile returns InvalidDbc for signal exceeding DLC`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:

BO_ 100 MESSAGE_1: 2 Vector__XXX
 SG_ Signal_1 : 8|16@1+ (1,0) [0|255] "" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Error(ParseError.InvalidDbc msg) -> msg |> should haveSubstring "exceeds"
            | Error e -> failwithf "Expected InvalidDbc, got: %A" e
            | Ok _ -> failwith "Expected error for DLC exceed"
        finally
            File.Delete(path)

    // -------------------------------------------------------
    // C-1 regression: signal metadata parsing works
    // -------------------------------------------------------

    [<Fact>]
    let ``parseDbcFile correctly parses signal metadata (endianness and sign)`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:

BO_ 100 MESSAGE_1: 8 Vector__XXX
 SG_ Signal_LE_Unsigned : 0|8@1+ (1,0) [0|255] "" Vector__XXX
 SG_ Signal_BE_Signed : 15|16@0- (0.1,-100) [-100|100] "Unit" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Ok ir ->
                let msg = ir.Messages |> List.exactlyOne
                let leSig = msg.Signals |> List.find (fun s -> s.Name = "Signal_LE_Unsigned")
                leSig.ByteOrder |> should equal ByteOrder.Little
                leSig.IsSigned |> should equal false
                let beSig = msg.Signals |> List.find (fun s -> s.Name = "Signal_BE_Signed")
                beSig.ByteOrder |> should equal ByteOrder.Big
                beSig.IsSigned |> should equal true
            | Error e -> failwithf "Expected success, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``parseDbcFile correctly parses multiplexer info`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:

BO_ 300 MUX_MSG: 8 Vector__XXX
 SG_ Switch M : 0|4@1+ (1,0) [0|15] "" Vector__XXX
 SG_ Branch0 m0 : 4|8@1+ (1,0) [0|255] "" Vector__XXX
 SG_ Branch1 m1 : 4|8@1+ (1,0) [0|255] "" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Ok ir ->
                let msg = ir.Messages |> List.exactlyOne
                let sw = msg.Signals |> List.find (fun s -> s.Name = "Switch")
                sw.MultiplexerIndicator |> should equal (Some "M")
                let b0 = msg.Signals |> List.find (fun s -> s.Name = "Branch0")
                b0.MultiplexerIndicator |> should equal (Some "m")
                b0.MultiplexerSwitchValue |> should equal (Some 0)
                let b1 = msg.Signals |> List.find (fun s -> s.Name = "Branch1")
                b1.MultiplexerIndicator |> should equal (Some "m")
                b1.MultiplexerSwitchValue |> should equal (Some 1)
            | Error e -> failwithf "Expected success, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``BE signal gets ByteOrder.Big even when not in metaMap`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:

BO_ 501 MSG_COMP_BE: 8 Vector__XXX
SG_ BE_16: 7|16@0+ (1,0) [0|65535] "" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Ok ir ->
                let beMsg = ir.Messages |> List.exactlyOne
                let beSignal = beMsg.Signals |> List.exactlyOne
                beSignal.ByteOrder |> should equal ByteOrder.Big
            | Error e -> failwithf "Expected success, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``DbcParserLib ByteOrder and IsSigned mapping from comprehensive_test`` () =
        let comprehensiveDbcPath =
            Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "comprehensive_test.dbc"))

        let parsed = Parser.ParseFromPath(comprehensiveDbcPath)

        let beMsg = parsed.Messages |> Seq.find (fun m -> m.Name = "MSG_COMP_BE")
        let leMsg = parsed.Messages |> Seq.find (fun m -> m.Name = "MSG_COMP_LE")
        let signedMsg = parsed.Messages |> Seq.find (fun m -> m.Name = "MSG_COMP_SIGNED")

        let beSignal = beMsg.Signals |> Seq.find (fun s -> s.Name = "BE_16")
        let leSignal = leMsg.Signals |> Seq.find (fun s -> s.Name = "LE_16")
        let signedSignal = signedMsg.Signals |> Seq.find (fun s -> s.Name = "S_LE_16")
        let unsignedSignal = leMsg.Signals |> Seq.find (fun s -> s.Name = "LE_12_CROSS")

        // DbcParserLib.Signal.ByteOrder: type=byte (numeric), 0=BigEndian, 1=LittleEndian
        let beByteOrderRaw = Convert.ToInt32(beSignal.ByteOrder)
        let leByteOrderRaw = Convert.ToInt32(leSignal.ByteOrder)
        beSignal.ByteOrder.GetType() |> should equal typeof<byte>
        beByteOrderRaw |> should equal 0
        leByteOrderRaw |> should equal 1

        let signedIsSignedProperty = signedSignal.GetType().GetProperty("IsSigned")
        let unsignedIsSignedProperty = unsignedSignal.GetType().GetProperty("IsSigned")
        signedIsSignedProperty |> should equal null
        unsignedIsSignedProperty |> should equal null

        // Signedness is not exposed as Signal.IsSigned in DbcParserLib v1.7.0.
        // For these known signals, signedness is inferable from min range in the parsed model.
        signedSignal.Minimum < 0.0 |> should equal true
        unsignedSignal.Minimum < 0.0 |> should equal false

    [<Fact>]
    let ``parseDbcFile correctly parses value tables`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:

BO_ 200 VT_MSG: 8 Vector__XXX
 SG_ Mode : 0|8@1+ (1,0) [0|255] "" Vector__XXX
 SG_ State : 8|8@1+ (1,0) [0|255] "" Vector__XXX

VAL_ 200 Mode 0 "OFF" 1 "ON" 2 "AUTO" ;
VAL_ 200 State 0 "IDLE" 1 "RUN" 2 "STOP" ;
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Ok ir ->
                let msg = ir.Messages |> List.find (fun m -> m.Name = "VT_MSG")
                let mode = msg.Signals |> List.find (fun s -> s.Name = "Mode")
                let state = msg.Signals |> List.find (fun s -> s.Name = "State")
                let vtMode = mode.ValueTable |> Option.defaultValue [] |> set
                let vtState = state.ValueTable |> Option.defaultValue [] |> set
                vtMode |> should equal (set [ (0, "OFF"); (1, "ON"); (2, "AUTO") ])
                vtState |> should equal (set [ (0, "IDLE"); (1, "RUN"); (2, "STOP") ])
            | Error e -> failwithf "Expected success, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``parseDbcFile succeeds for empty DBC (no messages)`` () =
        let dbc =
            """
VERSION ""
NS_ :
BS_:
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Ok ir -> ir.Messages.Length |> should equal 0
            | Error e -> failwithf "Expected success (empty DBC), got: %A" e
        finally
            File.Delete(path)

    // -------------------------------------------------------
    // T12: IsCrc / IsCounter heuristic detection (name substring)
    // -------------------------------------------------------

    [<Theory>]
    [<InlineData("CRC")>]
    [<InlineData("crc")>]
    [<InlineData("Checksum")>]
    [<InlineData("CHECKSUM")>]
    [<InlineData("EngineCRC8")>]
    [<InlineData("MsgChecksum")>]
    let ``IsCrc heuristic detects CRC signals by name substring`` (signalName: string) =
        let dbc =
            $"""
VERSION ""
NS_ :
BS_:

BO_ 100 MSG: 8 Vector__XXX
 SG_ {signalName} : 0|8@1+ (1,0) [0|255] "" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Ok ir ->
                let msg = ir.Messages |> List.exactlyOne
                let signal = msg.Signals |> List.exactlyOne
                signal.IsCrc |> should equal true
            | Error e -> failwithf "Expected success, got: %A" e
        finally
            File.Delete(path)

    [<Theory>]
    [<InlineData("Counter")>]
    [<InlineData("COUNTER")>]
    [<InlineData("AliveCounter")>]
    [<InlineData("ALIVE_CNT")>]
    let ``IsCounter heuristic detects counter signals by name substring`` (signalName: string) =
        let dbc =
            $"""
VERSION ""
NS_ :
BS_:

BO_ 200 MSG2: 8 Vector__XXX
 SG_ {signalName} : 0|8@1+ (1,0) [0|255] "" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Ok ir ->
                let msg = ir.Messages |> List.exactlyOne
                let signal = msg.Signals |> List.exactlyOne
                signal.IsCounter |> should equal true
            | Error e -> failwithf "Expected success, got: %A" e
        finally
            File.Delete(path)

    [<Theory>]
    [<InlineData("Speed")>]
    [<InlineData("Voltage")>]
    [<InlineData("Temperature")>]
    let ``Non-CRC/non-Counter names are not detected`` (signalName: string) =
        let dbc =
            $"""
VERSION ""
NS_ :
BS_:

BO_ 300 MSG3: 8 Vector__XXX
 SG_ {signalName} : 0|8@1+ (1,0) [0|255] "" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Ok ir ->
                let msg = ir.Messages |> List.exactlyOne
                let signal = msg.Signals |> List.exactlyOne
                signal.IsCrc |> should equal false
                signal.IsCounter |> should equal false
            | Error e -> failwithf "Expected success, got: %A" e
        finally
            File.Delete(path)

    [<Fact>]
    let ``Current heuristic: CRC_OFF is detected as CRC (documented false positive)`` () =
        // The parser uses a substring match for "crc"; names like "CRC_OFF" therefore are detected as CRC under current behavior.
        let dbc =
            """
VERSION ""
NS_ :
BS_:

 BO_ 400 MSG4: 8 Vector__XXX
 SG_ CRC_OFF : 0|8@1+ (1,0) [0|255] "" Vector__XXX
"""

        let path = createTempDbcFile dbc

        try
            match parseDbcFile path with
            | Ok ir ->
                let signal = ir.Messages |> List.exactlyOne |> fun m -> m.Signals |> List.exactlyOne
                signal.IsCrc |> should equal true
            | Error e -> failwithf "Expected success, got: %A" e
        finally
            File.Delete(path)

