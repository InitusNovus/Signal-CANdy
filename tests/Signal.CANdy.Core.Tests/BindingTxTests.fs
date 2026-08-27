namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.Wire

module BindingTxTests =

    let private signal name mux : WireSignal =
        { Name = name
          StartBit = 0us
          LengthBits = 8us
          ByteOrder = Little
          IsSigned = false
          Factor = 1.0
          Offset = 0.0
          Unit = ""
          Min = None
          Max = None
          Mux = mux
          Receivers = [] }

    let private message name canId signals : WireMessage =
        { Name = name
          CanId = canId
          IsExtended = false
          LengthBytes = 8us
          Signals = signals }

    let private pool direction : PoolContract =
        { Name = "TxPool"
          Signals =
            [ { Name = "Value"
                SemanticId = 1u
                Storage = U8
                Unit = ""
                Direction = direction
                Min = None
                Max = None
                Default = None } ] }

    let private parse json =
        match parseBindingSet json with
        | Ok value -> value
        | Error errors -> failwithf "Expected binding JSON to parse, got %A" errors

    let private hasInvalidValue (fragment: string) result =
        match result with
        | Error errors ->
            errors
            |> List.exists (function
                | InvalidValue details -> details.Contains(fragment)
                | _ -> false)
        | Ok _ -> false

    [<Fact>]
    let ``Tx existing version one JSON defaults txMessages to empty`` () =
        let bindingSet = parse """{ "version": "1", "bindings": [] }"""
        bindingSet.TxMessages |> should be Empty

    [<Fact>]
    let ``Tx duplicate logical message IDs are rejected`` () =
        let json =
            """
{
  "version": "1",
  "bindings": [],
  "txMessages": [
    { "message": "A", "logicalMessageId": 7 },
    { "message": "B", "logicalMessageId": 7 }
  ]
}
"""

        parseBindingSet json |> Result.isError |> should equal true

    [<Fact>]
    let ``Tx linker rejects a missing wire message`` () =
        let bindingSet =
            parse
                """{ "version": "1", "bindings": [], "txMessages": [ { "message": "Missing", "logicalMessageId": 1 } ] }"""

        link (pool Tx) { Messages = [] } bindingSet
        |> hasInvalidValue "Missing"
        |> should equal true

    [<Fact>]
    let ``Tx linker rejects an RX pool slot used by a TX message`` () =
        let bindingSet =
            parse
                """
{
  "version": "1",
  "bindings": [
    { "poolSignal": "Value", "message": "Command", "wireSignal": "Raw", "conversion": { "kind": "identity" } }
  ],
  "txMessages": [ { "message": "Command", "logicalMessageId": 1 } ]
}
"""

        let wire =
            { Messages = [ message "Command" 0x100u [ signal "Raw" Unconditional ] ] }

        link (pool Rx) wire bindingSet |> Result.isError |> should equal true

    [<Fact>]
    let ``Tx linker rejects a branch without a TX selector`` () =
        let bindingSet =
            parse
                """
{
  "version": "1",
  "bindings": [
    { "poolSignal": "Value", "message": "Command", "wireSignal": "Branch", "conversion": { "kind": "identity" } }
  ],
  "txMessages": [ { "message": "Command", "logicalMessageId": 1 } ]
}
"""

        let wire =
            { Messages = [ message "Command" 0x100u [ signal "Branch" (Branch 1) ] ] }

        link (pool Tx) wire bindingSet |> Result.isError |> should equal true

    [<Theory>]
    [<InlineData(1u, 1u, 0u)>]
    [<InlineData(16u, 0u, 0u)>]
    [<InlineData(16u, 16u, 0u)>]
    [<InlineData(16u, 1u, 16u)>]
    let ``Tx parser rejects an invalid counter profile`` modulus increment initialValue =
        let json =
            sprintf
                """{ "version": "1", "bindings": [], "txMessages": [ { "message": "Command", "logicalMessageId": 1, "counter": { "wireSignal": "Alive", "modulus": %u, "increment": %u, "initialValue": %u } } ] }"""
                modulus
                increment
                initialValue

        parseBindingSet json |> Result.isError |> should equal true

    [<Fact>]
    let ``Tx pool slot may be consumed by two logical messages`` () =
        let bindingSet =
            parse
                """
{
  "version": "1",
  "bindings": [
    { "poolSignal": "Value", "message": "CommandA", "wireSignal": "Raw", "conversion": { "kind": "identity" } },
    { "poolSignal": "Value", "message": "CommandB", "wireSignal": "Raw", "conversion": { "kind": "identity" } }
  ],
  "txMessages": [
    { "message": "CommandA", "logicalMessageId": 10 },
    { "message": "CommandB", "logicalMessageId": 20 }
  ]
}
"""

        let wire =
            { Messages =
                [ message "CommandA" 0x100u [ signal "Raw" Unconditional ]
                  message "CommandB" 0x101u [ signal "Raw" Unconditional ] ] }

        match link (pool Tx) wire bindingSet with
        | Error errors -> failwithf "Expected shared TX source slot to link, got %A" errors
        | Ok schema ->
            schema.PoolSlots.Length |> should equal 1

            schema.TxMessages
            |> List.map (fun message -> message.LogicalMessageId)
            |> should equal [ 10u; 20u ]

            schema.TxMessages
            |> List.collect (fun message -> message.Plans)
            |> List.map (fun plan -> plan.PoolSlotIndex)
            |> should equal [ 0us; 0us ]
