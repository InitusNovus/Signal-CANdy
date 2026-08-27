namespace Signal.CANdy.Core.Tests

open System
open Xunit
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.RuntimeCapabilities
open Signal.CANdy.Core.RuntimeRequirements
open Signal.CANdy.Core.Scimg

module RuntimeRequirementsTests =

    let private unwrap result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "Expected success, got %A" errors

    let private poolSignal index (direction: Direction) =
        { Name = sprintf "slot%d" index
          SemanticId = uint32 (index + 1)
          Storage = U8
          Unit = ""
          Direction = direction
          Min = None
          Max = None
          Default = None
          FreshnessMs = None }

    let private pool count =
        { Name = "requirements"
          Signals = [ for index in 0 .. count - 1 -> poolSignal index Direction.Rx ] }

    let private program start slot =
        { StartBit = start
          LengthBits = 8us
          OrderFlags = 0uy
          Storage = 0uy
          ConversionIndex = 0us
          SlotIndex = slot
          MuxSelectorSlot = UInt16.MaxValue
          MuxExpected = UInt32.MaxValue }

    let private emptyImage =
        { Messages = []
          Programs = []
          Conversions = []
          PoolSlotCount = 0us
          SignalNames = []
          MessageNames = []
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

    let private emptyLinked =
        { PoolSlots = []
          Messages = []
          TxMessages = [] }

    let private decodePlan =
        { PoolSignalName = "slot0"
          WireSignalName = "wire0"
          PoolSlotIndex = 0us
          StartBit = 0us
          Length = 8us
          ByteOrder = Little
          IsSigned = false
          Factor = 1.0
          Offset = 0.0
          Storage = U8
          IsMuxSelector = false
          MuxPath = [] }

    let private encodePlan =
        { PoolSignalName = "slot0"
          WireSignalName = "wire0"
          PoolSlotIndex = 0us
          StartBit = 0us
          Length = 8us
          ByteOrder = Little
          IsSigned = false
          Factor = 1.0
          Offset = 0.0
          Storage = U8
          PhysicalMin = None
          PhysicalMax = None
          IsMuxSelector = false
          MuxPath = [] }

    let private linkedRx length isExtended plans protection =
        { Name = "rx"
          Id = 0x100u
          IsExtended = isExtended
          Length = length
          Plans = plans
          Protection = protection }

    let private linkedTx length isExtended plans crc counter =
        { Name = "tx"
          LogicalMessageId = 1u
          CanId = 0x101u
          IsExtended = isExtended
          Length = length
          Plans = plans
          Crc = crc
          Counter = counter }

    let private deriveFor poolContract linked image byteCount =
        derive poolContract linked image (Array.zeroCreate byteCount) |> unwrap

    [<Theory>]
    [<InlineData(0u, 0u, 0u, 0u, 0u)>]
    [<InlineData(1u, 0u, 1u, 0u, 20u)>]
    [<InlineData(3u, 0u, 1u, 1u, 28u)>]
    [<InlineData(3u, 1u, 0u, 0u, 40u)>]
    [<InlineData(3u, 1u, 1u, 1u, 60u)>]
    [<InlineData(8192u, 1u, 4096u, 4096u, 147472u)>]
    let ``ILP32 state bytes use the exact base counter quality formula``
        poolSlots
        qualityEntries
        txCounters
        rxCounters
        expected
        =
        Assert.Equal(expected, runtimeStateBytes Ilp32 poolSlots qualityEntries txCounters rxCounters |> unwrap)

    [<Fact>]
    let ``ILP32 state formula rejects checked uint32 overflow`` () =
        runtimeStateBytes Ilp32 UInt32.MaxValue 1u UInt32.MaxValue UInt32.MaxValue
        |> Result.isError
        |> Assert.True

    [<Fact>]
    let ``Runtime scratch is the largest TX payload and RX contributes nothing`` () =
        let tx payload =
            { LogicalMessageId = uint32 payload
              EncodedCanId = uint32 payload
              PayloadLength = payload
              FrameFlags = 0uy
              ProgramCount = 0us
              ProgramIndex = 0us
              CounterIndex = UInt16.MaxValue
              TemplateOffset = 0u }

        Assert.Equal(
            64u,
            runtimeScratchBytes
                { emptyImage with
                    TxMessages = [ tx 8uy; tx 64uy; tx 12uy ] }
        )

        Assert.Equal(
            0u,
            runtimeScratchBytes
                { emptyImage with
                    Messages =
                        [ { EncodedCanId = 1u
                            ProgramCount = 0us
                            ProgramIndex = 0us } ] }
        )

    [<Fact>]
    let ``Requirement derivation pins all eighteen resources`` () =
        let linked =
            { emptyLinked with
                Messages = [ linkedRx 12us false [ decodePlan; { decodePlan with StartBit = 8us } ] None ]
                TxMessages = [ linkedTx 8us false [ encodePlan ] None None ] }

        let image =
            { emptyImage with
                Messages =
                    [ { EncodedCanId = 0x100u
                        ProgramCount = 2us
                        ProgramIndex = 0us } ]
                Programs = [ program 0us 0us; program 8us 1us ]
                TxMessages =
                    [ { LogicalMessageId = 1u
                        EncodedCanId = 0x101u
                        PayloadLength = 8uy
                        FrameFlags = 0uy
                        ProgramCount = 1us
                        ProgramIndex = 0us
                        CounterIndex = 0us
                        TemplateOffset = 0u } ]
                TxPrograms = [ program 0us 2us ]
                PoolSlotCount = 3us
                Conversions =
                    [ { IsAffine = false
                        Factor = 1.0
                        Offset = 0.0 }
                      { IsAffine = true
                        Factor = 2.0
                        Offset = 1.0 } ]
                NestedMuxRecords =
                    [ { TargetProgramIndex = 1us
                        Predicates =
                          [ { SelectorProgramIndex = 0us
                              SelectorSlot = 0us
                              Expected = 1u }
                            { SelectorProgramIndex = 1us
                              SelectorSlot = 1us
                              Expected = 2u } ] } ]
                QualityEntries = [ { FreshnessMs = 1u }; { FreshnessMs = 0u }; { FreshnessMs = 0u } ]
                RxProtectionPlans =
                    [ { HasCrc = false
                        HasCounter = false
                        Algorithm = 0uy
                        CrcWidthBytes = 0uy
                        CrcBigEndian = false
                        CrcStartBit = UInt16.MaxValue
                        SpanIndex = UInt16.MaxValue
                        SpanCount = 0uy
                        DataId = None
                        CounterIndex = UInt16.MaxValue } ]
                TxProtectionPlans =
                    [ { HasCrc = false
                        HasCounter = true
                        Algorithm = 0uy
                        CrcWidthBytes = 0uy
                        CrcBigEndian = false
                        CrcStartBit = UInt16.MaxValue
                        SpanIndex = UInt16.MaxValue
                        SpanCount = 0uy
                        DataId = None
                        CounterIndex = 0us } ]
                TxCounters =
                    [ { StartBit = 0us
                        LengthBits = 4us
                        BigEndian = false
                        Modulus = 16u
                        Increment = 1u
                        InitialValue = 0u } ]
                RxCounters =
                    [ { StartBit = 0us
                        LengthBits = 4us
                        BigEndian = false
                        Modulus = 16u
                        Increment = 1u } ]
                CoverageSpans = [ { ByteOffset = 0uy; ByteCount = 1uy }; { ByteOffset = 2uy; ByteCount = 3uy } ]
                TxTemplates = Array.zeroCreate 8 }

        let actual = deriveFor (pool 3) linked image 428
        Assert.Equal(428u, actual.ImageBytes)
        Assert.Equal(60u, actual.RuntimeStateBytes)
        Assert.Equal(8u, actual.RuntimeScratchBytes)
        Assert.Equal(1u, actual.RxMessages)
        Assert.Equal(2u, actual.RxPrograms)
        Assert.Equal(1u, actual.TxMessages)
        Assert.Equal(1u, actual.TxPrograms)
        Assert.Equal(3u, actual.PoolSlots)
        Assert.Equal(2u, actual.Conversions)
        Assert.Equal(1u, actual.NestedMuxRecords)
        Assert.Equal(2u, actual.MuxDepth)
        Assert.Equal(3u, actual.QualityEntries)
        Assert.Equal(2u, actual.ProtectionPlans)
        Assert.Equal(1u, actual.TxCounters)
        Assert.Equal(1u, actual.RxCounters)
        Assert.Equal(2u, actual.CoverageSpans)
        Assert.Equal(8u, actual.TxTemplateBytes)
        Assert.Equal(12u, actual.PayloadBytes)

    [<Fact>]
    let ``Requirement derivation detects baseline RX TX FD extended Motorola and affine features`` () =
        let rxPlan = { decodePlan with ByteOrder = Big }
        let txPlan = { encodePlan with ByteOrder = Big }

        let linked =
            { emptyLinked with
                Messages = [ linkedRx 12us true [ rxPlan ] None ]
                TxMessages = [ linkedTx 64us true [ txPlan ] None None ] }

        let image =
            { emptyImage with
                Messages =
                    [ { EncodedCanId = 0x80000100u
                        ProgramCount = 1us
                        ProgramIndex = 0us } ]
                Programs =
                    [ { program 0us 0us with
                          OrderFlags = 1uy } ]
                TxMessages =
                    [ { LogicalMessageId = 1u
                        EncodedCanId = 0x80000101u
                        PayloadLength = 64uy
                        FrameFlags = 1uy
                        ProgramCount = 1us
                        ProgramIndex = 0us
                        CounterIndex = UInt16.MaxValue
                        TemplateOffset = 0u } ]
                TxPrograms =
                    [ { program 0us 0us with
                          OrderFlags = 1uy } ]
                PoolSlotCount = 1us
                Conversions =
                    [ { IsAffine = true
                        Factor = 2.0
                        Offset = 1.0 } ]
                TxTemplates = Array.zeroCreate 64 }

        let features = (deriveFor (pool 1) linked image 256).Features

        set [ Rx; Tx; CanFd; ExtendedCan; Motorola; Affine ]
        |> Set.iter (fun feature -> Assert.Contains(feature, features))

    [<Fact>]
    let ``Requirement derivation detects direct and nested mux plus RX quality`` () =
        let predicate expected =
            { SelectorSlot = 0us
              SelectorProgramName = "selector"
              Expected = expected }

        let plans =
            [ { decodePlan with IsMuxSelector = true }
              { decodePlan with
                  PoolSlotIndex = 1us
                  MuxPath = [ predicate 1u ] }
              { decodePlan with
                  PoolSlotIndex = 2us
                  MuxPath = [ predicate 1u; predicate 2u ] } ]

        let linked =
            { emptyLinked with
                Messages = [ linkedRx 8us false plans None ] }

        let image =
            { emptyImage with
                Messages =
                    [ { EncodedCanId = 0x100u
                        ProgramCount = 3us
                        ProgramIndex = 0us } ]
                Programs = [ program 0us 0us; program 8us 1us; program 16us 2us ]
                PoolSlotCount = 3us
                NestedMuxRecords =
                    [ { TargetProgramIndex = 2us
                        Predicates =
                          [ { SelectorProgramIndex = 0us
                              SelectorSlot = 0us
                              Expected = 1u }
                            { SelectorProgramIndex = 1us
                              SelectorSlot = 1us
                              Expected = 2u } ] } ]
                QualityEntries = [ { FreshnessMs = 10u }; { FreshnessMs = 0u }; { FreshnessMs = 0u } ] }

        let features = (deriveFor (pool 3) linked image 256).Features

        set [ Rx; Multiplexing; NestedMux; RxQuality ]
        |> Set.iter (fun feature -> Assert.Contains(feature, features))

    [<Fact>]
    let ``Requirement derivation detects every exact protection feature`` () =
        let crc algorithm dataId =
            { WireSignalName = "crc"
              Algorithm = algorithm
              StartBit = 48us
              LengthBits = 8us
              BigEndian = true
              CoverageSpans = [ { ByteOffset = 0uy; ByteCount = 6uy } ]
              DataId = dataId }

        let rxCounter =
            { WireSignalName = "counter"
              StartBit = 0us
              Length = 4us
              ByteOrder = Big
              Modulus = 16u
              Increment = 1u }

        let txCounter =
            { WireSignalName = "counter"
              StartBit = 0us
              Length = 4us
              ByteOrder = Big
              Modulus = 16u
              Increment = 1u
              InitialValue = 0u }

        let linked =
            { emptyLinked with
                Messages =
                    [ linkedRx
                          8us
                          false
                          [ decodePlan ]
                          (Some
                              { Crc = Some(crc LinkedCrcAlgorithm.Crc16CcittFalse None)
                                Counter = Some rxCounter }) ]
                TxMessages =
                    [ linkedTx
                          8us
                          false
                          [ encodePlan ]
                          (Some(crc LinkedCrcAlgorithm.Crc8SaeJ1850 (Some 0x1234us)))
                          (Some txCounter) ] }

        let plan algorithm dataId =
            { HasCrc = true
              HasCounter = true
              Algorithm = algorithm
              CrcWidthBytes = if algorithm = 1uy then 1uy else 2uy
              CrcBigEndian = true
              CrcStartBit = 48us
              SpanIndex = 0us
              SpanCount = 1uy
              DataId = dataId
              CounterIndex = 0us }

        let image =
            { emptyImage with
                Messages =
                    [ { EncodedCanId = 0x100u
                        ProgramCount = 1us
                        ProgramIndex = 0us } ]
                Programs = [ program 8us 0us ]
                TxMessages =
                    [ { LogicalMessageId = 1u
                        EncodedCanId = 0x101u
                        PayloadLength = 8uy
                        FrameFlags = 0uy
                        ProgramCount = 1us
                        ProgramIndex = 0us
                        CounterIndex = 0us
                        TemplateOffset = 0u } ]
                TxPrograms = [ program 8us 0us ]
                PoolSlotCount = 1us
                RxProtectionPlans = [ plan 2uy None ]
                TxProtectionPlans = [ plan 1uy (Some 0x1234us) ]
                TxCounters =
                    [ { StartBit = 0us
                        LengthBits = 4us
                        BigEndian = true
                        Modulus = 16u
                        Increment = 1u
                        InitialValue = 0u } ]
                RxCounters =
                    [ { StartBit = 0us
                        LengthBits = 4us
                        BigEndian = true
                        Modulus = 16u
                        Increment = 1u } ]
                CoverageSpans = [ { ByteOffset = 0uy; ByteCount = 6uy } ]
                TxTemplates = Array.zeroCreate 8 }

        let features = (deriveFor (pool 1) linked image 320).Features

        set [ Crc8SaeJ1850; Crc16CcittFalse; CrcDataId; RxCounter; TxCounter; Motorola ]
        |> Set.iter (fun feature -> Assert.Contains(feature, features))
