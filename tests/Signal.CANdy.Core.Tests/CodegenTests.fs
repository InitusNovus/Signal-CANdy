namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open System.IO
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Config
open Signal.CANdy.Core.Codegen
open Signal.CANdy.Core.Dbc
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
          FilePrefix = "sc_"
          CrcCounter = None }

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
          Receivers = []
          CrcMeta = None
          CounterMeta = None }

    /// A minimal single-message IR for testing
    let private singleMessageIr =
        { Messages =
            [ { Name = "MESSAGE_1"
                Id = 100u
                IsExtended = false
                Length = 8us
                Signals = [ mkSignal "Signal_1" 0us 8us; mkSignal "Signal_2" 8us 16us ]
                Sender = "ECU"
                Receivers = []
                CrcCounterMode = None } ] }

    /// Helper: create temp output directory
    let private createTempOutDir () =
        let dir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
        Directory.CreateDirectory(dir) |> ignore
        dir

    /// Helper: clean up temp directory
    let private cleanupDir dir =
        if Directory.Exists(dir) then
            Directory.Delete(dir, true)

    let private goldenPath fileName =
        Path.Combine(__SOURCE_DIRECTORY__, "golden", fileName)

    let private normalizeGeneratedText (text: string) =
        text.Replace("\r\n", "\n").TrimEnd('\n')

    let private assertGeneratedFileMatchesGolden generatedPath goldenFileName =
        let generated = File.ReadAllText(generatedPath) |> normalizeGeneratedText
        let golden = File.ReadAllText(goldenPath goldenFileName) |> normalizeGeneratedText
        generated |> should equal golden

    let private examplesPath fileName =
        Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", fileName)

    let private generateFromExample dbcFileName outDir =
        let dbcPath = examplesPath dbcFileName
        let configPath = examplesPath "config.yaml"

        let config =
            match Signal.CANdy.Core.Config.loadFromYaml configPath with
            | Ok cfg -> cfg
            | Error e -> failwithf "Expected config load success, got: %A" e

        let ir =
            match parseDbcFile dbcPath with
            | Ok parsed -> parsed
            | Error e -> failwithf "Expected DBC parse success, got: %A" e

        match generate ir outDir config with
        | Ok files -> files
        | Error e -> failwithf "Expected Ok, got: %A" e

    let private mkSignalWithValueTable name startBit length muxIndicator muxValue valueTable =
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
          MultiplexerIndicator = muxIndicator
          MultiplexerSwitchValue = muxValue
          ValueTable = valueTable
          Receivers = []
          CrcMeta = None
          CounterMeta = None }

    let private muxMessageIr =
        { Messages =
            [ { Name = "MUX_MSG"
                Id = 200u
                IsExtended = false
                Length = 8us
                Signals =
                    [ { mkSignal "MuxSwitch" 0us 4us with
                          Maximum = Some 3.0
                          MultiplexerIndicator = Some "M" }
                      mkSignal "Base_8" 8us 8us
                      { mkSignal "Sig_m1" 16us 8us with
                          MultiplexerIndicator = Some "m"
                          MultiplexerSwitchValue = Some 1 }
                      { mkSignal "Sig_m2" 16us 16us with
                          Maximum = Some 65535.0
                          MultiplexerIndicator = Some "m"
                          MultiplexerSwitchValue = Some 2 } ]
                Sender = "ECU"
                Receivers = []
                CrcCounterMode = None } ] }

    let private valueTableMuxIr =
        { Messages =
            [ { Name = "VT_MSG"
                Id = 300u
                IsExtended = false
                Length = 8us
                Signals =
                    [ mkSignalWithValueTable
                          "Mode"
                          0us
                          8us
                          (Some "M")
                          None
                          (Some [ 0, "OFF"; 1, "ON"; 2, "AUTO" ])
                      mkSignal "Base" 8us 8us
                      mkSignalWithValueTable
                          "State"
                          16us
                          8us
                          (Some "m")
                          (Some 0)
                          (Some [ 0, "IDLE"; 1, "RUN"; 2, "STOP" ])
                      mkSignalWithValueTable
                          "Error"
                          24us
                          8us
                          (Some "m")
                          (Some 1)
                          (Some [ 0, "OK"; 1, "WARN"; 2, "FAIL" ]) ]
                Sender = "ECU"
                Receivers = []
                CrcCounterMode = None } ] }

    let private crcSignalIr =
        { Messages =
            [ { Name = "CRC_MSG"
                Id = 400u
                IsExtended = false
                Length = 8us
                Signals =
                    [ mkSignal "Payload" 0us 8us
                      { mkSignal "MessageCrc" 8us 8us with
                          IsCrc = true
                          CrcMeta = None
                          CounterMeta = None } ]
                Sender = "ECU"
                Receivers = []
                CrcCounterMode = None } ] }

    let private mkCrcCounterConfig mode messageName crcCfg counterCfg : CrcCounterConfig =
        { Mode = mode
          Messages = Map.ofList [ messageName, { Crc = crcCfg; Counter = counterCfg } ]
          CustomAlgorithms = None }

    let private mkCrc8SaeJ1850Params : CrcAlgorithmParams =
        { Width = 8
          Poly = 0x1DUL
          Init = 0xFFUL
          XorOut = 0xFFUL
          ReflectIn = false
          ReflectOut = false }

    let private mkCrc88h2fParams : CrcAlgorithmParams =
        { Width = 8
          Poly = 0x2FUL
          Init = 0xFFUL
          XorOut = 0xFFUL
          ReflectIn = false
          ReflectOut = false }

    let private mkCrcSignalMeta algorithm parameters byteStart byteEnd : CrcSignalMeta =
        { Algorithm = algorithm
          Params = parameters
          ByteRange = {| Start = byteStart; End = byteEnd |}
          DataId = None }

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
                    Receivers = []
                    CrcCounterMode = None } ] }

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
    let ``CAN FD LE signal wider than 8 bytes n_bytes clamp absent from generated utils`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let utilsC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "sc_utils.c")

                let content = File.ReadAllText(utilsC)
                content.Contains("if (n_bytes > 8) n_bytes = 8;") |> should equal false
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

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
                    Receivers = []
                    CrcCounterMode = None } ] }

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
    let ``generate sc_utils.h matches golden output`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let utilsH =
                    files.Headers |> List.find (fun f -> Path.GetFileName(f) = "sc_utils.h")

                let generated = File.ReadAllText(utilsH) |> normalizeGeneratedText
                let golden = File.ReadAllText(goldenPath "sc_utils.h") |> normalizeGeneratedText
                generated |> should equal golden
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

    [<Fact>]
    let ``generate sc_utils.c matches golden output`` () =
        let outDir = createTempOutDir ()

        try
            match generate singleMessageIr outDir defaultConfig with
            | Ok files ->
                let utilsC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "sc_utils.c")

                let generated = File.ReadAllText(utilsC) |> normalizeGeneratedText
                let golden = File.ReadAllText(goldenPath "sc_utils.c") |> normalizeGeneratedText
                generated |> should equal golden
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate message_1 files match golden output`` () =
        let outDir = createTempOutDir ()

        try
            let files = generateFromExample "sample.dbc" outDir
            let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "message_1.h")
            let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "message_1.c")
            assertGeneratedFileMatchesGolden msgH "message_1.h"
            assertGeneratedFileMatchesGolden msgC "message_1.c"
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate mux_msg files match golden output`` () =
        let outDir = createTempOutDir ()

        try
            let files = generateFromExample "multiplex_suite.dbc" outDir
            let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "mux_msg.h")
            let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "mux_msg.c")
            assertGeneratedFileMatchesGolden msgH "mux_msg.h"
            assertGeneratedFileMatchesGolden msgC "mux_msg.c"
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate vt_msg files match golden output`` () =
        let outDir = createTempOutDir ()

        try
            let files = generateFromExample "value_table.dbc" outDir
            let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "vt_msg.h")
            let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "vt_msg.c")
            assertGeneratedFileMatchesGolden msgH "vt_msg.h"
            assertGeneratedFileMatchesGolden msgC "vt_msg.c"
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate sc_registry files match golden output`` () =
        let outDir = createTempOutDir ()

        try
            let files = generateFromExample "sample.dbc" outDir
            let regH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "sc_registry.h")
            let regC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "sc_registry.c")
            assertGeneratedFileMatchesGolden regH "sc_registry.h"
            assertGeneratedFileMatchesGolden regC "sc_registry.c"
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate rejects crc_counter_check when inferred CRC signal exists`` () =
        let outDir = createTempOutDir ()
        let cfg = { defaultConfig with CrcCounterCheck = true }

        try
            match generate crcSignalIr outDir cfg with
            | Error(CodeGenError.UnsupportedFeature msg) ->
                msg |> should haveSubstring "crc_counter_check=true"
                msg |> should haveSubstring "MessageCrc"
                msg |> should haveSubstring "CRC_MSG"
            | Error e -> failwithf "Expected UnsupportedFeature, got: %A" e
            | Ok _ -> failwith "Expected UnsupportedFeature when crc_counter_check is enabled"
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate still succeeds when crc_counter_check is enabled without inferred CRC or counter signals`` () =
        let outDir = createTempOutDir ()
        let cfg = { defaultConfig with CrcCounterCheck = true }

        try
            match generate singleMessageIr outDir cfg with
            | Ok files ->
                files.Sources |> List.map Path.GetFileName |> should contain "message_1.c"
                files.Headers |> List.map Path.GetFileName |> should contain "sc_registry.h"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate treats passthrough mode CRC signal as regular signal`` () =
        let outDir = createTempOutDir ()

        let passthroughCfg =
            { defaultConfig with
                CrcCounterCheck = true
                CrcCounter = Some(mkCrcCounterConfig "passthrough" "TEST_MSG" None None) }

        let ir =
            { Messages =
                [ { Name = "TEST_MSG"
                    Id = 200u
                    IsExtended = false
                    Length = 8us
                    Signals = [ mkSignal "PAYLOAD" 0us 8us; mkSignal "CHECKSUM" 8us 8us ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = Some CrcCounterMode.Passthrough } ] }

        try
            match generate ir outDir passthroughCfg with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "test_msg.c")
                let content = File.ReadAllText(msgC)
                content.Contains("sc_crc8_sae_j1850") |> should equal false
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate returns UnsupportedFeature for fail_fast mode with crc_counter block`` () =
        let outDir = createTempOutDir ()

        let failFastCfg =
            { defaultConfig with
                CrcCounterCheck = true
                CrcCounter =
                    Some(
                        mkCrcCounterConfig
                            "fail_fast"
                            "TEST_MSG"
                            (Some
                                { Signal = "CHECKSUM"
                                  Algorithm = "CRC8_SAE_J1850"
                                  ByteRange = (0, 0)
                                  DataId = None })
                            None
                    ) }

        let ir =
            { Messages =
                [ { Name = "TEST_MSG"
                    Id = 201u
                    IsExtended = false
                    Length = 8us
                    Signals = [ mkSignal "PAYLOAD" 0us 8us; mkSignal "CHECKSUM" 8us 8us ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = None } ] }

        try
            match generate ir outDir failFastCfg with
            | Error(CodeGenError.UnsupportedFeature msg) ->
                msg |> should haveSubstring "TEST_MSG"
                msg |> should haveSubstring "crc_counter.mode=validate"
            | Error e -> failwithf "Expected UnsupportedFeature, got: %A" e
            | Ok _ -> failwith "Expected UnsupportedFeature for fail_fast mode"
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate validate mode emits CRC-8 SAE J1850 decode and encode checks`` () =
        let outDir = createTempOutDir ()

        let crcMeta =
            mkCrcSignalMeta CrcAlgorithmId.CRC8_SAE_J1850 mkCrc8SaeJ1850Params 0 0

        let crcSignal =
            { (mkSignal "CHECKSUM" 8us 8us) with
                CrcMeta = Some crcMeta
                IsCrc = true }

        let ir =
            { Messages =
                [ { Name = "TEST_MSG"
                    Id = 202u
                    IsExtended = false
                    Length = 8us
                    Signals = [ mkSignal "PAYLOAD" 0us 8us; crcSignal ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = Some CrcCounterMode.Validate } ] }

        let cfg =
            { defaultConfig with
                CrcCounterCheck = true
                CrcCounter =
                    Some(
                        mkCrcCounterConfig
                            "validate"
                            "TEST_MSG"
                            (Some
                                { Signal = "CHECKSUM"
                                  Algorithm = "CRC8_SAE_J1850"
                                  ByteRange = (0, 0)
                                  DataId = None })
                            None
                    ) }

        try
            match generate ir outDir cfg with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "test_msg.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "sc_crc8_sae_j1850"
                content |> should haveSubstring "!= (uint8_t)msg->CHECKSUM) { return false; }"
                content |> should haveSubstring "uint8_t crc_val_CHECKSUM = sc_crc8_sae_j1850"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate validate mode emits CRC-8 8H2F verification call`` () =
        let outDir = createTempOutDir ()

        let crcMeta =
            mkCrcSignalMeta CrcAlgorithmId.CRC8_8H2F mkCrc88h2fParams 0 0

        let crcSignal =
            { (mkSignal "CHECKSUM" 8us 8us) with
                CrcMeta = Some crcMeta
                IsCrc = true }

        let ir =
            { Messages =
                [ { Name = "TEST_MSG"
                    Id = 203u
                    IsExtended = false
                    Length = 8us
                    Signals = [ mkSignal "PAYLOAD" 0us 8us; crcSignal ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = Some CrcCounterMode.Validate } ] }

        let cfg =
            { defaultConfig with
                CrcCounterCheck = true
                CrcCounter =
                    Some(
                        mkCrcCounterConfig
                            "validate"
                            "TEST_MSG"
                            (Some
                                { Signal = "CHECKSUM"
                                  Algorithm = "CRC8_8H2F"
                                  ByteRange = (0, 0)
                                  DataId = None })
                            None
                    ) }

        try
            match generate ir outDir cfg with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "test_msg.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "sc_crc8_8h2f"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate emits counter state type and declaration when counter metadata exists`` () =
        let outDir = createTempOutDir ()

        let counterSignal =
            { (mkSignal "COUNTER" 16us 4us) with
                CounterMeta = Some { Modulus = 15; Increment = 1 }
                IsCounter = true }

        let ir =
            { Messages =
                [ { Name = "TEST_MSG"
                    Id = 204u
                    IsExtended = false
                    Length = 8us
                    Signals = [ mkSignal "PAYLOAD" 0us 8us; counterSignal ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = Some CrcCounterMode.Validate } ] }

        let cfg =
            { defaultConfig with
                CrcCounterCheck = true
                CrcCounter =
                    Some(
                        mkCrcCounterConfig
                            "validate"
                            "TEST_MSG"
                            None
                            (Some
                                { Signal = "COUNTER"
                                  Modulus = 15
                                  Increment = 1 })
                    ) }

        try
            match generate ir outDir cfg with
            | Ok files ->
                let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "test_msg.h")
                let content = File.ReadAllText(msgH)
                content |> should haveSubstring "counter_state_t"
                content |> should haveSubstring "check_counter"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate emits check_counter function body with rollover modulus`` () =
        let outDir = createTempOutDir ()

        let counterSignal =
            { (mkSignal "COUNTER" 16us 4us) with
                CounterMeta = Some { Modulus = 15; Increment = 1 }
                IsCounter = true }

        let ir =
            { Messages =
                [ { Name = "TEST_MSG"
                    Id = 205u
                    IsExtended = false
                    Length = 8us
                    Signals = [ mkSignal "PAYLOAD" 0us 8us; counterSignal ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = Some CrcCounterMode.Validate } ] }

        let cfg =
            { defaultConfig with
                CrcCounterCheck = true
                CrcCounter =
                    Some(
                        mkCrcCounterConfig
                            "validate"
                            "TEST_MSG"
                            None
                            (Some
                                { Signal = "COUNTER"
                                  Modulus = 15
                                  Increment = 1 })
                    ) }

        try
            match generate ir outDir cfg with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "test_msg.c")
                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "check_counter"
                content |> should haveSubstring "% 15"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate without CRC config keeps message output free from CRC and counter artifacts`` () =
        let outDir = createTempOutDir ()

        let ir =
            { Messages =
                [ { Name = "PARITY_MSG"
                    Id = 206u
                    IsExtended = false
                    Length = 8us
                    Signals = [ mkSignal "PAYLOAD" 0us 8us; mkSignal "STATUS" 8us 8us ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = None } ] }

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "parity_msg.c")
                let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "parity_msg.h")
                let sourceContent = File.ReadAllText(msgC)
                let headerContent = File.ReadAllText(msgH)
                sourceContent.Contains("sc_crc8_") |> should equal false
                headerContent.Contains("counter_state_t") |> should equal false
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate emits CRC helper declarations and definitions in utils for validate config`` () =
        let outDir = createTempOutDir ()

        let crcMeta =
            mkCrcSignalMeta CrcAlgorithmId.CRC8_SAE_J1850 mkCrc8SaeJ1850Params 0 0

        let crcSignal =
            { (mkSignal "CHECKSUM" 8us 8us) with
                CrcMeta = Some crcMeta
                IsCrc = true }

        let ir =
            { Messages =
                [ { Name = "TEST_MSG"
                    Id = 207u
                    IsExtended = false
                    Length = 8us
                    Signals = [ mkSignal "PAYLOAD" 0us 8us; crcSignal ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = Some CrcCounterMode.Validate } ] }

        let cfg =
            { defaultConfig with
                CrcCounterCheck = true
                CrcCounter =
                    Some(
                        mkCrcCounterConfig
                            "validate"
                            "TEST_MSG"
                            (Some
                                { Signal = "CHECKSUM"
                                  Algorithm = "CRC8_SAE_J1850"
                                  ByteRange = (0, 0)
                                  DataId = None })
                            None
                    ) }

        try
            match generate ir outDir cfg with
            | Ok files ->
                let utilsH = files.Headers |> List.find (fun f -> Path.GetFileName(f).EndsWith("utils.h"))
                let utilsC = files.Sources |> List.find (fun f -> Path.GetFileName(f).EndsWith("utils.c"))
                let utilsHContent = File.ReadAllText(utilsH)
                let utilsCContent = File.ReadAllText(utilsC)
                utilsHContent |> should haveSubstring "sc_crc8_sae_j1850"
                utilsCContent |> should haveSubstring "sc_crc8_sae_j1850"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``generate without CRC config keeps utils free from CRC helpers`` () =
        let outDir = createTempOutDir ()

        let ir =
            { Messages =
                [ { Name = "PARITY_MSG"
                    Id = 208u
                    IsExtended = false
                    Length = 8us
                    Signals = [ mkSignal "PAYLOAD" 0us 8us; mkSignal "STATUS" 8us 8us ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = None } ] }

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let utilsC = files.Sources |> List.find (fun f -> Path.GetFileName(f).EndsWith("utils.c"))
                let content = File.ReadAllText(utilsC)
                content.Contains("sc_crc8_") |> should equal false
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
                    Receivers = []
                    CrcCounterMode = None } ] }

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
                    Receivers = []
                    CrcCounterMode = None } ] }

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
    let ``motorolaMsbFromLsb crosses byte boundary correctly`` () =
        let ir =
            { Messages =
                [ { Name = "BE_LSB_BOUNDARY_MSG"
                    Id = 553u
                    IsExtended = false
                    Length = 8us
                    Signals =
                      [ { mkSignal "SigBEBoundary" 8us 16us with
                            ByteOrder = ByteOrder.Big
                            Maximum = None
                            Minimum = None } ]
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = None } ] }

        let lsbConfig =
            { defaultConfig with
                MotorolaStartBit = "lsb" }

        let outDir = createTempOutDir ()

        try
            match generate ir outDir lsbConfig with
            | Ok files ->
                let msgC =
                    files.Sources
                    |> List.find (fun f -> Path.GetFileName(f) = "be_lsb_boundary_msg.c")

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
                    Receivers = []
                    CrcCounterMode = None } ] }

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

    [<Fact>]
    let ``Range check skipped for DBC no-range sentinel 0 0`` () =
        let signal =
            { Name = "TestSig"
              StartBit = 0us
              Length = 8us
              Factor = 1.0
              Offset = 0.0
              Minimum = Some 0.0
              Maximum = Some 0.0
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

        let msg =
            { Name = "TEST_MSG"
              Id = 1u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "test_msg.c")
                let result = File.ReadAllText(msgC)
                result |> should not' (haveSubstring "TestSig < 0")
                result |> should not' (haveSubstring "TestSig > 0")
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``Range check skipped for inverted DBC range sentinel min gt max`` () =
        // Signal with [1|0] (inverted DBC sentinel): min=1.0, max=0.0 - no range defined
        let invSig =
            { Name = "InvSig"
              StartBit = 0us
              Length = 8us
              Factor = 1.0
              Offset = 0.0
              Minimum = Some 1.0
              Maximum = Some 0.0
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

        let msg =
            { Name = "INV_MSG"
              Id = 2u
              IsExtended = false
              Length = 8us
              Signals = [ invSig ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "inv_msg.c")
                let result = File.ReadAllText(msgC)
                result |> should not' (haveSubstring "InvSig < 1")
                result |> should not' (haveSubstring "InvSig > 0")
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    // -------------------------------------------------------
    // B-O1: Raw-range sentinel detection tests
    // -------------------------------------------------------

    let private mkSignalWithRange name startBit length factor offset minV maxV isSigned =
        { mkSignal name startBit length with
            Factor = factor
            Offset = offset
            Minimum = Some minV
            Maximum = Some maxV
            IsSigned = isSigned }

    [<Fact>]
    let ``Raw range heuristic skips Chrysler LAT_DIST style sentinel`` () =
        let signal = mkSignalWithRange "LAT_DIST" 0us 11us 0.005 -1000.0 0.0 2047.0 false

        let msg =
            { Name = "CHRYSLER_MSG"
              Id = 300u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "chrysler_msg.c")

                let content = File.ReadAllText(msgC)
                content |> should not' (haveSubstring "LAT_DIST < ")
                content |> should not' (haveSubstring "LAT_DIST > ")
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``Raw range heuristic skips Mercedes offset sentinel`` () =
        let signal = mkSignalWithRange "STEER_DIR" 0us 1us 1.0 2.0 0.0 1.0 false

        let msg =
            { Name = "MERCEDES_MSG"
              Id = 301u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "mercedes_msg.c")

                let content = File.ReadAllText(msgC)
                content |> should not' (haveSubstring "STEER_DIR < ")
                content |> should not' (haveSubstring "STEER_DIR > ")
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``Raw range heuristic keeps normal physical range checks`` () =
        let signal = mkSignalWithRange "NORMAL_C" 0us 8us 0.1 0.0 0.0 25.5 false

        let msg =
            { Name = "NORMAL_C_MSG"
              Id = 310u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "normal_c_msg.c")

                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "return false"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``Raw range heuristic keeps identity full-scale checks`` () =
        let signal = mkSignalWithRange "IDENTITY_D" 0us 8us 1.0 0.0 0.0 255.0 false

        let msg =
            { Name = "IDENTITY_D_MSG"
              Id = 311u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "identity_d_msg.c")

                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "return false"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``Raw range heuristic keeps narrowed physical range checks`` () =
        let signal = mkSignalWithRange "NARROW_E" 0us 8us 1.0 0.0 10.0 200.0 false

        let msg =
            { Name = "NARROW_E_MSG"
              Id = 312u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "narrow_e_msg.c")

                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "return false"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``Raw range heuristic keeps Ford full-scale checks`` () =
        let signal = mkSignalWithRange "FORD_F" 0us 12us 0.0625 0.0 0.0 255.9375 false

        let msg =
            { Name = "FORD_F_MSG"
              Id = 313u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "ford_f_msg.c")

                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "return false"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``Raw range heuristic skips signed sentinel ranges`` () =
        let signal = mkSignalWithRange "SIGNED_G" 0us 12us 0.1 0.0 0.0 409.5 true

        let msg =
            { Name = "SIGNED_G_MSG"
              Id = 320u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "signed_g_msg.c")

                let content = File.ReadAllText(msgC)
                content |> should not' (haveSubstring "SIGNED_G < ")
                content |> should not' (haveSubstring "SIGNED_G > ")
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``Raw range heuristic keeps valid signed physical range checks`` () =
        let signal = mkSignalWithRange "SIGNED_H" 0us 8us 1.0 0.0 -128.0 127.0 true

        let msg =
            { Name = "SIGNED_H_MSG"
              Id = 321u
              IsExtended = false
              Length = 8us
              Signals = [ signal ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC =
                    files.Sources |> List.find (fun f -> Path.GetFileName(f) = "signed_h_msg.c")

                let content = File.ReadAllText(msgC)
                content |> should haveSubstring "return false"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``Raw range heuristic selectively skips per signal in same message`` () =
        let sigA = mkSignalWithRange "RAW_SIG" 0us 11us 0.005 -1000.0 0.0 2047.0 false
        let sigB = mkSignalWithRange "NORMAL_SIG" 16us 8us 0.1 0.0 0.0 25.5 false

        let msg =
            { Name = "MIXED_MSG"
              Id = 400u
              IsExtended = false
              Length = 8us
              Signals = [ sigA; sigB ]
              Sender = "ECU"
              Receivers = []
              CrcCounterMode = None }

        let ir = { Messages = [ msg ] }
        let config = { defaultConfig with RangeCheck = true }
        let outDir = createTempOutDir ()

        try
            match generate ir outDir config with
            | Ok files ->
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "mixed_msg.c")
                let content = File.ReadAllText(msgC)
                content |> should not' (haveSubstring "RAW_SIG < ")
                content |> should not' (haveSubstring "RAW_SIG > ")
                content |> should haveSubstring "NORMAL_SIG"
                content |> should haveSubstring "return false"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    let private mkMuxSwitch name startBit length =
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
          MultiplexerIndicator = Some "M"
          MultiplexerSwitchValue = None
          ValueTable = None
          Receivers = []
          CrcMeta = None
          CounterMeta = None }

    let private mkBranchSignal name startBit length muxVal =
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
          MultiplexerIndicator = Some "m"
          MultiplexerSwitchValue = Some muxVal
          ValueTable = None
          Receivers = []
          CrcMeta = None
          CounterMeta = None }

    let private mkMuxMessage name msgId switchSig branchSignals baseSignals =
        { Messages =
            [ { Name = name
                Id = msgId
                IsExtended = false
                Length = 8us
                Signals = [ switchSig ] @ branchSignals @ baseSignals
                Sender = "ECU"
                Receivers = []
                CrcCounterMode = None } ] }

    [<Fact>]
    let ``valid bitmask uses uint32_t for 8-signal mux message`` () =
        let switchSig = mkMuxSwitch "MuxSel" 0us 4us

        let branchSignals =
            [ 0..6 ]
            |> List.map (fun i -> mkBranchSignal (sprintf "Branch_%d" i) (uint16 (8 + (i * 8))) 8us i)

        let ir = mkMuxMessage "MUX8_MSG" 900u switchSig branchSignals []
        let outDir = createTempOutDir ()

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "mux8_msg.h")
                let msgC = files.Sources |> List.find (fun f -> Path.GetFileName(f) = "mux8_msg.c")
                let headerContent = File.ReadAllText(msgH)
                let sourceContent = File.ReadAllText(msgC)
                headerContent |> should haveSubstring "uint32_t valid;"
                headerContent |> should haveSubstring "(1u <<"
                sourceContent |> should haveSubstring "= 0u;"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``valid bitmask uses uint64_t for 33-signal mux message`` () =
        let switchSig = mkMuxSwitch "MuxSel" 0us 4us

        let branchSignals =
            [ 0..31 ]
            |> List.map (fun i -> mkBranchSignal (sprintf "Branch_%d" i) (uint16 ((i + 1) % 64)) 1us i)

        let ir = mkMuxMessage "MUX33_MSG" 901u switchSig branchSignals []
        let outDir = createTempOutDir ()

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "mux33_msg.h")
                let content = File.ReadAllText(msgH)
                content |> should haveSubstring "uint64_t valid;"
                content |> should haveSubstring "(1ULL <<"
                content |> should haveSubstring "= 0ULL;"
                content |> should haveSubstring "/* valid field widened"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``valid bitmask uses uint64_t for 64-signal mux message`` () =
        let switchSig = mkMuxSwitch "MuxSel" 0us 4us

        let branchSignals =
            [ 0..62 ]
            |> List.map (fun i -> mkBranchSignal (sprintf "Branch_%d" i) (uint16 ((i + 1) % 64)) 1us i)

        let ir = mkMuxMessage "MUX64_MSG" 902u switchSig branchSignals []
        let outDir = createTempOutDir ()

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let msgH = files.Headers |> List.find (fun f -> Path.GetFileName(f) = "mux64_msg.h")
                let content = File.ReadAllText(msgH)
                content |> should haveSubstring "uint64_t valid;"
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir

    [<Fact>]
    let ``codegen fails with UnsupportedFeature for 65-signal mux message valid bitmask`` () =
        let switchSig = mkMuxSwitch "MuxSel" 0us 4us

        let branchSignals =
            [ 0..63 ]
            |> List.map (fun i -> mkBranchSignal (sprintf "Branch_%d" i) (uint16 ((i + 1) % 64)) 1us i)

        let ir = mkMuxMessage "MUX65_MSG" 903u switchSig branchSignals []
        let result = generate ir "C:/tmp/nonexistent" defaultConfig

        match result with
        | Error(UnsupportedFeature msg) -> msg |> should haveSubstring "65"
        | _ -> failwith "Expected UnsupportedFeature error"

    [<Fact>]
    let ``non-mux message with many signals has no valid field valid bitmask`` () =
        let signals =
            [ 0..39 ]
            |> List.map (fun i -> mkSignal (sprintf "Plain_%d" i) (uint16 (i % 64)) 1us)

        let ir =
            { Messages =
                [ { Name = "PLAIN40_MSG"
                    Id = 904u
                    IsExtended = false
                    Length = 8us
                    Signals = signals
                    Sender = "ECU"
                    Receivers = []
                    CrcCounterMode = None } ] }

        let outDir = createTempOutDir ()

        try
            match generate ir outDir defaultConfig with
            | Ok files ->
                let msgH =
                    files.Headers |> List.find (fun f -> Path.GetFileName(f) = "plain40_msg.h")

                let content = File.ReadAllText(msgH)
                content |> should not' (haveSubstring "valid")
            | Error e -> failwithf "Expected Ok, got: %A" e
        finally
            cleanupDir outDir
