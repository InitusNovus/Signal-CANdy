namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open System
open System.IO
open Signal.CANdy

module FacadeTests =

    /// Helper: write content to a temp file and return its path
    let private createTempFile (content: string) (extension: string) =
        let tempPath = Path.ChangeExtension(Path.GetTempFileName(), extension)
        File.WriteAllText(tempPath, content)
        tempPath

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
