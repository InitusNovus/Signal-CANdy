namespace Signal.CANdy.Core.Tests

open System
open System.Buffers.Binary
open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core.Scimg

module ScimgQualityTests =

    let private identity =
        { IsAffine = false
          Factor = 1.0
          Offset = 0.0 }

    let private program start length slot selector expected =
        { StartBit = start
          LengthBits = length
          OrderFlags = 0uy
          Storage = 0uy
          ConversionIndex = 0us
          SlotIndex = slot
          MuxSelectorSlot = selector
          MuxExpected = expected }

    let private qualityImage: RuntimeImage =
        { Messages =
            [ { EncodedCanId = 0x324u
                ProgramCount = 3us
                ProgramIndex = 0us } ]
          Programs =
            [ program 0us 2us 0us UInt16.MaxValue UInt32.MaxValue
              program 2us 2us 1us 0us 1u
              program 16us 8us 2us 0us 1u ]
          Conversions = [ identity ]
          PoolSlotCount = 3us
          SignalNames = [ "Outer"; "Inner"; "Leaf" ]
          MessageNames = [ "NestedFrame" ]
          TxMessages = []
          TxPrograms = []
          TxCounters = []
          TxTemplates = [||]
          NestedMuxRecords =
            [ ({ TargetProgramIndex = 2us
                 Predicates =
                   [ ({ SelectorProgramIndex = 0us
                        SelectorSlot = 0us
                        Expected = 1u }
                     : ImageMuxPredicate)
                     ({ SelectorProgramIndex = 1us
                        SelectorSlot = 1us
                        Expected = 2u }
                     : ImageMuxPredicate) ] }
              : ImageNestedMuxRecord) ]
          QualityEntries =
            [ ({ FreshnessMs = 0u }: ImageQualityEntry)
              ({ FreshnessMs = 0u }: ImageQualityEntry)
              ({ FreshnessMs = 200u }: ImageQualityEntry) ] }

    let private bytesFixture () =
        match write qualityImage with
        | Ok bytes -> bytes
        | Error errors -> failwithf "Expected RXQ image write, got %A" errors

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

    let private extensionOffset (bytes: byte array) =
        int (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)))

    let private rejectMutation mutate =
        let bytes = bytesFixture ()
        mutate bytes
        fixCrc bytes
        read bytes |> Result.isError |> should equal true

    [<Fact>]
    let ``Quality EX01 NMX and quality table roundtrip deterministically`` () =
        match write qualityImage, write qualityImage with
        | Ok first, Ok second ->
            second |> should equal first
            BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(10, 2)) |> should equal 2us

            let extension = extensionOffset first

            BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(extension, 4))
            |> should equal 0x31305845u

            first.[extension + 6] |> should equal 4uy

            BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(extension + 8, 2))
            |> should equal 1us

            BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(extension + 10, 2))
            |> should equal 3us

            BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(extension + 12, 4))
            |> should equal 40u

            BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(extension + 16, 4))
            |> should equal 76u

            match read first with
            | Error errors -> failwithf "Expected RXQ image read, got %A" errors
            | Ok actual -> actual |> should equal qualityImage
        | first, second -> failwithf "Expected deterministic RXQ writes, got %A / %A" first second

    [<Fact>]
    let ``Quality reader rejects unknown main feature`` () =
        rejectMutation (fun bytes -> BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), 6us))

    [<Fact>]
    let ``Quality reader rejects non-dense EX01 offset`` () =
        rejectMutation (fun bytes ->
            let extension = extensionOffset bytes
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 12, 4), 44u))

    [<Fact>]
    let ``Quality reader rejects NMX depth above four`` () =
        rejectMutation (fun bytes ->
            let extension = extensionOffset bytes
            bytes.[extension + 42] <- 5uy)

    [<Fact>]
    let ``Quality reader rejects NMX target index outside RX programs`` () =
        rejectMutation (fun bytes ->
            let extension = extensionOffset bytes
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(extension + 40, 2), 99us))

    [<Fact>]
    let ``Quality reader rejects NMX selector index outside RX programs`` () =
        rejectMutation (fun bytes ->
            let extension = extensionOffset bytes
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(extension + 44, 2), 99us))

    [<Fact>]
    let ``Quality reader rejects quality count unequal to pool slots`` () =
        rejectMutation (fun bytes ->
            let extension = extensionOffset bytes
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(extension + 10, 2), 2us))

    [<Fact>]
    let ``Quality reader rejects threshold above int32 maximum`` () =
        rejectMutation (fun bytes ->
            let extension = extensionOffset bytes

            let qualityOffset =
                int (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(extension + 16, 4)))

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + qualityOffset + 8, 4), 0x80000000u))

    [<Fact>]
    let ``Quality reader rejects CRC mutation`` () =
        let bytes = bytesFixture ()
        bytes.[extensionOffset bytes + 7] <- 1uy
        read bytes |> Result.isError |> should equal true

    let private legacyRxImage: RuntimeImage =
        { Messages =
            [ { EncodedCanId = 0x100u
                ProgramCount = 1us
                ProgramIndex = 0us } ]
          Programs = [ program 0us 8us 0us UInt16.MaxValue UInt32.MaxValue ]
          Conversions = [ identity ]
          PoolSlotCount = 1us
          SignalNames = [ "value" ]
          MessageNames = [ "frame" ]
          TxMessages = []
          TxPrograms = []
          TxCounters = []
          TxTemplates = [||]
          NestedMuxRecords = []
          QualityEntries = [] }

    let private txOnlyImage: RuntimeImage =
        { Messages = []
          Programs = []
          Conversions = [ identity ]
          PoolSlotCount = 1us
          SignalNames = [ "command" ]
          MessageNames = []
          TxMessages =
            [ { LogicalMessageId = 7u
                EncodedCanId = 0x321u
                PayloadLength = 8uy
                FrameFlags = 0uy
                ProgramCount = 1us
                ProgramIndex = 0us
                CounterIndex = 0us
                TemplateOffset = 96u } ]
          TxPrograms = [ program 0us 8us 0us UInt16.MaxValue UInt32.MaxValue ]
          TxCounters =
            [ { StartBit = 8us
                LengthBits = 4us
                BigEndian = false
                Modulus = 16u
                Increment = 1u
                InitialValue = 0u } ]
          TxTemplates = Array.zeroCreate 8
          NestedMuxRecords = []
          QualityEntries = [] }

    [<Fact>]
    let ``Quality legacy RX bytes remain frozen when feature is absent`` () =
        let frozen =
            Convert.FromHexString(
                "5343494D473031000100000088000000010001000100000000000000000000004000000008000000480000001000000058000000180000007000000014000000000100000100000000000800000000000000FFFFFFFFFFFF0000000000000000000000000000F03F000000000000000001000100050076616C756505006672616D650000DA2DED64"
            )

        match write legacyRxImage with
        | Error errors -> failwithf "Expected frozen legacy RX write, got %A" errors
        | Ok bytes -> bytes |> should equal frozen

    [<Fact>]
    let ``Quality TX-only bytes remain frozen when RXQ feature is absent`` () =
        let frozen =
            Convert.FromHexString(
                "5343494D4730310001000100D40000000000000001000100680000006800000040000000000000004000000000000000400000001800000058000000100000000000000000000000000000000000F03F0000000000000000010000000700636F6D6D616E64000000545830310100010001000000200000003800000048000000600000000800000007000000210300000800010000000000600000000000000000000800000000000000FFFFFFFFFFFF080004000000000010000000010000000000000000000000000000000000000095117879"
            )

        match write txOnlyImage with
        | Error errors -> failwithf "Expected frozen TX-only write, got %A" errors
        | Ok bytes -> bytes |> should equal frozen
