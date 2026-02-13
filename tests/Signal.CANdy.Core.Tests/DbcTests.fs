namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open System.IO
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
        // A directory path is not a valid DBC file — reading it should fail
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
