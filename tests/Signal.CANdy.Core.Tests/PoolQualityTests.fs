namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Pool

module PoolQualityTests =

    let private parse json =
        match parsePoolDefinition json with
        | Ok contract -> contract
        | Error errors -> failwithf "Expected pool definition to parse, got %A" errors

    let private invalidJsonContains (fragment: string) result =
        match result with
        | Error errors ->
            errors
            |> List.exists (function
                | InvalidJson details -> details.Contains(fragment)
                | _ -> false)
        | Ok _ -> false

    let private definition direction freshness =
        sprintf
            """
{
  "name": "QualityPool",
  "version": "1",
  "signals": [
    {
      "name": "Value",
      "semanticId": 1,
      "storage": "u16",
      "unit": "",
      "direction": "%s"%s
    }
  ]
}
"""
            direction
            freshness

    [<Fact>]
    let ``Quality freshness parses and manifest roundtrips`` () =
        let contract = parse (definition "rx" ",\n      \"freshnessMs\": 200")
        contract.Signals.Head.FreshnessMs |> should equal (Some 200u)

        match writeManifest contract with
        | Error errors -> failwithf "Expected quality manifest write, got %A" errors
        | Ok json ->
            json |> should haveSubstring "\"freshnessMs\": 200"
            parse json |> should equal contract

    [<Fact>]
    let ``Quality omitted freshness remains disabled and omitted by writer`` () =
        let contract = parse (definition "rx" "")
        contract.Signals.Head.FreshnessMs |> should equal None

        match writeManifest contract with
        | Error errors -> failwithf "Expected legacy manifest write, got %A" errors
        | Ok json -> json.Contains("freshnessMs") |> should equal false

    [<Theory>]
    [<InlineData("0")>]
    [<InlineData("2147483648")>]
    [<InlineData("1.5")>]
    let ``Quality parser rejects freshness outside positive int32 range`` value =
        parsePoolDefinition (definition "rx" (sprintf ",\n      \"freshnessMs\": %s" value))
        |> Result.isError
        |> should equal true

    [<Fact>]
    let ``Quality parser rejects freshness on TX signal`` () =
        parsePoolDefinition (definition "tx" ",\n      \"freshnessMs\": 10")
        |> Result.isError
        |> should equal true

    [<Fact>]
    let ``Quality parser remains strict for near miss freshness key`` () =
        parsePoolDefinition (definition "rx" ",\n      \"freshnessMS\": 10")
        |> invalidJsonContains "freshnessMS"
        |> should equal true
