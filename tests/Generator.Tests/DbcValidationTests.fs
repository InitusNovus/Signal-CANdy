namespace Generator.Tests

open Xunit
open FsUnit.Xunit
open Generator.Dbc
open Generator.Result
open System.IO

module DbcValidationTests =

    // Helper function to create temporary DBC files for testing
    let createTempDbcFile (content: string) =
        let tempPath = Path.GetTempFileName()
        File.WriteAllText(tempPath, content)
        tempPath

    [<Fact>]
    let ``Validation fails for duplicate message IDs`` () =
        let dbcContent = """
    VERSION ""
    NS_ :
    BS_:

    BO_ 100 MESSAGE_1: 8 Vector__XXX
     SG_ Signal_1 : 0|8@1+ (1,0) [0|255] "" Vector__XXX
    BO_ 100 MESSAGE_2: 8 Vector__XXX
     SG_ Signal_2 : 8|16@1+ (0.1,0) [0|100] "Unit" Vector__XXX
    """
        let dbcPath = createTempDbcFile dbcContent
        let result = parseDbcFile dbcPath
        printfn "Duplicate ID test result: %A" result
        match result with
        | Success _ -> failwith "Expected a failure, but got success."
        | Failure errors ->
            errors |> should equal ["Duplicate message ID 100 found."]
        File.Delete(dbcPath)

    [<Fact>]
    let ``Validation fails for overlapping signals`` () =
        let dbcContent = """
    VERSION ""
    NS_ :
    BS_:

    BO_ 100 MESSAGE_1: 8 Vector__XXX
     SG_ Signal_1 : 0|8@1+ (1,0) [0|255] "" Vector__XXX
     SG_ Signal_2 : 7|16@1+ (0.1,0) [0|100] "Unit" Vector__XXX
    """
        let dbcPath = createTempDbcFile dbcContent
        let result = parseDbcFile dbcPath
        printfn "Overlapping signals test result: %A" result
        match result with
        | Success _ -> failwith "Expected a failure, but got success."
        | Failure errors ->
            errors |> should equal ["Signal 'Signal_2' in message 'MESSAGE_1' overlaps with other signals."]
        File.Delete(dbcPath)

    [<Fact>]
    let ``Validation fails for signal exceeding message DLC`` () =
        let dbcContent = """
    VERSION ""
    NS_ :
    BS_:

    BO_ 100 MESSAGE_1: 2 Vector__XXX
     SG_ Signal_1 : 8|16@1+ (1,0) [0|255] "" Vector__XXX
    """
        let dbcPath = createTempDbcFile dbcContent
        let result = parseDbcFile dbcPath
        printfn "Exceeding DLC test result: %A" result
        match result with
        | Success _ -> failwith "Expected a failure, but got success."
        | Failure errors ->
            errors |> should equal ["Signal 'Signal_1' in message 'MESSAGE_1' exceeds the message DLC of 2 bytes."]
        File.Delete(dbcPath)

    [<Fact>]
    let ``Validation succeeds for a valid DBC file`` () =
        let dbcContent = """
    VERSION ""
    NS_ :
    BS_:

    BO_ 100 MESSAGE_1: 8 Vector__XXX
     SG_ Signal_1 : 0|8@1+ (1,0) [0|255] "" Vector__XXX
     SG_ Signal_2 : 8|16@1+ (0.1,0) [0|100] "Unit" Vector__XXX
    BO_ 200 MESSAGE_2: 1 Vector__XXX
     SG_ Signal_3 : 0|1@1+ (1,0) [0|1] "" Vector__XXX
    """
        let dbcPath = createTempDbcFile dbcContent
        let result = parseDbcFile dbcPath
        printfn "Valid DBC test result: %A" result
        match result with
        | Success _ -> true |> should be True
        | Failure errors -> failwith (sprintf "Expected success, but got %A" errors)
        File.Delete(dbcPath)

    [<Fact>]
    let ``Validation fails for multiple multiplexer switches in one message`` () =
        let dbcContent = """
    VERSION ""
    NS_ :
    BS_:

    BO_ 300 MUX_BAD: 8 Vector__XXX
     SG_ Switch1 M : 0|4@1+ (1,0) [0|15] "" Vector__XXX
     SG_ Switch2 M : 4|4@1+ (1,0) [0|15] "" Vector__XXX
    """
        let dbcPath = createTempDbcFile dbcContent
        let result = parseDbcFile dbcPath
        match result with
        | Success _ -> failwith "Expected a failure, but got success."
        | Failure errors ->
            errors |> should equal ["Multiple multiplexer switch signals found in message 'MUX_BAD'."]
        File.Delete(dbcPath)

    [<Fact>]
    let ``Validation fails for multiplexed signal missing branch value`` () =
        let dbcContent = """
    VERSION ""
    NS_ :
    BS_:

    BO_ 301 MUX_MISSING: 8 Vector__XXX
     SG_ Switch M : 0|4@1+ (1,0) [0|15] "" Vector__XXX
     SG_ Branch m : 4|8@1+ (1,0) [0|255] "" Vector__XXX
    """
        let dbcPath = createTempDbcFile dbcContent
        let result = parseDbcFile dbcPath
        match result with
        | Success _ -> failwith "Expected a failure, but got success."
        | Failure errors ->
            errors |> should equal ["Multiplexed signal 'Branch' in message 'MUX_MISSING' is missing a switch value (m<k>)."]
        File.Delete(dbcPath)
