namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.Wire

module LinkedTests =

    let private pool =
        { Name = "Vehicle"
          Signals =
            [ { Name = "Speed"
                SemanticId = 1u
                Storage = F32
                Unit = "km/h"
                Direction = Rx
                Min = Some 0.0
                Max = Some 250.0
                Default = None }
              { Name = "Temperature"
                SemanticId = 2u
                Storage = F32
                Unit = "C"
                Direction = Rx
                Min = None
                Max = None
                Default = None } ] }

    let private wire: WireIr =
        { Messages =
            [ { Name = "VehicleStatus"
                CanId = 100u
                IsExtended = false
                LengthBytes = 8us
                Signals =
                  [ { Name = "RawSpeed"
                      StartBit = 0us
                      LengthBits = 16us
                      Factor = 0.1
                      Offset = 0.0
                      Min = None
                      Max = None
                      Unit = "km/h"
                      IsSigned = false
                      ByteOrder = Little
                      Mux = Unconditional
                      Receivers = [] } ] } ] }

    [<Fact>]
    let ``Linked linker resolves pool slots and affine conversion`` () =
        let bindings =
            { Bindings =
                [ { PoolSignalName = "Speed"
                    MessageName = "VehicleStatus"
                    WireSignalName = "RawSpeed"
                    Conversion = Affine(0.5, 2.0) } ]
              TxMessages = [] }

        match link pool wire bindings with
        | Error errors -> failwithf "Unexpected errors: %A" errors
        | Ok schema ->
            let plan = schema.Messages.Head.Plans.Head
            plan.PoolSlotIndex |> should equal 0us
            plan.Factor |> should equal 0.5
            plan.Offset |> should equal 2.0

    [<Fact>]
    let ``Linked linker reports every unresolved reference`` () =
        let bindings =
            { Bindings =
                [ { PoolSignalName = "MissingPool"
                    MessageName = "VehicleStatus"
                    WireSignalName = "RawSpeed"
                    Conversion = Identity }
                  { PoolSignalName = "Speed"
                    MessageName = "VehicleStatus"
                    WireSignalName = "MissingWire"
                    Conversion = Identity } ]
              TxMessages = [] }

        match link pool wire bindings with
        | Ok _ -> failwith "Expected unresolved binding diagnostics."
        | Error errors ->
            errors
            |> List.map (function
                | InvalidValue message -> message
                | error -> sprintf "%A" error)
            |> String.concat "\n"
            |> fun messages ->
                messages.Contains("MissingPool") |> should equal true

                messages.Contains("MissingWire") |> should equal true
