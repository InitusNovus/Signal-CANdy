namespace Signal.CANdy.Core.Tests

open System
open System.Buffers.Binary
open Xunit
open FsUnit.Xunit
open Signal.CANdy.Core.Scimg

module ProtectionScimgTests =

    let private identity =
        { IsAffine = false
          Factor = 1.0
          Offset = 0.0 }

    let private program start length slot =
        { StartBit = start
          LengthBits = length
          OrderFlags = 0uy
          Storage = 0uy
          ConversionIndex = 0us
          SlotIndex = slot
          MuxSelectorSlot = UInt16.MaxValue
          MuxExpected = UInt32.MaxValue }

    let private emptyPlan: ImageProtectionPlan =
        { HasCrc = false
          HasCounter = false
          Algorithm = 0uy
          CrcWidthBytes = 0uy
          CrcBigEndian = false
          CrcStartBit = UInt16.MaxValue
          SpanIndex = UInt16.MaxValue
          SpanCount = 0uy
          DataId = None
          CounterIndex = UInt16.MaxValue }

    let private protectionImage: RuntimeImage =
        { Messages =
            [ { EncodedCanId = 0x326u
                ProgramCount = 1us
                ProgramIndex = 0us } ]
          Programs = [ program 8us 16us 0us ]
          Conversions = [ identity ]
          PoolSlotCount = 2us
          SignalNames = [ "RxValue"; "TxValue" ]
          MessageNames = [ "ProtectedRx" ]
          TxMessages =
            [ { LogicalMessageId = 33u
                EncodedCanId = 0x325u
                PayloadLength = 8uy
                FrameFlags = 0uy
                ProgramCount = 1us
                ProgramIndex = 0us
                CounterIndex = 0us
                TemplateOffset = 96u } ]
          TxPrograms = [ program 8us 16us 1us ]
          TxCounters =
            [ { StartBit = 0us
                LengthBits = 4us
                BigEndian = false
                Modulus = 16u
                Increment = 1u
                InitialValue = 0u } ]
          TxTemplates = Array.zeroCreate 8
          NestedMuxRecords = []
          QualityEntries = [ { FreshnessMs = 100u }; { FreshnessMs = 0u } ]
          RxProtectionPlans =
            [ { HasCrc = true
                HasCounter = true
                Algorithm = 2uy
                CrcWidthBytes = 2uy
                CrcBigEndian = false
                CrcStartBit = 48us
                SpanIndex = 0us
                SpanCount = 1uy
                DataId = None
                CounterIndex = 0us } ]
          TxProtectionPlans =
            [ { HasCrc = true
                HasCounter = true
                Algorithm = 1uy
                CrcWidthBytes = 1uy
                CrcBigEndian = false
                CrcStartBit = 56us
                SpanIndex = 1us
                SpanCount = 1uy
                DataId = None
                CounterIndex = 0us } ]
          RxCounters =
            [ { StartBit = 0us
                LengthBits = 4us
                BigEndian = false
                Modulus = 16u
                Increment = 1u } ]
          CoverageSpans = [ { ByteOffset = 0uy; ByteCount = 6uy }; { ByteOffset = 0uy; ByteCount = 7uy } ] }

    let private legacyRxImage: RuntimeImage =
        { Messages =
            [ { EncodedCanId = 0x100u
                ProgramCount = 1us
                ProgramIndex = 0us } ]
          Programs = [ program 0us 8us 0us ]
          Conversions = [ identity ]
          PoolSlotCount = 1us
          SignalNames = [ "value" ]
          MessageNames = [ "frame" ]
          TxMessages = []
          TxPrograms = []
          TxCounters = []
          TxTemplates = [||]
          NestedMuxRecords = []
          QualityEntries = []
          RxProtectionPlans = []
          TxProtectionPlans = []
          RxCounters = []
          CoverageSpans = [] }

    let private legacyTxImage: RuntimeImage =
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
          TxPrograms = [ program 0us 8us 0us ]
          TxCounters =
            [ { StartBit = 8us
                LengthBits = 4us
                BigEndian = false
                Modulus = 16u
                Increment = 1u
                InitialValue = 0u } ]
          TxTemplates = Array.zeroCreate 8
          NestedMuxRecords = []
          QualityEntries = []
          RxProtectionPlans = []
          TxProtectionPlans = []
          RxCounters = []
          CoverageSpans = [] }

    let private legacyRxqImage: RuntimeImage =
        { legacyRxImage with
            QualityEntries = [ { FreshnessMs = 25u } ] }

    let private writeBytes image =
        match write image with
        | Ok bytes -> bytes
        | Error errors -> failwithf "Expected image write, got %A" errors

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

    let private protectionOffset (bytes: byte array) =
        let extension = extensionOffset bytes

        extension
        + int (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(extension + 28, 4)))

    let private rejectMutation mutate =
        let bytes = writeBytes protectionImage
        mutate bytes
        fixCrc bytes
        read bytes |> Result.isError |> should equal true

    [<Fact>]
    let ``Protection PR01 roundtrips deterministically with dense EX01 order`` () =
        match write protectionImage, write protectionImage with
        | Ok first, Ok second ->
            second |> should equal first

            BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(10, 2)) |> should equal 7us

            let extension = extensionOffset first

            BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(extension, 4))
            |> should equal 0x31305845u

            BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(extension + 4, 2))
            |> should equal 14us

            let profileRelative =
                BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(extension + 28, 4))

            let profileSize =
                BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(extension + 32, 4))

            let txRelative =
                BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(extension + 20, 4))

            profileRelative |> should equal 48u
            profileSize |> should equal 104u
            txRelative |> should equal 152u

            let profile = extension + int profileRelative

            BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(profile, 4))
            |> should equal 0x31305250u

            BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(profile + 4, 2))
            |> should equal 1us

            BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(profile + 6, 2))
            |> should equal 1us

            BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(profile + 8, 2))
            |> should equal 1us

            BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(profile + 10, 2))
            |> should equal 2us

            BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(profile + 12, 4))
            |> should equal 48u

            BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(profile + 16, 4))
            |> should equal 64u

            BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(profile + 20, 4))
            |> should equal 80u

            BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(profile + 24, 4))
            |> should equal 96u

            BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(profile + 28, 4))
            |> should equal 104u

            match read first with
            | Ok actual -> actual |> should equal protectionImage
            | Error errors -> failwithf "Expected PR01 read, got %A" errors
        | first, second -> failwithf "Expected deterministic PR01 writes, got %A / %A" first second

    [<Fact>]
    let ``Protection without RXQ writes zero quality count and roundtrips`` () =
        let image =
            { protectionImage with
                QualityEntries = [] }

        let bytes = writeBytes image
        let extension = extensionOffset bytes

        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(extension + 10, 2))
        |> should equal 0us

        match read bytes with
        | Ok actual -> actual |> should equal image
        | Error errors -> failwithf "Expected protection-only image read, got %A" errors

    [<Fact>]
    let ``Protection empty plan uses exact absent sentinels`` () =
        let image =
            { protectionImage with
                RxProtectionPlans = [ emptyPlan ]
                RxCounters = []
                CoverageSpans = protectionImage.CoverageSpans |> List.skip 1
                TxProtectionPlans =
                    [ { protectionImage.TxProtectionPlans.Head with
                          SpanIndex = 0us } ] }

        let bytes = writeBytes image
        let plan = protectionOffset bytes + 48
        bytes.[plan] |> should equal 0uy
        bytes.[plan + 1] |> should equal 0uy

        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(plan + 4, 2))
        |> should equal UInt16.MaxValue

        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(plan + 6, 2))
        |> should equal UInt16.MaxValue

        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(plan + 10, 2))
        |> should equal UInt16.MaxValue

    [<Fact>]
    let ``Protection legacy RX TX and RXQ bytes stay frozen`` () =
        let frozenRx =
            Convert.FromHexString(
                "5343494D473031000100000088000000010001000100000000000000000000004000000008000000480000001000000058000000180000007000000014000000000100000100000000000800000000000000FFFFFFFFFFFF0000000000000000000000000000F03F000000000000000001000100050076616C756505006672616D650000DA2DED64"
            )

        let frozenTx =
            Convert.FromHexString(
                "5343494D4730310001000100D40000000000000001000100680000006800000040000000000000004000000000000000400000001800000058000000100000000000000000000000000000000000F03F0000000000000000010000000700636F6D6D616E64000000545830310100010001000000200000003800000048000000600000000800000007000000210300000800010000000000600000000000000000000800000000000000FFFFFFFFFFFF080004000000000010000000010000000000000000000000000000000000000095117879"
            )

        let frozenRxq =
            Convert.FromHexString(
                "5343494D4730310001000200B40000000100010001000100840000002C0000004000000008000000480000001000000058000000180000007000000014000000000100000100000000000800000000000000FFFFFFFFFFFF0000000000000000000000000000F03F000000000000000001000100050076616C756505006672616D65000045583031020004000000010028000000280000002C000000000000000000000000000000000000001900000061536D59"
            )

        writeBytes legacyRxImage |> should equal frozenRx
        writeBytes legacyTxImage |> should equal frozenTx

        writeBytes legacyRxqImage |> should equal frozenRxq

    [<Fact>]
    let ``Protection reader rejects unknown main and EX01 flags`` () =
        rejectMutation (fun bytes -> BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), 15us))

        rejectMutation (fun bytes ->
            let extension = extensionOffset bytes
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(extension + 4, 2), 30us))

    [<Fact>]
    let ``Protection reader rejects non-dense PR01 pointers`` () =
        rejectMutation (fun bytes ->
            let extension = extensionOffset bytes
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extension + 28, 4), 52u))

        rejectMutation (fun bytes ->
            let profile = protectionOffset bytes
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(profile + 16, 4), 68u))

    [<Fact>]
    let ``Protection reader rejects PR01 count mismatch and reserved bytes`` () =
        rejectMutation (fun bytes ->
            let profile = protectionOffset bytes
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(profile + 4, 2), 0us))

        rejectMutation (fun bytes ->
            let profile = protectionOffset bytes
            bytes.[profile + 32] <- 1uy)

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    [<InlineData(3)>]
    [<InlineData(4)>]
    [<InlineData(5)>]
    let ``Protection reader rejects malformed plan flags and sentinels`` mutation =
        rejectMutation (fun bytes ->
            let plan = protectionOffset bytes + 48

            match mutation with
            | 0 -> bytes.[plan] <- 7uy
            | 1 -> bytes.[plan + 1] <- 3uy
            | 2 -> bytes.[plan + 3] <- 2uy
            | 3 -> BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(plan + 4, 2), UInt16.MaxValue)
            | 4 -> bytes.[plan + 9] <- 1uy
            | _ -> BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(plan + 10, 2), UInt16.MaxValue))

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    [<InlineData(3)>]
    let ``Protection reader rejects malformed coverage spans`` mutation =
        rejectMutation (fun bytes ->
            let profile = protectionOffset bytes
            let rxPlan = profile + 48
            let spans = profile + 96

            match mutation with
            | 0 -> bytes.[spans + 1] <- 0uy
            | 1 ->
                bytes.[spans] <- 6uy
                bytes.[spans + 1] <- 1uy
            | 2 -> BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(rxPlan + 6, 2), 2us)
            | _ -> bytes.[rxPlan + 8] <- 3uy)

    [<Fact>]
    let ``Protection reader rejects TX plan counter mismatch`` () =
        rejectMutation (fun bytes ->
            let txPlan = protectionOffset bytes + 64
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(txPlan + 10, 2), UInt16.MaxValue))

    [<Fact>]
    let ``Protection reader rejects profile CRC mutation`` () =
        let bytes = writeBytes protectionImage
        bytes.[protectionOffset bytes + 1] <- bytes.[protectionOffset bytes + 1] ^^^ 1uy
        read bytes |> Result.isError |> should equal true
