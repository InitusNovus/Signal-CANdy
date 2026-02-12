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
    let private defaultConfig : Config =
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
                Signals =
                    [ mkSignal "Signal_1" 0us 8us
                      mkSignal "Signal_2" 8us 16us ]
                Sender = "ECU"
                Receivers = [] } ] }

    /// Helper: create temp output directory
    let private createTempOutDir () =
        let dir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
        Directory.CreateDirectory(dir) |> ignore
        dir

    /// Helper: clean up temp directory
    let private cleanupDir dir =
        if Directory.Exists(dir) then Directory.Delete(dir, true)

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
        let fixedConfig = { defaultConfig with PhysType = "fixed"; PhysMode = "fixed_double" }
        // Use factor = 0.01 = 10^-2, offset = 0 (integral) -> should use llround fast path
        let ir =
            { Messages =
                [ { Name = "MSG_FIXED"
                    Id = 200u
                    IsExtended = false
                    Length = 8us
                    Signals =
                        [ { mkSignal "Temp" 0us 16us with Factor = 0.01; Offset = 0.0 } ]
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
        let directMapConfig = { defaultConfig with Dispatch = "direct_map" }
        try
            match generate singleMessageIr outDir directMapConfig with
            | Ok files ->
                let regC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "sc_registry.c")
                let content = File.ReadAllText(regC)
                content |> should haveSubstring "switch (id)"
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
