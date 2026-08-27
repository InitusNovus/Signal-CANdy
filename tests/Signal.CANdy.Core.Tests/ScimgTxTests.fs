namespace Signal.CANdy.Core.Tests

open System
open System.Buffers.Binary
open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Scimg

module ScimgTxTests =

    let private identity =
        { IsAffine = false
          Factor = 1.0
          Offset = 0.0 }

    let private legacyImage: RuntimeImage =
        { Messages =
            [ { EncodedCanId = 0x100u
                ProgramCount = 1us
                ProgramIndex = 0us } ]
          Programs =
            [ { StartBit = 0us
                LengthBits = 8us
                OrderFlags = 0uy
                Storage = 0uy
                ConversionIndex = 0us
                SlotIndex = 0us
                MuxSelectorSlot = UInt16.MaxValue
                MuxExpected = UInt32.MaxValue } ]
          Conversions = [ identity ]
          PoolSlotCount = 1us
          SignalNames = [ "value" ]
          MessageNames = [ "frame" ]
          TxMessages = []
          TxPrograms = []
          TxCounters = []
          TxTemplates = [||] }

    let private frozenLegacyBytes () =
        Convert.FromHexString(
            "5343494D473031000100000088000000010001000100000000000000000000004000000008000000480000001000000058000000180000007000000014000000000100000100000000000800000000000000FFFFFFFFFFFF0000000000000000000000000000F03F000000000000000001000100050076616C756505006672616D650000DA2DED64"
        )

    let private txImage: RuntimeImage =
        { Messages = []
          Programs = []
          Conversions = [ identity ]
          PoolSlotCount = 1us
          SignalNames = [ "command" ]
          MessageNames = []
          TxMessages =
            [ ({ LogicalMessageId = 7u
                 EncodedCanId = 0x321u
                 PayloadLength = 8uy
                 FrameFlags = 0uy
                 ProgramCount = 1us
                 ProgramIndex = 0us
                 CounterIndex = 0us
                 TemplateOffset = 96u }
              : ImageTxMessage) ]
          TxPrograms =
            [ { StartBit = 0us
                LengthBits = 8us
                OrderFlags = 0uy
                Storage = 0uy
                ConversionIndex = 0us
                SlotIndex = 0us
                MuxSelectorSlot = UInt16.MaxValue
                MuxExpected = UInt32.MaxValue } ]
          TxCounters =
            [ ({ StartBit = 8us
                 LengthBits = 4us
                 BigEndian = false
                 Modulus = 16u
                 Increment = 1u
                 InitialValue = 0u }
              : ImageTxCounter) ]
          TxTemplates = Array.zeroCreate 8 }

    let private crc32 (bytes: byte array) count =
        let mutable crc = UInt32.MaxValue

        for index in 0 .. count - 1 do
            crc <- crc ^^^ uint32 bytes.[index]

            for _ in 0..7 do
                if (crc &&& 1u) <> 0u then
                    crc <- (crc >>> 1) ^^^ 0xEDB88320u
                else
                    crc <- crc >>> 1

        crc ^^^ UInt32.MaxValue

    let private fixCrc (bytes: byte array) =
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4, 4), crc32 bytes (bytes.Length - 4))

    [<Fact>]
    let ``Tx RX-only writer preserves the frozen v1 bytes`` () =
        match write legacyImage with
        | Error errors -> failwithf "Expected legacy image write to succeed, got %A" errors
        | Ok bytes -> bytes |> should equal (frozenLegacyBytes ())

    [<Fact>]
    let ``Tx image roundtrips and writes deterministically`` () =
        match write txImage, write txImage with
        | Ok first, Ok second ->
            first |> should equal second
            first.[10] |> should equal 1uy

            BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(22, 2)) |> should equal 1us

            match read first with
            | Error errors -> failwithf "Expected TX image read to succeed, got %A" errors
            | Ok actual -> actual |> should equal txImage
        | first, second -> failwithf "Expected TX writes to succeed, got %A / %A" first second

    [<Fact>]
    let ``Tx legacy RX image reads without semantic changes`` () =
        match read (frozenLegacyBytes ()) with
        | Error errors -> failwithf "Expected frozen legacy image to read, got %A" errors
        | Ok image ->
            image.Messages |> should equal legacyImage.Messages

            image.Programs |> should equal legacyImage.Programs

            image.PoolSlotCount |> should equal 1us
            image.TxMessages |> should be Empty
            image.TxPrograms |> should be Empty
            image.TxCounters |> should be Empty
            image.TxTemplates |> should be Empty

    [<Fact>]
    let ``Tx reader rejects an unknown feature bit`` () =
        let bytes = frozenLegacyBytes ()
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), 2us)
        fixCrc bytes
        read bytes |> Result.isError |> should equal true

    [<Fact>]
    let ``Tx reader rejects a malformed TX section range`` () =
        let bytes = frozenLegacyBytes ()
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), 1us)
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1us)
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 65u)
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 32u)
        fixCrc bytes

        match read bytes with
        | Ok _ -> failwith "Expected malformed TX range to be rejected"
        | Error errors ->
            errors
            |> List.exists (function
                | ImageAlign
                | ImageBounds
                | ImageTable -> true
                | _ -> false)
            |> should equal true
