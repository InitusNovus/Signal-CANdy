namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Pool

module PoolTests =

    let private validSignal =
        { Name = "VehicleSpeed"
          SemanticId = 1u
          Storage = F32
          Unit = "km/h"
          Direction = Rx
          Min = Some 0.0
          Max = Some 250.0
          Default = Some 0.0
          FreshnessMs = None }

    let private validContract =
        { Name = "VehiclePool"
          Signals = [ validSignal ] }

    let private containsError predicate result =
        match result with
        | Error errors -> errors |> List.exists predicate
        | Ok _ -> false

    [<Fact>]
    let ``Roundtrip: pool -> manifest JSON -> parse -> equal contract`` () =
        let contract =
            { validContract with
                Signals =
                    [ validSignal
                      { Name = "Odometer"
                        SemanticId = 2u
                        Storage = U32
                        Unit = "km"
                        Direction = Tx
                        Min = None
                        Max = None
                        Default = None
                        FreshnessMs = None } ] }

        match writeManifest contract with
        | Error errors -> failwithf "Expected manifest write to succeed, got: %A" errors
        | Ok manifest ->
            match parsePoolDefinition manifest with
            | Ok parsed -> parsed |> should equal contract
            | Error errors -> failwithf "Expected manifest parse to succeed, got: %A" errors

    [<Fact>]
    let ``Validation rejects duplicate semantic ids`` () =
        let duplicate =
            { validSignal with
                Name = "EngineSpeed"
                SemanticId = validSignal.SemanticId }

        let contract =
            { validContract with
                Signals = [ validSignal; duplicate ] }

        validate contract
        |> containsError (function
            | DuplicateSemanticId(1u, "EngineSpeed") -> true
            | _ -> false)
        |> should equal true

    [<Fact>]
    let ``Validation rejects duplicate signal names`` () =
        let duplicate = { validSignal with SemanticId = 2u }

        let contract =
            { validContract with
                Signals = [ validSignal; duplicate ] }

        validate contract
        |> containsError (function
            | DuplicateName "VehicleSpeed" -> true
            | _ -> false)
        |> should equal true

    [<Fact>]
    let ``Validation rejects inverted range`` () =
        let contract =
            { validContract with
                Signals =
                    [ { validSignal with
                          Min = Some 10.0
                          Max = Some 5.0 } ] }

        validate contract
        |> containsError (function
            | InvalidRange "VehicleSpeed" -> true
            | _ -> false)
        |> should equal true

    [<Fact>]
    let ``Validation rejects default outside range`` () =
        let contract =
            { validContract with
                Signals =
                    [ { validSignal with
                          Default = Some 300.0
                          FreshnessMs = None } ] }

        validate contract
        |> containsError (function
            | DefaultOutOfRange "VehicleSpeed" -> true
            | _ -> false)
        |> should equal true

    [<Fact>]
    let ``Manifest output is deterministic across two writes`` () =
        match writeManifest validContract, writeManifest validContract with
        | Ok first, Ok second -> second |> should equal first
        | first, second -> failwithf "Expected both manifest writes to succeed, got: %A and %A" first second

    [<Fact>]
    let ``Parse rejects unknown JSON keys`` () =
        let json =
            """
{
  "name": "VehiclePool",
  "signals": [],
  "unexpected": true
}
"""

        parsePoolDefinition json
        |> containsError (function
            | InvalidJson details when details.Contains("unexpected") -> true
            | _ -> false)
        |> should equal true

    [<Fact>]
    let ``Validation rejects empty pool name`` () =
        let contract = { validContract with Name = "" }

        validate contract
        |> containsError (function
            | MissingField "Pool name" -> true
            | _ -> false)
        |> should equal true

    [<Fact>]
    let ``Validation rejects empty signal name`` () =
        let contract =
            { validContract with
                Signals = [ { validSignal with Name = "" } ] }

        validate contract
        |> containsError (function
            | MissingField "Signal name" -> true
            | _ -> false)
        |> should equal true
