namespace Signal.CANdy.Core.Tests

open System
open System.Buffers.Binary
open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.Scimg
open Signal.CANdy.Core.Wire

module ScimgTests =

    let private pool: PoolContract =
        { Name = "RuntimePool"
          Signals =
            [ { Name = "Counter"
                SemanticId = 1u
                Storage = U16
                Unit = ""
                Direction = Rx
                Min = None
                Max = None
                Default = None }
              { Name = "Temperature"
                SemanticId = 2u
                Storage = F64
                Unit = "C"
                Direction = Rx
                Min = None
                Max = None
                Default = None }
              { Name = "SignedValue"
                SemanticId = 3u
                Storage = I16
                Unit = ""
                Direction = Rx
                Min = None
                Max = None
                Default = None } ] }

    let private wireSignal name startBit length byteOrder isSigned unitName : WireSignal =
        { Name = name
          StartBit = startBit
          LengthBits = length
          ByteOrder = byteOrder
          IsSigned = isSigned
          Factor = 1.0
          Offset = 0.0
          Unit = unitName
          Min = None
          Max = None
          Mux = Unconditional
          Receivers = [] }

    let private wire: WireIr =
        { Messages =
            [ { Name = "StandardFrame"
                CanId = 0x100u
                IsExtended = false
                LengthBytes = 8us
                Signals =
                  [ wireSignal "RawCounter" 0us 16us Little false ""
                    wireSignal "RawTemperature" 16us 16us Little false "C" ] }
              { Name = "ExtendedFrame"
                CanId = 0x12345u
                IsExtended = true
                LengthBytes = 8us
                Signals = [ wireSignal "RawSigned" 24us 16us Big true "" ] } ] }

    let private bindings: BindingSet =
        { Bindings =
            [ { PoolSignalName = "Counter"
                MessageName = "StandardFrame"
                WireSignalName = "RawCounter"
                Conversion = Identity }
              { PoolSignalName = "Temperature"
                MessageName = "StandardFrame"
                WireSignalName = "RawTemperature"
                Conversion = Affine(0.5, -2.0) }
              { PoolSignalName = "SignedValue"
                MessageName = "ExtendedFrame"
                WireSignalName = "RawSigned"
                Conversion = Identity } ]
          TxMessages = [] }

    let private linkedFixture () =
        match link pool wire bindings with
        | Ok schema -> schema
        | Error errors -> failwithf "Fixture linking failed: %A" errors

    let private imageFixture () =
        match lower (linkedFixture ()) with
        | Ok image -> image
        | Error errors -> failwithf "Fixture lowering failed: %A" errors

    let private bytesFixture () =
        match write (imageFixture ()) with
        | Ok bytes -> bytes
        | Error errors -> failwithf "Fixture writing failed: %A" errors

    let private hasError expected result =
        match result with
        | Error errors -> errors |> List.contains expected
        | Ok _ -> false

    [<Fact>]
    let ``roundtrip: linked fixture -> bytes -> read -> equal image`` () =
        let image = imageFixture ()

        match write image with
        | Error errors -> failwithf "Unexpected write errors: %A" errors
        | Ok bytes ->
            match Scimg.read bytes with
            | Error errors -> failwithf "Unexpected read errors: %A" errors
            | Ok actual -> actual |> should equal image

    [<Fact>]
    let ``two writes are byte identical`` () =
        let image = imageFixture ()

        match write image, write image with
        | Ok first, Ok second -> first |> should equal second
        | first, second -> failwithf "Unexpected write results: %A / %A" first second

    [<Fact>]
    let ``inspector JSON contains counts and crc`` () =
        match inspect (bytesFixture ()) with
        | Error errors -> failwithf "Unexpected inspector errors: %A" errors
        | Ok json ->
            json |> should haveSubstring "\"messageCount\": 2"
            json |> should haveSubstring "\"signalCount\": 3"

            json |> should haveSubstring "\"conversionCount\": 2"

            json |> should haveSubstring "\"crc32Hex\": \"0x"
            json |> should haveSubstring "\"crcValid\": true"
            json.EndsWith("\n") |> should equal true

    [<Fact>]
    let ``reader rejects bad magic`` () =
        let bytes = bytesFixture ()
        bytes.[0] <- 0uy

        Scimg.read bytes |> hasError ImageBadMagic |> should equal true

    [<Fact>]
    let ``reader rejects truncated mid-section`` () =
        let bytes = bytesFixture ()
        let truncated = bytes.[0 .. bytes.Length - 11]

        Scimg.read truncated |> hasError ImageSize |> should equal true

    [<Fact>]
    let ``reader rejects offset past end`` () =
        let bytes = bytesFixture ()
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32, 4), uint32 bytes.Length + 4u)

        Scimg.read bytes |> hasError ImageBounds |> should equal true

    [<Fact>]
    let ``reader rejects crc mismatch`` () =
        let bytes = bytesFixture ()
        bytes.[64] <- bytes.[64] ^^^ 1uy

        Scimg.read bytes |> hasError ImageCrc |> should equal true

    [<Fact>]
    let ``lowering rejects integer storage with affine conversion`` () =
        let schema = linkedFixture ()
        let message = schema.Messages.Head
        let plan = message.Plans.Head

        let invalid: LinkedSchema =
            { schema with
                Messages =
                    [ { message with
                          Plans =
                              [ { plan with
                                    Factor = 2.0
                                    Storage = U16 } ] } ] }

        lower invalid |> hasError ImageTable |> should equal true

    [<Fact>]
    let ``lowering rejects non-dense slots`` () =
        let schema = linkedFixture ()
        let message = schema.Messages.Head
        let plan = message.Plans.Head

        let invalid: LinkedSchema =
            { schema with
                Messages =
                    [ { message with
                          Plans = [ { plan with PoolSlotIndex = 1us } ] } ] }

        lower invalid |> hasError ImageTable |> should equal true

    [<Fact>]
    let ``linker rejects branch without bound selector`` () =
        let branchPool =
            { Name = "MuxPool"
              Signals =
                [ { Name = "BranchValue"
                    SemanticId = 10u
                    Storage = U8
                    Unit = ""
                    Direction = Rx
                    Min = None
                    Max = None
                    Default = None } ] }

        let branchSignal =
            { wireSignal "Muxed" 8us 8us Little false "" with
                Mux = Branch 1 }

        let branchWire =
            { Messages =
                [ { Name = "MuxFrame"
                    CanId = 0x200u
                    IsExtended = false
                    LengthBytes = 8us
                    Signals = [ branchSignal ] } ] }

        let branchBindings =
            { Bindings =
                [ { PoolSignalName = "BranchValue"
                    MessageName = "MuxFrame"
                    WireSignalName = "Muxed"
                    Conversion = Identity } ]
              TxMessages = [] }

        match link branchPool branchWire branchBindings with
        | Ok _ -> failwith "Expected missing selector error."
        | Error errors ->
            errors
            |> List.exists (function
                | InvalidValue details -> details.Contains("MuxFrame") && details.Contains("selector")
                | _ -> false)
            |> should equal true
