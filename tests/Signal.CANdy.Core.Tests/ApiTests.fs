namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open System.IO
open Signal.CANdy.Core.Errors

module ApiTests =

    /// Helper: write content to a temp file and return its path
    let private createTempFile (content: string) (extension: string) =
        let tempPath = Path.ChangeExtension(Path.GetTempFileName(), extension)
        File.WriteAllText(tempPath, content)
        tempPath

    // -------------------------------------------------------
    // C-2 tests: generateFromPaths preserves error type information
    // -------------------------------------------------------

    [<Fact>]
    let ``generateFromPaths returns Parse error for non-existent DBC`` () =
        let outDir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
        Directory.CreateDirectory(outDir) |> ignore
        try
            let t = Signal.CANdy.Core.Api.generateFromPaths "does_not_exist.dbc" outDir None
            let result = t.GetAwaiter().GetResult()
            match result with
            | Error (GenerateError.Parse (ParseError.IoError _)) -> () // expected
            | Error e -> failwithf "Expected GenerateError.Parse(IoError), got: %A" e
            | Ok _ -> failwith "Expected error, got Ok"
        finally
            if Directory.Exists(outDir) then Directory.Delete(outDir, true)

    [<Fact>]
    let ``generateFromPaths returns Validation error for invalid config`` () =
        let dbcContent = """
VERSION ""
NS_ :
BS_:

BO_ 100 MESSAGE_1: 8 Vector__XXX
 SG_ Signal_1 : 0|8@1+ (1,0) [0|255] "" Vector__XXX
"""
        let dbcPath = createTempFile dbcContent ".dbc"
        let configContent = """
phys_type: INVALID_TYPE
"""
        let configPath = createTempFile configContent ".yaml"
        let outDir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
        Directory.CreateDirectory(outDir) |> ignore
        try
            let t = Signal.CANdy.Core.Api.generateFromPaths dbcPath outDir (Some configPath)
            let result = t.GetAwaiter().GetResult()
            match result with
            | Error (GenerateError.Validation (ValidationError.InvalidValue _)) -> () // expected
            | Error e -> failwithf "Expected GenerateError.Validation(InvalidValue), got: %A" e
            | Ok _ -> failwith "Expected validation error, got Ok"
        finally
            File.Delete(dbcPath)
            File.Delete(configPath)
            if Directory.Exists(outDir) then Directory.Delete(outDir, true)

    [<Fact>]
    let ``generateFromPaths returns Parse error for duplicate IDs in DBC`` () =
        let dbcContent = """
VERSION ""
NS_ :
BS_:

BO_ 100 MESSAGE_1: 8 Vector__XXX
 SG_ Signal_1 : 0|8@1+ (1,0) [0|255] "" Vector__XXX
BO_ 100 MESSAGE_2: 8 Vector__XXX
 SG_ Signal_2 : 8|16@1+ (0.1,0) [0|100] "Unit" Vector__XXX
"""
        let dbcPath = createTempFile dbcContent ".dbc"
        let outDir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
        Directory.CreateDirectory(outDir) |> ignore
        try
            let t = Signal.CANdy.Core.Api.generateFromPaths dbcPath outDir None
            let result = t.GetAwaiter().GetResult()
            match result with
            | Error (GenerateError.Parse (ParseError.InvalidDbc msg)) ->
                msg |> should haveSubstring "Duplicate message ID 100"
            | Error e -> failwithf "Expected GenerateError.Parse(InvalidDbc), got: %A" e
            | Ok _ -> failwith "Expected parse error, got Ok"
        finally
            File.Delete(dbcPath)
            if Directory.Exists(outDir) then Directory.Delete(outDir, true)
