namespace Generator.Tests

open Xunit
open FsUnit.Xunit
open Generator.Dbc
open Generator.Result

module ValueTableTests =

    let createTempDbcFile (content: string) =
        let path = System.IO.Path.GetTempFileName()
        System.IO.File.WriteAllText(path, content)
        path

    [<Fact>]
    let ``Value tables are parsed and attached to signals`` () =
        let dbcContent = """
VERSION ""
NS_ :
BS_:

BO_ 200 VT_MSG: 8 Vector__XXX
 SG_ Mode : 0|8@1+ (1,0) [0|255] "" Vector__XXX
 SG_ State : 8|8@1+ (1,0) [0|255] "" Vector__XXX

VAL_ 200 Mode 0 "OFF" 1 "ON" 2 "AUTO" ;
VAL_ 200 State 0 "IDLE" 1 "RUN" 2 "STOP" ;
"""
        let dbcPath = createTempDbcFile dbcContent
        let result = parseDbcFile dbcPath
        match result with
        | Success ir ->
            let msg = ir.Messages |> List.find (fun m -> m.Name = "VT_MSG")
            let mode = msg.Signals |> List.find (fun s -> s.Name = "Mode")
            let state = msg.Signals |> List.find (fun s -> s.Name = "State")
            let vtMode = mode.ValueTable |> Option.defaultValue [] |> set
            let vtState = state.ValueTable |> Option.defaultValue [] |> set
            vtMode |> should equal (set [ (0, "OFF"); (1, "ON"); (2, "AUTO") ])
            vtState |> should equal (set [ (0, "IDLE"); (1, "RUN"); (2, "STOP") ])
        | Failure errors -> failwithf "Expected success, got errors: %A" errors
        System.IO.File.Delete(dbcPath)
