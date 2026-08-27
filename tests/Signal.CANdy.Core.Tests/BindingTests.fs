namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Errors

module BindingTests =

    let private containsInvalidJson (fragment: string) result =
        match result with
        | Error errors ->
            errors
            |> List.exists (function
                | InvalidJson details -> details.Contains(fragment)
                | _ -> false)
        | Ok _ -> false

    [<Fact>]
    let ``Binding parser accepts identity and affine conversions`` () =
        let json =
            """
{
  "version": "1",
  "bindings": [
    { "poolSignal": "A", "message": "M", "wireSignal": "WA", "conversion": { "kind": "identity" } },
    { "poolSignal": "B", "message": "M", "wireSignal": "WB", "conversion": { "kind": "affine", "factor": 0.5, "offset": -40.0 } }
  ]
}
"""

        match parseBindingSet json with
        | Error errors -> failwithf "Expected binding parse to succeed, got %A" errors
        | Ok bindingSet ->
            bindingSet.Bindings.Length |> should equal 2
            bindingSet.Bindings.[0].Conversion |> should equal Identity
            bindingSet.Bindings.[1].Conversion |> should equal (Affine(0.5, -40.0))

    [<Fact>]
    let ``Binding parser rejects unknown keys at every object level`` () =
        let json =
            """
{
  "version": "1",
  "bindings": [
    { "poolSignal": "A", "message": "M", "wireSignal": "WA", "conversion": { "kind": "identity", "extra": 1 } }
  ]
}
"""

        parseBindingSet json |> containsInvalidJson "extra" |> should equal true

    [<Theory>]
    [<InlineData("{ \"kind\": \"affine\", \"factor\": 0, \"offset\": 1 }")>]
    [<InlineData("{ \"kind\": \"affine\", \"factor\": 1 }")>]
    [<InlineData("{ \"kind\": \"other\" }")>]
    let ``Binding parser rejects invalid conversion contracts`` conversion =
        let json =
            sprintf
                """{ "version": "1", "bindings": [ { "poolSignal": "A", "message": "M", "wireSignal": "WA", "conversion": %s } ] }"""
                conversion

        parseBindingSet json |> containsInvalidJson "conversion" |> should equal true
