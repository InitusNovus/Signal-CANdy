namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open System.IO
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Config
open Signal.CANdy.Core.Codegen
open Signal.CANdy.Core.Errors

module CodegenTests =

    /// Default config for codegen tests
    let private defaultConfig: Config =
        { PhysType = "float"
          PhysMode = "double"
          RangeCheck = false
          Dispatch = "binary_search"
          CrcCounterCheck = false
          MotorolaStartBit = "msb"
          FilePrefix = "sc_" }

    /// A minimal single-signal for building test IR
    let private mkSignal name startBit length =
        { Name = name
          StartBit = startBit
          Length = length
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
          Receivers = [] }

    /// A minimal single-message IR for testing
    let private singleMessageIr =
        { Messages =
            [ { Name = "MESSAGE_1"
                Id = 100u
                IsExtended = false
                Length = 8us
                Signals = [ mkSignal "Signal_1" 0us 8us; mkSignal "Signal_2" 8us 16us ]
                Sender = "ECU"
                Receivers = [] } ] }

    /// Helper: create temp output directory
    let private createTempOutDir () =
        let dir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
        Directory.CreateDirectory(dir) |> ignore
        dir

    /// Helper: clean up temp directory
    let private cleanupDir dir =
        if Directory.Exists(dir) then
            Directory.Delete(dir, true)

    // -------------------------------------------------------
    // H-1d: Codegen.generate tests
    // -------------------------------------------------------

    [<Fact>]
    let ``generate creates expected files for single message`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                // Sources: sc_utils.c, sc_registry.c, message_1.c = 3
                files.Sources.Length |> should equal 3
                // Headers: sc_utils.h, sc_registry.h, utils.h (shim), registry.h (shim), message_1.h = 5
                files.Headers.Length |> should equal 5
                // All files should exist on disk
                files.Sources |> List.iter (fun f -> File.Exists(f) |> should equal true)
                files.Headers |> List.iter (fun f -> File.Exists(f) |> should equal true)
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate creates include guard in header`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "message_1.h")
                let content = File.ReadAllText(msgH)
                content |> should haveSubstring "#ifndef MESSAGE_1_H"
                content |> should haveSubstring "#define MESSAGE_1_H"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate creates extern C guards`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "message_1.h")
                let content = File.ReadAllText(msgH)
                content |> should haveSubstring "extern \"C\""
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate creates struct typedef`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "message_1.h")
                let content = File.ReadAllText(msgH)
                content |> should haveSubstring "typedef struct {"
                content |> should haveSubstring "} MESSAGE_1_t;"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate creates decode and encode functions`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "message_1.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "bool MESSAGE_1_decode("
                content |> should haveSubstring "bool MESSAGE_1_encode("
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate with phys_type fixed produces integer fast path`` () =
        let outDir = createTempOutDir ()

        let fixedConfig =
            { defaultConfig with
                PhysType = "fixed"
                PhysMode = "fixed_double" }
        // Use factor = 0.01 = 10^-2, offset = 0 (integral) -> should use llround fast path
        let ir =
            { Messages =
                [ { Name = "MSG_FIXED"
                    Id = 200u
                    IsExtended = false
                    Length = 8us
                    Signals =
                      [ { mkSignal "Temp" 0us 16us with
                            Factor = 0.01
                            Offset = 0.0 } ]
                    Sender = "ECU"
                    Receivers = [] } ] }

        try
            match generate ir outDir fixedConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "msg_fixed.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "llround"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate with dispatch direct_map produces switch`` () =
        let outDir = createTempOutDir ()

        let directMapConfig =
            { defaultConfig with
                Dispatch = "direct_map" }

        try
            match generate singleMessageIr outDir directMapConfig with
            | Ok files ->
                let regC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "sc_registry.c")

                let content = File.ReadAllText(regC)
                content |> should haveSubstring "switch (id)"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    // -------------------------------------------------------
    // CAN FD: Utils code generation tests
    // -------------------------------------------------------

    [<Fact>]
    let ``generate utils.c contains n_bytes for FD-safe LE accessors`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let utilsC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "sc_utils.c")

                let content = File.ReadAllText(utilsC)
                // Must NOT have the old hardcoded "< 8" loop bound
                content.Contains("i < 8 && (byte_offset + i) < 8") |> should equal false
                // Must have the new n_bytes pattern
                content |> should haveSubstring "n_bytes"
                // Must have the UINT64_MAX safe mask for 64-bit signals
                content |> should haveSubstring "length == 64"
                content |> should haveSubstring "UINT64_MAX"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate encode uses message length for memset in FD message`` () =
        let fdIr =
            { Messages =
                [ { Name = "FD_MSG"
                    Id = 800u
                    IsExtended = false
                    Length = 64us
                    Signals = [ mkSignal "FD_Sig" 0us 8us ]
                    Sender = "ECU"
                    Receivers = [] } ] }

        let outDir = createTempOutDir ()

        try
            match generate fdIr outDir defaultConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "fd_msg.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "memset(data, 0, 64)"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate encode uses 8 for classic CAN memset`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "message_1.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "memset(data, 0, 8)"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate with range_check true produces bounds check`` () =
        let outDir = createTempOutDir ()
        let rangeConfig = { defaultConfig with RangeCheck = true }

        try
            match generate singleMessageIr outDir rangeConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "message_1.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "return false"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    // -------------------------------------------------------
    // L-4c: CAN FD DLC mapping codegen tests
    // -------------------------------------------------------

    [<Fact>]
    let ``generate utils.h contains DLC mapping prototypes`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let utilsH =
                    files.Headers |> List.find (fun f -> Path.GetFileName(f) = "sc_utils.h")

                let content = File.ReadAllText(utilsH)
                content |> should haveSubstring "uint8_t canfd_dlc_to_len(uint8_t dlc);"
                content |> should haveSubstring "uint8_t canfd_len_to_dlc(uint8_t len);"
                content |> should haveSubstring "CAN FD DLC"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate utils.c contains DLC mapping implementation`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let utilsC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "sc_utils.c")

                let content = File.ReadAllText(utilsC)
                content |> should haveSubstring "CANFD_DLC_TO_LEN[16]"
                content |> should haveSubstring "canfd_dlc_to_len"
                content |> should haveSubstring "canfd_len_to_dlc"
                content |> should haveSubstring "if (dlc > 15)"
                content |> should haveSubstring "if (len <= 8)"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    // -------------------------------------------------------
    // Comprehensive signal codegen pattern tests
    // -------------------------------------------------------

    [<Fact>]
    let ``generate creates get_bits_le call for 32-bit LE signal`` () =
        let ir =
            { Messages =
                [ { Name = "LE32_MSG"
                    Id = 550u
                    IsExtended = false
                    Length = 8us
                    Signals =
                      [ { mkSignal "Sig32" 0us 32us with
                            Maximum = None
                            Minimum = None } ]
                    Sender = "ECU"
                    Receivers = [] } ] }

        let outDir = createTempOutDir ()

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "le32_msg.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "get_bits_le(data, 0, 32)"
                content |> should haveSubstring "set_bits_le(data, 0, 32"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate creates get_bits_be call for 16-bit BE signal`` () =
        let ir =
            { Messages =
                [ { Name = "BE16_MSG"
                    Id = 551u
                    IsExtended = false
                    Length = 8us
                    Signals =
                      [ { mkSignal "SigBE16" 7us 16us with
                            ByteOrder = ByteOrder.Big
                            Maximum = None
                            Minimum = None } ]
                    Sender = "ECU"
                    Receivers = [] } ] }

        let outDir = createTempOutDir ()

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "be16_msg.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "get_bits_be(data, 7, 16)"
                content |> should haveSubstring "set_bits_be(data, 7, 16"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate creates sign extension for signed 16-bit signal`` () =
        let ir =
            { Messages =
                [ { Name = "SIGN16_MSG"
                    Id = 552u
                    IsExtended = false
                    Length = 8us
                    Signals =
                      [ { mkSignal "SigS16" 0us 16us with
                            IsSigned = true
                            Maximum = None
                            Minimum = None } ]
                    Sender = "ECU"
                    Receivers = [] } ] }

        let outDir = createTempOutDir ()

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let msgC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "sign16_msg.c")

                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "1ULL << (16 - 1)"
                content |> should haveSubstring "~((1ULL << 16) - 1)"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir
