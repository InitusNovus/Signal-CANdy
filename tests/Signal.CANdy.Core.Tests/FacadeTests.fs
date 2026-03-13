namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open System
open System.IO
open Signal.CANdy
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Config

module FacadeTests =

    /// Helper: write content to a temp file and return its path
    let private createTempFile (content: string) (extension: string) =
        let tempPath = Path.ChangeExtension(Path.GetTempFileName(), extension)
        File.WriteAllText(tempPath, content)
        tempPath

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

    let private mkUnsupportedMuxIr () =
        let switchSig = mkMuxSwitch "MuxSel" 0us 4us

        let branchSignals =
            [ 0..63 ]
            |> List.map (fun i -> mkBranchSignal (sprintf "Branch_%d" i) (uint16 ((i + 1) % 64)) 1us i)

        { Messages =
            [ { Name = "MUX65_MSG"
                Id = 903u
                IsExtended = false
                Length = 8us
                Signals = [ switchSig ] @ branchSignals
                Sender = "ECU"
                Receivers = []
                CrcCounterMode = None } ] }

    let private defaultConfig: Config =
        { PhysType = "float"
          PhysMode = "double"
          RangeCheck = false
          Dispatch = "binary_search"
          CrcCounterCheck = false
          MotorolaStartBit = "msb"
          FilePrefix = "sc_" }

    // -------------------------------------------------------
    // H-3c: Facade unit tests — exception type verification
    // -------------------------------------------------------

    [<Fact>]
    let ``GenerateFromPathsAsync throws SignalCandyParseException for non-existent DBC`` () =
        let facade = GeneratorFacade()
        let outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(outDir) |> ignore

        try
            let ex =
                Assert.ThrowsAsync<SignalCandyParseException>(fun () ->
                    facade.GenerateFromPathsAsync("does_not_exist.dbc", outDir, null) :> System.Threading.Tasks.Task)

            let result = ex.GetAwaiter().GetResult()
            result.Message |> should not' (be EmptyString)
        finally
            if Directory.Exists(outDir) then
                Directory.Delete(outDir, true)

    [<Fact>]
    let ``GenerateFromPathsAsync throws SignalCandyValidationException for invalid config`` () =
        let facade = GeneratorFacade()

        let dbcContent =
            """
VERSION ""
NS_ :
BS_:

BO_ 100 MESSAGE_1: 8 Vector__XXX
 SG_ Signal_1 : 0|8@1+ (1,0) [0|255] "" Vector__XXX
"""

        let dbcPath = createTempFile dbcContent ".dbc"

        let configContent =
            """
phys_type: INVALID_TYPE
"""

        let configPath = createTempFile configContent ".yaml"
        let outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(outDir) |> ignore

        try
            let ex =
                Assert.ThrowsAsync<SignalCandyValidationException>(fun () ->
                    facade.GenerateFromPathsAsync(dbcPath, outDir, configPath) :> System.Threading.Tasks.Task)

            let result = ex.GetAwaiter().GetResult()
            result.Message |> should haveSubstring "InvalidValue"
        finally
            File.Delete(dbcPath)
            File.Delete(configPath)

            if Directory.Exists(outDir) then
                Directory.Delete(outDir, true)

    [<Fact>]
    let ``ParseDbc throws SignalCandyParseException for non-existent file`` () =
        let facade = GeneratorFacade()

        let ex =
            Assert.Throws<SignalCandyParseException>(fun () -> facade.ParseDbc("absolutely_nonexistent.dbc") |> ignore)

        ex.Message |> should not' (be EmptyString)

    [<Fact>]
    let ``ValidateConfig throws SignalCandyValidationException for invalid config`` () =
        let facade = GeneratorFacade()

        let badConfig: Signal.CANdy.Core.Config.Config =
            { PhysType = "INVALID"
              PhysMode = "double"
              RangeCheck = false
              Dispatch = "binary_search"
              CrcCounterCheck = false
              MotorolaStartBit = "msb"
              FilePrefix = "sc_" }

        let ex =
            Assert.Throws<SignalCandyValidationException>(fun () -> facade.ValidateConfig(badConfig))

        ex.Message |> should haveSubstring "phys_type"

    [<Fact>]
    let ``GenerateCode throws SignalCandyCodeGenException for UnsupportedFeature`` () =
        let facade = GeneratorFacade()
        let outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(outDir) |> ignore

        try
            let ex =
                Assert.Throws<SignalCandyCodeGenException>(fun () ->
                    facade.GenerateCode(mkUnsupportedMuxIr (), outDir, defaultConfig) |> ignore)

            ex.Message |> should haveSubstring ">64"
        finally
            if Directory.Exists(outDir) then
                Directory.Delete(outDir, true)

    [<Fact>]
    let ``GenerateFromPathsAsync throws SignalCandyCodeGenException for crc_counter_check unsupported path`` () =
        let facade = GeneratorFacade()

        let dbcContent =
            """
VERSION ""
NS_ :
BS_:

BO_ 400 CRC_MSG: 8 Vector__XXX
 SG_ Payload : 0|8@1+ (1,0) [0|255] "" Vector__XXX
 SG_ MessageCrc : 8|8@1+ (1,0) [0|255] "" Vector__XXX
"""

        let dbcPath = createTempFile dbcContent ".dbc"

        let configContent =
            """
phys_type: float
phys_mode: double
range_check: false
dispatch: binary_search
crc_counter_check: true
motorola_start_bit: msb
file_prefix: sc_
"""

        let configPath = createTempFile configContent ".yaml"
        let outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(outDir) |> ignore

        try
            let ex =
                Assert.ThrowsAsync<SignalCandyCodeGenException>(fun () ->
                    facade.GenerateFromPathsAsync(dbcPath, outDir, configPath) :> System.Threading.Tasks.Task)

            let result = ex.GetAwaiter().GetResult()
            result.Message |> should haveSubstring "crc_counter_check=true"
            result.Message |> should haveSubstring "MessageCrc"
        finally
            File.Delete(dbcPath)
            File.Delete(configPath)

            if Directory.Exists(outDir) then
                Directory.Delete(outDir, true)
