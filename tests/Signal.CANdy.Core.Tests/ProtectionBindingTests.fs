namespace Signal.CANdy.Core.Tests

open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.Wire

module ProtectionBindingTests =

    let private parse json =
        match parseBindingSet json with
        | Ok value -> value
        | Error errors -> failwithf "Expected binding JSON to parse, got %A" errors

    let private containsError (fragment: string) result =
        match result with
        | Error errors -> errors |> List.exists (fun error -> (sprintf "%A" error).Contains(fragment))
        | Ok _ -> false

    let private signal name start length byteOrder isSigned factor offset mux : WireSignal =
        { Name = name
          StartBit = start
          LengthBits = length
          ByteOrder = byteOrder
          IsSigned = isSigned
          Factor = factor
          Offset = offset
          Unit = ""
          Min = None
          Max = None
          IsMuxSelector = (mux = Selector)
          MuxPath =
            match mux with
            | Branch expected ->
                [ { SelectorSignalName = "Selector"
                    Expected = uint32 expected } ]
            | _ -> []
          Receivers = [] }

    let private message name canId signals : WireMessage =
        { Name = name
          CanId = canId
          IsExtended = false
          LengthBytes = 8us
          Signals = signals }

    let private pool signals : PoolContract =
        { Name = "ProtectionPool"
          Signals = signals }

    let private poolSignal name direction : PoolSignal =
        { Name = name
          SemanticId = if name = "RxValue" then 1u else 2u
          Storage = U16
          Unit = ""
          Direction = direction
          Min = None
          Max = None
          Default = None
          FreshnessMs = None }

    let private validJson =
        """
{
  "version": "1",
  "bindings": [
    { "poolSignal": "RxValue", "message": "RxFrame", "wireSignal": "Value", "conversion": { "kind": "identity" } },
    { "poolSignal": "TxValue", "message": "TxFrame", "wireSignal": "Value", "conversion": { "kind": "identity" } }
  ],
  "rxMessages": [
    {
      "message": "RxFrame",
      "crc": {
        "wireSignal": "Crc16",
        "algorithm": "crc16-ccitt-false",
        "byteRange": { "start": 0, "end": 7 },
        "dataId": 4660
      },
      "counter": { "wireSignal": "Alive", "modulus": 16, "increment": 1 }
    }
  ],
  "txMessages": [
    {
      "message": "TxFrame",
      "logicalMessageId": 33,
      "crc": {
        "wireSignal": "Crc8",
        "algorithm": "crc8-sae-j1850",
        "byteRange": { "start": 0, "end": 7 }
      },
      "counter": { "wireSignal": "Alive", "modulus": 16, "increment": 1, "initialValue": 0 }
    }
  ]
}
"""

    let private validPool () =
        pool [ poolSignal "RxValue" Rx; poolSignal "TxValue" Tx ]

    let private validWire () =
        { Messages =
            [ message
                  "RxFrame"
                  0x326u
                  [ signal "Alive" 0us 4us Little false 1.0 0.0 Unconditional
                    signal "Value" 8us 16us Little false 1.0 0.0 Unconditional
                    signal "Crc16" 48us 16us Little false 1.0 0.0 Unconditional ]
              message
                  "TxFrame"
                  0x325u
                  [ signal "Alive" 0us 4us Little false 1.0 0.0 Unconditional
                    signal "Value" 8us 16us Little false 1.0 0.0 Unconditional
                    signal "Crc8" 56us 8us Little false 1.0 0.0 Unconditional ] ] }

    [<Fact>]
    let ``Protection parser accepts exact RX and TX profile tokens`` () =
        let bindingSet = parse validJson
        bindingSet.RxMessages.Length |> should equal 1
        bindingSet.TxMessages.Length |> should equal 1

        let rx = bindingSet.RxMessages.Head
        rx.MessageName |> should equal "RxFrame"
        rx.Counter.Value.Modulus |> should equal 16u

        let rxCrc = rx.Crc.Value
        rxCrc.Algorithm |> should equal Crc16CcittFalse
        rxCrc.ByteStart |> should equal 0uy
        rxCrc.ByteEndInclusive |> should equal 7uy
        rxCrc.DataId |> should equal (Some 0x1234us)

        let txCrc = bindingSet.TxMessages.Head.Crc.Value
        txCrc.Algorithm |> should equal Crc8SaeJ1850
        txCrc.DataId |> should equal None

    [<Fact>]
    let ``Protection legacy JSON defaults RX profiles and TX CRC to empty`` () =
        let bindingSet =
            parse """{ "version": "1", "bindings": [], "txMessages": [ { "message": "Tx", "logicalMessageId": 1 } ] }"""

        bindingSet.RxMessages |> should be Empty

        bindingSet.TxMessages.Head.Crc |> should equal None

    [<Theory>]
    [<InlineData("CRC8-SAE-J1850")>]
    [<InlineData("crc8_sae_j1850")>]
    [<InlineData("crc16-ccitt")>]
    [<InlineData("crc32")>]
    let ``Protection parser rejects non-exact algorithm tokens`` algorithm =
        let json =
            sprintf
                """{ "version": "1", "bindings": [], "rxMessages": [ { "message": "Rx", "crc": { "wireSignal": "Crc", "algorithm": "%s", "byteRange": { "start": 0, "end": 7 } } } ], "txMessages": [] }"""
                algorithm

        parseBindingSet json |> containsError "algorithm" |> should equal true

    [<Theory>]
    [<InlineData("{ \"message\": \"Rx\", \"crc\": { \"wireSignal\": \"Crc\", \"algorithm\": \"crc8-sae-j1850\", \"byteRange\": { \"start\": 0, \"end\": 7 } }, \"extra\": 1 }")>]
    [<InlineData("{ \"message\": \"Rx\", \"crc\": { \"wireSignal\": \"Crc\", \"algorithm\": \"crc8-sae-j1850\", \"byteRange\": { \"start\": 0, \"end\": 7 }, \"extra\": 1 } }")>]
    [<InlineData("{ \"message\": \"Rx\", \"crc\": { \"wireSignal\": \"Crc\", \"algorithm\": \"crc8-sae-j1850\", \"byteRange\": { \"start\": 0, \"end\": 7, \"extra\": 1 } } }")>]
    [<InlineData("{ \"message\": \"Rx\", \"counter\": { \"wireSignal\": \"Alive\", \"modulus\": 16, \"increment\": 1, \"extra\": 1 } }")>]
    let ``Protection parser rejects unknown keys at profile levels`` profile =
        let json =
            sprintf """{ "version": "1", "bindings": [], "rxMessages": [ %s ], "txMessages": [] }""" profile

        parseBindingSet json |> containsError "extra" |> should equal true

    [<Fact>]
    let ``Protection parser rejects unknown TX CRC keys`` () =
        let json =
            """
{
  "version": "1",
  "bindings": [],
  "rxMessages": [],
  "txMessages": [
    {
      "message": "Tx",
      "logicalMessageId": 1,
      "crc": {
        "wireSignal": "Crc",
        "algorithm": "crc8-sae-j1850",
        "byteRange": { "start": 0, "end": 7 },
        "extra": 1
      }
    }
  ]
}
"""

        parseBindingSet json |> containsError "extra" |> should equal true

    [<Fact>]
    let ``Protection parser rejects duplicate RX declarations`` () =
        let json =
            """
{
  "version": "1",
  "bindings": [],
  "rxMessages": [
    { "message": "Rx", "counter": { "wireSignal": "Alive", "modulus": 16, "increment": 1 } },
    { "message": "Rx", "counter": { "wireSignal": "Alive", "modulus": 16, "increment": 1 } }
  ],
  "txMessages": []
}
"""

        parseBindingSet json |> containsError "Rx" |> should equal true

    [<Fact>]
    let ``Protection parser rejects duplicate TX profile declarations`` () =
        let profile =
            """{ "message": "Tx", "logicalMessageId": 1, "crc": { "wireSignal": "Crc", "algorithm": "crc8-sae-j1850", "byteRange": { "start": 0, "end": 7 } } }"""

        let json =
            sprintf """{ "version": "1", "bindings": [], "rxMessages": [], "txMessages": [ %s, %s ] }""" profile profile

        parseBindingSet json |> containsError "Tx" |> should equal true

    [<Fact>]
    let ``Protection parser rejects RX declaration without CRC or counter`` () =
        let json =
            """{ "version": "1", "bindings": [], "rxMessages": [ { "message": "Rx" } ], "txMessages": [] }"""

        parseBindingSet json |> Result.isError |> should equal true

    [<Fact>]
    let ``Protection linker precomputes CRC spans and profile fields`` () =
        match link (validPool ()) (validWire ()) (parse validJson) with
        | Error errors -> failwithf "Expected protection profiles to link, got %A" errors
        | Ok schema ->
            let rx = schema.Messages.Head.Protection.Value
            rx.Counter.Value.StartBit |> should equal 0us
            rx.Counter.Value.Length |> should equal 4us
            rx.Counter.Value.Modulus |> should equal 16u

            let rxCrc = rx.Crc.Value

            rxCrc.Algorithm |> should equal LinkedCrcAlgorithm.Crc16CcittFalse

            rxCrc.StartBit |> should equal 48us
            rxCrc.LengthBits |> should equal 16us
            rxCrc.DataId |> should equal (Some 0x1234us)

            rxCrc.CoverageSpans |> should equal [ { ByteOffset = 0uy; ByteCount = 6uy } ]

            let tx = schema.TxMessages.Head
            tx.Counter.Value.StartBit |> should equal 0us

            tx.Crc.Value.Algorithm |> should equal LinkedCrcAlgorithm.Crc8SaeJ1850

            tx.Crc.Value.CoverageSpans
            |> should equal [ { ByteOffset = 0uy; ByteCount = 7uy } ]

    [<Fact>]
    let ``Protection linker rejects missing profile fields`` () =
        let wire = validWire ()

        let withoutFields =
            { wire with
                Messages =
                    wire.Messages
                    |> List.map (fun item ->
                        { item with
                            Signals = item.Signals |> List.filter (fun item -> item.Name = "Value") }) }

        link (validPool ()) withoutFields (parse validJson)
        |> containsError "Crc16"
        |> should equal true

    [<Theory>]
    [<InlineData(true, 1.0, 0.0, 0)>]
    [<InlineData(false, 2.0, 0.0, 0)>]
    [<InlineData(false, 1.0, 1.0, 0)>]
    [<InlineData(false, 1.0, 0.0, 1)>]
    let ``Protection linker rejects signed scaled or muxed CRC fields`` isSigned factor offset muxValue =
        let wire = validWire ()

        let invalidCrc =
            signal
                "Crc16"
                48us
                16us
                Little
                isSigned
                factor
                offset
                (if muxValue = 0 then Unconditional else Branch muxValue)

        let invalidWire =
            { wire with
                Messages =
                    wire.Messages
                    |> List.map (fun item ->
                        if item.Name = "RxFrame" then
                            { item with
                                Signals =
                                    (item.Signals |> List.filter (fun item -> item.Name <> "Crc16"))
                                    @ [ invalidCrc ] }
                        else
                            item) }

        link (validPool ()) invalidWire (parse validJson)
        |> Result.isError
        |> should equal true

    [<Theory>]
    [<InlineData(true, 1.0, 0.0, 0)>]
    [<InlineData(false, 2.0, 0.0, 0)>]
    [<InlineData(false, 1.0, 1.0, 0)>]
    [<InlineData(false, 1.0, 0.0, 1)>]
    let ``Protection linker rejects signed scaled or muxed counter fields`` isSigned factor offset muxValue =
        let wire = validWire ()

        let invalidCounter =
            signal
                "Alive"
                0us
                4us
                Little
                isSigned
                factor
                offset
                (if muxValue = 0 then Unconditional else Branch muxValue)

        let invalidWire =
            { wire with
                Messages =
                    wire.Messages
                    |> List.map (fun item ->
                        { item with
                            Signals =
                                (item.Signals |> List.filter (fun signal -> signal.Name <> "Alive"))
                                @ [ invalidCounter ] }) }

        link (validPool ()) invalidWire (parse validJson)
        |> Result.isError
        |> should equal true

    [<Fact>]
    let ``Protection linker rejects CRC width alignment and range violations`` () =
        let cases =
            [ signal "Crc16" 48us 8us Little false 1.0 0.0 Unconditional, validJson
              signal "Crc16" 49us 16us Little false 1.0 0.0 Unconditional, validJson
              signal "Crc16" 48us 16us Little false 1.0 0.0 Unconditional, validJson.Replace("\"end\": 7", "\"end\": 8") ]

        for invalidCrc, json in cases do
            let wire = validWire ()

            let invalidWire =
                { wire with
                    Messages =
                        wire.Messages
                        |> List.map (fun item ->
                            if item.Name = "RxFrame" then
                                { item with
                                    Signals =
                                        (item.Signals |> List.filter (fun signal -> signal.Name <> "Crc16"))
                                        @ [ invalidCrc ] }
                            else
                                item) }

            link (validPool ()) invalidWire (parse json)
            |> Result.isError
            |> should equal true

    [<Fact>]
    let ``Protection linker rejects profile overlap with ordinary plans`` () =
        let wire = validWire ()

        let overlapping =
            { wire with
                Messages =
                    wire.Messages
                    |> List.map (fun item ->
                        if item.Name = "RxFrame" then
                            { item with
                                Signals =
                                    item.Signals
                                    |> List.map (fun signal ->
                                        if signal.Name = "Crc16" then
                                            { signal with StartBit = 8us }
                                        else
                                            signal) }
                        else
                            item) }

        link (validPool ()) overlapping (parse validJson)
        |> Result.isError
        |> should equal true

    [<Fact>]
    let ``Protection linker rejects counter overlap with ordinary plans`` () =
        let wire = validWire ()

        let overlapping =
            { wire with
                Messages =
                    wire.Messages
                    |> List.map (fun item ->
                        { item with
                            Signals =
                                item.Signals
                                |> List.map (fun signal ->
                                    if signal.Name = "Alive" then
                                        { signal with StartBit = 8us }
                                    else
                                        signal) }) }

        link (validPool ()) overlapping (parse validJson)
        |> Result.isError
        |> should equal true

    [<Fact>]
    let ``Protection linker rejects counter modulus wider than its field`` () =
        let json = validJson.Replace("\"modulus\": 16", "\"modulus\": 32")

        link (validPool ()) (validWire ()) (parse json)
        |> Result.isError
        |> should equal true

    [<Fact>]
    let ``Protection linker rejects counter outside CRC coverage`` () =
        let json = validJson.Replace("\"end\": 7", "\"end\": 2")

        link (validPool ()) (validWire ()) (parse json)
        |> Result.isError
        |> should equal true
