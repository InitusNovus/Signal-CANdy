namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open System.IO
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Config
open Signal.CANdy.Core.Codegen
open Signal.CANdy.Core.Dbc
open Signal.CANdy.Core.Errors

module EdgeCaseTests =

    /// Default config for codegen tests
    let private defaultConfig: Config =
        { PhysType = "float"
          PhysMode = "double"
          RangeCheck = false
          Dispatch = "binary_search"
          CrcCounterCheck = false
          MotorolaStartBit = "msb"
          FilePrefix = "sc_" }

    /// Helper: create temp output directory
    let private createTempOutDir () =
        let dir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
        Directory.CreateDirectory(dir) |> ignore
        dir

    /// Helper: clean up temp directory
    let private cleanupDir dir =
        if Directory.Exists(dir) then
            Directory.Delete(dir, true)

    /// Helper: write DBC content to a temp file and return its path
    let private createTempDbcFile (content: string) =
        let tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".dbc")
        File.WriteAllText(tempPath, content)
        tempPath

    // -------------------------------------------------------
    // H-1f: Edge case tests
    // -------------------------------------------------------

    [<Fact>]
    let ``codegen succeeds for empty DBC (no messages)`` () =
        let emptyIr = { Messages = [] }
        let outDir = createTempOutDir ()

        try
            match generate emptyIr outDir defaultConfig with
            | Ok files ->
                // Should still generate utils and registry (even if empty)
                files.Sources.Length |> should be (greaterThanOrEqualTo 2)
                files.Headers.Length |> should be (greaterThanOrEqualTo 2)
                // No per-message files — sources are just sc_utils.c + sc_registry.c
                let sourceNames = files.Sources |> List.map Path.GetFileName
                sourceNames |> should contain "sc_utils.c"
                sourceNames |> should contain "sc_registry.c"
            | Error e -> failwithf "Expected Ok for empty IR, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``codegen succeeds for Motorola LSB DBC`` () =
        let lsbConfig =
            { defaultConfig with
                MotorolaStartBit = "lsb" }

        let dbcContent =
            """
VERSION ""
NS_ :
BS_:

BO_ 500 MOTO_MSG: 8 Vector__XXX
 SG_ MotorSig : 0|8@0+ (1,0) [0|255] "" Vector__XXX
"""

        let dbcPath = createTempDbcFile dbcContent
        let outDir = createTempOutDir ()

        try
            match parseDbcFile dbcPath with
            | Ok ir ->
                match generate ir outDir lsbConfig with
                | Ok files ->
                    files.Sources.Length |> should be (greaterThan 0)
                    files.Headers.Length |> should be (greaterThan 0)
                | Error e -> failwithf "Expected Ok from codegen, got: %A" e
            | Error e -> failwithf "Expected Ok from parse, got: %A" e
        finally
            File.Delete(dbcPath)
            cleanupDir outDir

    [<Fact>]
    let ``codegen succeeds for signed signals`` () =
        let ir =
            { Messages =
                [ { Name = "SIGNED_MSG"
                    Id = 600u
                    IsExtended = false
                    Length = 8us
                    Signals =
                      [ { Name = "SignedTemp"
                          StartBit = 0us
                          Length = 16us
                          Factor = 0.1
                          Offset = -40.0
                          Minimum = Some -40.0
                          Maximum = Some 80.0
                          Unit = "C"
                          IsSigned = true
                          IsCrc = false
                          IsCounter = false
                          ByteOrder = ByteOrder.Little
                          MultiplexerIndicator = None
                          MultiplexerSwitchValue = None
                          ValueTable = None
                          Receivers = []
                          CrcMeta = None
                          CounterMeta = None } ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = None } ] }

        let outDir = createTempOutDir ()

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let msgC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "signed_msg.c")

                let content = File.ReadAllText(msgC)
                // Signed signal should have sign extension code
                content |> should haveSubstring "1ULL <<"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    // -------------------------------------------------------
    // CAN FD edge case tests
    // -------------------------------------------------------

    [<Fact>]
    let ``codegen succeeds for CAN FD 64-byte message`` () =
        let fdIr =
            { Messages =
                [ { Name = "FD_TEST"
                    Id = 900u
                    IsExtended = false
                    Length = 64us
                    Signals =
                      [ { Name = "FD_Low"
                          StartBit = 0us
                          Length = 8us
                          Factor = 1.0
                          Offset = 0.0
                          Minimum = Some 0.0
                          Maximum = Some 255.0
                          Unit = ""
                          IsSigned = false
                          IsCrc = false
                          IsCounter = false
                          ByteOrder = ByteOrder.Little
                          MultiplexerIndicator = None
                          MultiplexerSwitchValue = None
                          ValueTable = None
                          Receivers = []
                          CrcMeta = None
                          CounterMeta = None }
                        { Name = "FD_High"
                          StartBit = 480us
                          Length = 16us
                          Factor = 0.1
                          Offset = 0.0
                          Minimum = None
                          Maximum = None
                          Unit = ""
                          IsSigned = false
                          IsCrc = false
                          IsCounter = false
                          ByteOrder = ByteOrder.Little
                          MultiplexerIndicator = None
                          MultiplexerSwitchValue = None
                          ValueTable = None
                          Receivers = []
                          CrcMeta = None
                          CounterMeta = None } ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = None } ] }

        let outDir = createTempOutDir ()

        try
            match generate fdIr outDir defaultConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "fd_test.c")
                let content = File.ReadAllText(msgC)
                // DLC check should use 64
                content |> should haveSubstring "dlc < 64"
                // memset should use 64 bytes
                content |> should haveSubstring "memset(data, 0, 64)"
                // out_dlc should be 64
                content |> should haveSubstring "*out_dlc = 64"
                // Signal at byte 60 (start_bit 480) should reference get_bits_le(data, 480, 16)
                content |> should haveSubstring "get_bits_le(data, 480, 16)"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``codegen succeeds for signal at byte position beyond 8`` () =
        let fdIr =
            { Messages =
                [ { Name = "FD_POS"
                    Id = 901u
                    IsExtended = false
                    Length = 32us
                    Signals =
                      [ { Name = "HighSig"
                          StartBit = 200us
                          Length = 8us
                          Factor = 1.0
                          Offset = 0.0
                          Minimum = None
                          Maximum = None
                          Unit = ""
                          IsSigned = false
                          IsCrc = false
                          IsCounter = false
                          ByteOrder = ByteOrder.Little
                          MultiplexerIndicator = None
                          MultiplexerSwitchValue = None
                          ValueTable = None
                          Receivers = []
                          CrcMeta = None
                          CounterMeta = None } ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = None } ] }

        let outDir = createTempOutDir ()

        try
            match generate fdIr outDir defaultConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "fd_pos.c")
                let content = File.ReadAllText(msgC)
                // Signal at start_bit=200 (byte 25) should work
                content |> should haveSubstring "get_bits_le(data, 200, 8)"
                content |> should haveSubstring "memset(data, 0, 32)"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``codegen succeeds for 64-bit signal in FD message`` () =
        let fdIr =
            { Messages =
                [ { Name = "FD_WIDE"
                    Id = 902u
                    IsExtended = false
                    Length = 64us
                    Signals =
                      [ { Name = "FullPayload"
                          StartBit = 0us
                          Length = 64us
                          Factor = 1.0
                          Offset = 0.0
                          Minimum = None
                          Maximum = None
                          Unit = ""
                          IsSigned = false
                          IsCrc = false
                          IsCounter = false
                          ByteOrder = ByteOrder.Little
                          MultiplexerIndicator = None
                          MultiplexerSwitchValue = None
                          ValueTable = None
                          Receivers = []
                          CrcMeta = None
                          CounterMeta = None } ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = None } ] }

        let outDir = createTempOutDir ()

        try
            match generate fdIr outDir defaultConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "fd_wide.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "get_bits_le(data, 0, 64)"
                content |> should haveSubstring "memset(data, 0, 64)"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``codegen succeeds for 64-bit signal`` () =
        let ir =
            { Messages =
                [ { Name = "WIDE_MSG"
                    Id = 700u
                    IsExtended = false
                    Length = 8us
                    Signals =
                      [ { Name = "BigValue"
                          StartBit = 0us
                          Length = 64us
                          Factor = 1.0
                          Offset = 0.0
                          Minimum = None
                          Maximum = None
                          Unit = ""
                          IsSigned = false
                          IsCrc = false
                          IsCounter = false
                          ByteOrder = ByteOrder.Little
                          MultiplexerIndicator = None
                          MultiplexerSwitchValue = None
                          ValueTable = None
                          Receivers = []
                          CrcMeta = None
                          CounterMeta = None } ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = None } ] }

        let outDir = createTempOutDir ()

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "wide_msg.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "get_bits_le(data, 0, 64)"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir
