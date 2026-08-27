namespace Signal.CANdy.Core

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Signal.CANdy.Core.Errors
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.Wire

module Scimg =

    [<Literal>]
    let private MaxMessages = 4096

    [<Literal>]
    let private MaxPrograms = 8192

    [<Literal>]
    let private MaxConversions = 1024

    [<Literal>]
    let private MaxImageSize = 1024 * 1024

    [<Literal>]
    let private HeaderSize = 32

    [<Literal>]
    let private DirectorySize = 32

    [<Literal>]
    let private TxHeaderSize = 32

    [<Literal>]
    let private TxMessageSize = 24

    [<Literal>]
    let private ProgramSize = 16

    [<Literal>]
    let private CounterSize = 24

    [<Literal>]
    let private ExtensionHeaderSize = 40

    [<Literal>]
    let private NestedMuxRecordSize = 36

    [<Literal>]
    let private ProtectionHeaderSize = 48

    [<Literal>]
    let private ProtectionPlanSize = 16

    [<Literal>]
    let private RxCounterSize = 16

    [<Literal>]
    let private CoverageSpanSize = 4

    [<Literal>]
    let private MaxCoverageSpans = 16384

    let private magic =
        [| 0x53uy; 0x43uy; 0x49uy; 0x4Duy; 0x47uy; 0x30uy; 0x31uy; 0uy |]

    type ImageProgram =
        { StartBit: uint16
          LengthBits: uint16
          OrderFlags: uint8
          Storage: uint8
          ConversionIndex: uint16
          SlotIndex: uint16
          MuxSelectorSlot: uint16
          MuxExpected: uint32 }

    type ImageMessage =
        { EncodedCanId: uint32
          ProgramCount: uint16
          ProgramIndex: uint16 }

    type ImageConversion =
        { IsAffine: bool
          Factor: float
          Offset: float }

    type ImageTxMessage =
        { LogicalMessageId: uint32
          EncodedCanId: uint32
          PayloadLength: uint8
          FrameFlags: uint8
          ProgramCount: uint16
          ProgramIndex: uint16
          CounterIndex: uint16
          TemplateOffset: uint32 }

    type ImageTxCounter =
        { StartBit: uint16
          LengthBits: uint16
          BigEndian: bool
          Modulus: uint32
          Increment: uint32
          InitialValue: uint32 }

    type ImageMuxPredicate =
        { SelectorProgramIndex: uint16
          SelectorSlot: uint16
          Expected: uint32 }

    type ImageNestedMuxRecord =
        { TargetProgramIndex: uint16
          Predicates: ImageMuxPredicate list }

    type ImageQualityEntry = { FreshnessMs: uint32 }

    type ImageProtectionPlan =
        { HasCrc: bool
          HasCounter: bool
          Algorithm: uint8
          CrcWidthBytes: uint8
          CrcBigEndian: bool
          CrcStartBit: uint16
          SpanIndex: uint16
          SpanCount: uint8
          DataId: uint16 option
          CounterIndex: uint16 }

    type ImageRxCounter =
        { StartBit: uint16
          LengthBits: uint16
          BigEndian: bool
          Modulus: uint32
          Increment: uint32 }

    type ImageCoverageSpan = { ByteOffset: uint8; ByteCount: uint8 }

    type RuntimeImage =
        { Messages: ImageMessage list
          Programs: ImageProgram list
          Conversions: ImageConversion list
          PoolSlotCount: uint16
          SignalNames: string list
          MessageNames: string list
          TxMessages: ImageTxMessage list
          TxPrograms: ImageProgram list
          TxCounters: ImageTxCounter list
          TxTemplates: byte array
          NestedMuxRecords: ImageNestedMuxRecord list
          QualityEntries: ImageQualityEntry list
          RxProtectionPlans: ImageProtectionPlan list
          TxProtectionPlans: ImageProtectionPlan list
          RxCounters: ImageRxCounter list
          CoverageSpans: ImageCoverageSpan list }

    let private identityConversion =
        { IsAffine = false
          Factor = 1.0
          Offset = 0.0 }

    let private isIntegerStorageValue storage = storage <= 7uy

    let private storageValue storage =
        match storage with
        | U8 -> 0uy
        | U16 -> 1uy
        | U32 -> 2uy
        | U64 -> 3uy
        | I8 -> 4uy
        | I16 -> 5uy
        | I32 -> 6uy
        | I64 -> 7uy
        | F32 -> 8uy
        | F64 -> 9uy

    let private encodedCanId id isExtended =
        if isExtended then 0x80000000u ||| id else id

    let private validCanId id isExtended =
        if isExtended then id <= 0x1FFFFFFFu else id <= 0x7FFu

    let private validEncodedCanId value =
        let extended = (value &&& 0x80000000u) <> 0u
        validCanId (value &&& 0x7FFFFFFFu) extended

    let private validPayloadLength length =
        length <= 8uy
        || length = 12uy
        || length = 16uy
        || length = 20uy
        || length = 24uy
        || length = 32uy
        || length = 48uy
        || length = 64uy

    let private utf8 = UTF8Encoding(false, true)

    let private nameError (name: string) =
        if String.IsNullOrEmpty(name) then
            true
        else
            try
                let bytes = utf8.GetBytes(name)
                bytes.Length > 255 || name.IndexOf('\000') >= 0
            with :? EncoderFallbackException ->
                true

    let private conversionKey factor offset =
        struct (BitConverter.DoubleToInt64Bits(factor), BitConverter.DoubleToInt64Bits(offset))

    let private orderedDecodePlans (message: LinkedMessage) =
        if message.Plans |> List.forall (fun plan -> plan.MuxPath.Length <= 1) then
            message.Plans
            |> List.sortBy (fun plan ->
                let selectorRank = if plan.IsMuxSelector then 0 else 1
                selectorRank, plan.StartBit, plan.PoolSlotIndex)
        else
            message.Plans
            |> List.sortBy (fun plan ->
                let selectorRank = if plan.IsMuxSelector then 0 else 1
                plan.MuxPath.Length, selectorRank, plan.StartBit, plan.PoolSlotIndex)

    let private orderedEncodePlans (message: LinkedTxMessage) =
        message.Plans
        |> List.sortBy (fun plan ->
            let selectorRank = if plan.IsMuxSelector then 0 else 1

            let expectedRank =
                match plan.MuxExpected with
                | None -> struct (0, 0u)
                | Some value -> struct (1, value)

            selectorRank, plan.StartBit, expectedRank, plan.PoolSlotIndex)

    let private imageProgram conversionIndex planStart planLength byteOrder isSigned storage slot muxSlot muxExpected =
        { StartBit = planStart
          LengthBits = planLength
          OrderFlags = (if byteOrder = Big then 1uy else 0uy) ||| (if isSigned then 2uy else 0uy)
          Storage = storageValue storage
          ConversionIndex = conversionIndex
          SlotIndex = slot
          MuxSelectorSlot = muxSlot
          MuxExpected = muxExpected }

    let private muxFields selector expected =
        match selector, expected with
        | Some slot, Some value -> slot, value
        | _ -> UInt16.MaxValue, UInt32.MaxValue

    let private align4 value = (value + 3) / 4 * 4

    let lower (schema: LinkedSchema) : Result<RuntimeImage, ValidationError list> =
        let rxPlans = schema.Messages |> List.collect (fun message -> message.Plans)

        let txPlans = schema.TxMessages |> List.collect (fun message -> message.Plans)

        let hasTx = not schema.TxMessages.IsEmpty

        let errors =
            [ if schema.Messages.Length > MaxMessages || schema.TxMessages.Length > MaxMessages then
                  ImageLimit "message_count exceeds 4096"

              if rxPlans.Length > MaxPrograms || txPlans.Length > MaxPrograms then
                  ImageLimit "program_count exceeds 8192"

              if schema.PoolSlots.Length > MaxPrograms then
                  ImageLimit "pool_slot_count exceeds 8192"

              if hasTx && schema.PoolSlots.IsEmpty then
                  ImageTable

              if not hasTx then
                  let slots = rxPlans |> List.map (fun plan -> plan.PoolSlotIndex) |> List.sort

                  let expected = [ for index in 0 .. rxPlans.Length - 1 -> uint16 index ]

                  if slots <> expected || schema.PoolSlots.Length <> rxPlans.Length then
                      ImageTable

              for slot in schema.PoolSlots do
                  if nameError slot.Name then
                      ImageLimit(sprintf "signal name '%s' is not 1..255 UTF-8 bytes" slot.Name)

              for message in schema.Messages do
                  if not (validCanId message.Id message.IsExtended) || message.Plans.IsEmpty then
                      ImageTable

                  if nameError message.Name then
                      ImageLimit(sprintf "message name '%s' is invalid" message.Name)

                  for plan in message.Plans do
                      if
                          plan.Length < 1us
                          || plan.Length > 64us
                          || uint32 plan.StartBit + uint32 plan.Length > uint32 message.Length * 8u
                          || int plan.PoolSlotIndex >= schema.PoolSlots.Length
                      then
                          ImageTable

                      if
                          isIntegerStorageValue (storageValue plan.Storage)
                          && (plan.Factor <> 1.0 || plan.Offset <> 0.0)
                      then
                          ImageTable

              let rxIds =
                  schema.Messages
                  |> List.map (fun message -> encodedCanId message.Id message.IsExtended)

              if rxIds.Length <> (rxIds |> List.distinct).Length then
                  ImageTable

              let logicalIds =
                  schema.TxMessages |> List.map (fun message -> message.LogicalMessageId)

              if logicalIds.Length <> (logicalIds |> List.distinct).Length then
                  ImageTable

              for message in schema.TxMessages do
                  if
                      not (validCanId message.CanId message.IsExtended)
                      || not (validPayloadLength (uint8 message.Length))
                  then
                      ImageTable

                  if message.Plans.IsEmpty && message.Counter.IsNone then
                      ImageTable

                  for plan in message.Plans do
                      if
                          plan.Length < 1us
                          || plan.Length > 64us
                          || uint32 plan.StartBit + uint32 plan.Length > uint32 message.Length * 8u
                          || int plan.PoolSlotIndex >= schema.PoolSlots.Length
                      then
                          ImageTable

                      if
                          isIntegerStorageValue (storageValue plan.Storage)
                          && (plan.Factor <> 1.0 || plan.Offset <> 0.0)
                      then
                          ImageTable ]

        if not errors.IsEmpty then
            Error errors
        else
            let conversions = ResizeArray<ImageConversion>()
            let indices = Dictionary<struct (int64 * int64), uint16>()
            conversions.Add(identityConversion)
            indices.Add(conversionKey 1.0 0.0, 0us)

            let intern factor offset =
                let key = conversionKey factor offset

                match indices.TryGetValue(key) with
                | true, index -> index
                | false, _ ->
                    let index = uint16 conversions.Count
                    indices.Add(key, index)

                    conversions.Add(
                        { IsAffine = true
                          Factor = factor
                          Offset = offset }
                    )

                    index

            // RX traversal is intentionally unchanged; TX conversions append afterwards.
            for message in schema.Messages do
                for plan in message.Plans do
                    if plan.Factor <> 1.0 || plan.Offset <> 0.0 then
                        intern plan.Factor plan.Offset |> ignore

            let sortedTx =
                schema.TxMessages |> List.sortBy (fun message -> message.LogicalMessageId)

            for message in sortedTx do
                for plan in orderedEncodePlans message do
                    if plan.Factor <> 1.0 || plan.Offset <> 0.0 then
                        intern plan.Factor plan.Offset |> ignore

            if conversions.Count > MaxConversions then
                Error[ImageLimit "conversion_count exceeds 1024"]
            else
                let conversionIndex factor offset =
                    if factor = 1.0 && offset = 0.0 then
                        0us
                    else
                        indices.[conversionKey factor offset]

                let messages = ResizeArray<ImageMessage>()
                let programs = ResizeArray<ImageProgram>()
                let messageNames = ResizeArray<string>()
                let nestedMuxRecords = ResizeArray<ImageNestedMuxRecord>()
                let mutable nextRxProgram = 0

                schema.Messages
                |> List.sortBy (fun message -> encodedCanId message.Id message.IsExtended)
                |> List.iter (fun message ->
                    let plans = orderedDecodePlans message

                    let programIndices =
                        plans
                        |> List.mapi (fun index plan -> plan.WireSignalName, uint16 (nextRxProgram + index))
                        |> Map.ofList

                    messages.Add(
                        { EncodedCanId = encodedCanId message.Id message.IsExtended
                          ProgramCount = uint16 plans.Length
                          ProgramIndex = uint16 nextRxProgram }
                    )

                    messageNames.Add(message.Name)

                    for plan in plans do
                        let firstPredicate = plan.MuxPath |> List.tryHead

                        let muxSlot, muxExpected =
                            match firstPredicate with
                            | Some predicate -> predicate.SelectorSlot, predicate.Expected
                            | None -> UInt16.MaxValue, UInt32.MaxValue

                        programs.Add(
                            imageProgram
                                (conversionIndex plan.Factor plan.Offset)
                                plan.StartBit
                                plan.Length
                                plan.ByteOrder
                                plan.IsSigned
                                plan.Storage
                                plan.PoolSlotIndex
                                muxSlot
                                muxExpected
                        )

                        if plan.MuxPath.Length >= 2 then
                            nestedMuxRecords.Add(
                                { TargetProgramIndex = programIndices.[plan.WireSignalName]
                                  Predicates =
                                    plan.MuxPath
                                    |> List.map (fun predicate ->
                                        { SelectorProgramIndex = programIndices.[predicate.SelectorProgramName]
                                          SelectorSlot = predicate.SelectorSlot
                                          Expected = predicate.Expected }) }
                            )

                    nextRxProgram <- nextRxProgram + plans.Length)

                let txPrograms = ResizeArray<ImageProgram>()
                let txCounters = ResizeArray<ImageTxCounter>()
                let txMessages = ResizeArray<ImageTxMessage>()
                let templates = ResizeArray<byte>()
                let mutable nextTxProgram = 0

                let txMessageTableOffset = TxHeaderSize

                let txProgramTableOffset = txMessageTableOffset + sortedTx.Length * TxMessageSize

                let txProgramCount = sortedTx |> List.sumBy (fun message -> message.Plans.Length)

                let txCounterTableOffset = txProgramTableOffset + txProgramCount * ProgramSize

                let counterCount =
                    sortedTx |> List.sumBy (fun message -> if message.Counter.IsSome then 1 else 0)

                let txTemplateOffset = txCounterTableOffset + counterCount * CounterSize

                for message in sortedTx do
                    let plans = orderedEncodePlans message

                    let counterIndex =
                        match message.Counter with
                        | None -> UInt16.MaxValue
                        | Some counter ->
                            let index = uint16 txCounters.Count

                            txCounters.Add(
                                { StartBit = counter.StartBit
                                  LengthBits = counter.Length
                                  BigEndian = counter.ByteOrder = Big
                                  Modulus = counter.Modulus
                                  Increment = counter.Increment
                                  InitialValue = counter.InitialValue }
                            )

                            index

                    let frameFlags =
                        (if message.IsExtended then 1uy else 0uy)
                        ||| (if message.Length > 8us then 2uy else 0uy)

                    txMessages.Add(
                        { LogicalMessageId = message.LogicalMessageId
                          EncodedCanId = encodedCanId message.CanId message.IsExtended
                          PayloadLength = uint8 message.Length
                          FrameFlags = frameFlags
                          ProgramCount = uint16 plans.Length
                          ProgramIndex = uint16 nextTxProgram
                          CounterIndex = counterIndex
                          TemplateOffset = uint32 (txTemplateOffset + templates.Count) }
                    )

                    for plan in plans do
                        let muxSlot, muxExpected = muxFields plan.MuxSelectorSlot plan.MuxExpected

                        txPrograms.Add(
                            imageProgram
                                (conversionIndex plan.Factor plan.Offset)
                                plan.StartBit
                                plan.Length
                                plan.ByteOrder
                                plan.IsSigned
                                plan.Storage
                                plan.PoolSlotIndex
                                muxSlot
                                muxExpected
                        )

                    for _ in 1 .. int message.Length do
                        templates.Add(0uy)

                    nextTxProgram <- nextTxProgram + plans.Length

                let signalNames =
                    if hasTx then
                        schema.PoolSlots |> List.map (fun slot -> slot.Name)
                    else
                        schema.Messages
                        |> List.collect (fun message -> message.Plans)
                        |> List.sortBy (fun plan -> plan.PoolSlotIndex)
                        |> List.map (fun plan -> plan.PoolSignalName)

                let hasRxq =
                    nestedMuxRecords.Count > 0
                    || (schema.PoolSlots |> List.exists (fun slot -> slot.FreshnessMs.IsSome))

                let qualityEntries =
                    if hasRxq then
                        schema.PoolSlots
                        |> List.map (fun slot -> { FreshnessMs = slot.FreshnessMs |> Option.defaultValue 0u })
                    else
                        []

                let hasProtection =
                    schema.Messages |> List.exists (fun message -> message.Protection.IsSome)
                    || sortedTx |> List.exists (fun message -> message.Crc.IsSome)

                let rxProtectionPlans = ResizeArray<ImageProtectionPlan>()
                let txProtectionPlans = ResizeArray<ImageProtectionPlan>()
                let rxCounters = ResizeArray<ImageRxCounter>()
                let coverageSpans = ResizeArray<ImageCoverageSpan>()

                let addProtectionPlan
                    (crc: LinkedCrc option)
                    (counter: (uint16 * uint16 * bool * uint32 * uint32) option)
                    isTx
                    =
                    let spanIndex =
                        if crc.IsSome then
                            uint16 coverageSpans.Count
                        else
                            UInt16.MaxValue

                    match crc with
                    | Some value ->
                        for span in value.CoverageSpans do
                            coverageSpans.Add(
                                { ByteOffset = span.ByteOffset
                                  ByteCount = span.ByteCount }
                            )
                    | None -> ()

                    let counterIndex =
                        match counter with
                        | None -> UInt16.MaxValue
                        | Some(startBit, length, bigEndian, modulus, increment) when isTx ->
                            sortedTx
                            |> List.take (txProtectionPlans.Count + 1)
                            |> List.sumBy (fun message -> if message.Counter.IsSome then 1 else 0)
                            |> fun count -> uint16 (count - 1)
                        | Some(startBit, length, bigEndian, modulus, increment) ->
                            let index = uint16 rxCounters.Count

                            rxCounters.Add(
                                { StartBit = startBit
                                  LengthBits = length
                                  BigEndian = bigEndian
                                  Modulus = modulus
                                  Increment = increment }
                            )

                            index

                    { HasCrc = crc.IsSome
                      HasCounter = counter.IsSome
                      Algorithm =
                        crc
                        |> Option.map (fun value ->
                            if value.Algorithm = LinkedCrcAlgorithm.Crc8SaeJ1850 then
                                1uy
                            else
                                2uy)
                        |> Option.defaultValue 0uy
                      CrcWidthBytes =
                        crc
                        |> Option.map (fun value -> uint8 (value.LengthBits / 8us))
                        |> Option.defaultValue 0uy
                      CrcBigEndian = crc |> Option.exists _.BigEndian
                      CrcStartBit = crc |> Option.map _.StartBit |> Option.defaultValue UInt16.MaxValue
                      SpanIndex = spanIndex
                      SpanCount =
                        crc
                        |> Option.map (fun value -> uint8 value.CoverageSpans.Length)
                        |> Option.defaultValue 0uy
                      DataId = crc |> Option.bind _.DataId
                      CounterIndex = counterIndex }

                if hasProtection then
                    schema.Messages
                    |> List.sortBy (fun message -> encodedCanId message.Id message.IsExtended)
                    |> List.iter (fun message ->
                        let crc = message.Protection |> Option.bind _.Crc

                        let counter =
                            message.Protection
                            |> Option.bind _.Counter
                            |> Option.map (fun value ->
                                value.StartBit, value.Length, value.ByteOrder = Big, value.Modulus, value.Increment)

                        rxProtectionPlans.Add(addProtectionPlan crc counter false))

                    sortedTx
                    |> List.iter (fun message ->
                        let counter =
                            message.Counter
                            |> Option.map (fun value ->
                                value.StartBit, value.Length, value.ByteOrder = Big, value.Modulus, value.Increment)

                        txProtectionPlans.Add(addProtectionPlan message.Crc counter true))

                Ok
                    { Messages = messages |> Seq.toList
                      Programs = programs |> Seq.toList
                      Conversions = conversions |> Seq.toList
                      PoolSlotCount = uint16 schema.PoolSlots.Length
                      SignalNames = signalNames
                      MessageNames = messageNames |> Seq.toList
                      TxMessages = txMessages |> Seq.toList
                      TxPrograms = txPrograms |> Seq.toList
                      TxCounters = txCounters |> Seq.toList
                      TxTemplates = templates.ToArray()
                      NestedMuxRecords = nestedMuxRecords |> Seq.toList
                      QualityEntries = qualityEntries
                      RxProtectionPlans = rxProtectionPlans |> Seq.toList
                      TxProtectionPlans = txProtectionPlans |> Seq.toList
                      RxCounters = rxCounters |> Seq.toList
                      CoverageSpans = coverageSpans |> Seq.toList }

    let private messageRangeErrors messages programCount allowEmpty =
        let mutable expectedIndex = 0

        [ for message: ImageMessage in messages do
              if
                  (not allowEmpty && message.ProgramCount = 0us)
                  || int message.ProgramIndex <> expectedIndex
              then
                  ImageTable

              let rangeEnd = int message.ProgramIndex + int message.ProgramCount

              if rangeEnd > programCount then
                  ImageTable

              expectedIndex <- rangeEnd

          if expectedIndex <> programCount then
              ImageTable ]

    let private programErrors slotCount conversionCount (programs: ImageProgram list) =
        [ for program in programs do
              if
                  program.LengthBits < 1us
                  || program.LengthBits > 64us
                  || uint32 program.StartBit + uint32 program.LengthBits > 512u
              then
                  ImageTable

              if program.OrderFlags > 3uy || program.Storage > 9uy then
                  ImageTable

              if int program.ConversionIndex >= conversionCount then
                  ImageTable

              if int program.SlotIndex >= slotCount then
                  ImageTable

              if isIntegerStorageValue program.Storage && program.ConversionIndex <> 0us then
                  ImageTable

              let unconditional = program.MuxSelectorSlot = UInt16.MaxValue
              let sentinelExpected = program.MuxExpected = UInt32.MaxValue

              if unconditional <> sentinelExpected then
                  ImageTable

              if not unconditional then
                  if
                      int program.MuxSelectorSlot >= slotCount
                      || program.MuxSelectorSlot = program.SlotIndex
                  then
                      ImageTable ]

    let private selectorErrors (programs: ImageProgram array) start count =
        [ if count > 0 && start >= 0 && start + count <= programs.Length then
              let messagePrograms = programs.[start .. start + count - 1]

              let selectorSlots =
                  messagePrograms
                  |> Array.choose (fun program ->
                      if program.MuxSelectorSlot = UInt16.MaxValue then
                          None
                      else
                          Some program.MuxSelectorSlot)
                  |> Array.distinct

              if selectorSlots.Length > 1 then
                  ImageTable

              if selectorSlots.Length = 1 then
                  let selectorSlot = selectorSlots.[0]

                  let selectorIndex =
                      messagePrograms
                      |> Array.tryFindIndex (fun program ->
                          program.SlotIndex = selectorSlot
                          && program.MuxSelectorSlot = UInt16.MaxValue
                          && program.MuxExpected = UInt32.MaxValue)

                  if selectorIndex <> Some 0 then
                      ImageTable ]

    let private rangesOverlap startA lengthA startB lengthB =
        uint32 startA < uint32 startB + uint32 lengthB
        && uint32 startB < uint32 startA + uint32 lengthA

    let private validateRuntimeImage (image: RuntimeImage) =
        let hasTx = not image.TxMessages.IsEmpty
        let slotCount = int image.PoolSlotCount
        let conversions = image.Conversions |> List.toArray
        let rxPrograms = image.Programs |> List.toArray
        let txPrograms = image.TxPrograms |> List.toArray
        let hasRxq = not image.NestedMuxRecords.IsEmpty || not image.QualityEntries.IsEmpty

        let protectionErrors =
            [ let hasProtection =
                  not image.RxProtectionPlans.IsEmpty || not image.TxProtectionPlans.IsEmpty

              if hasProtection then
                  if
                      image.RxProtectionPlans.Length <> image.Messages.Length
                      || image.TxProtectionPlans.Length <> image.TxMessages.Length
                  then
                      ImageTable
              elif not image.RxCounters.IsEmpty || not image.CoverageSpans.IsEmpty then
                  ImageTable

              if image.RxCounters.Length > MaxMessages then
                  ImageLimit "RX counter count exceeds 4096"

              if image.CoverageSpans.Length > MaxCoverageSpans then
                  ImageLimit "coverage span count exceeds 16384"

              let mutable expectedRxCounter = 0
              let mutable expectedSpan = 0
              let allPlans = image.RxProtectionPlans @ image.TxProtectionPlans

              for planIndex in 0 .. allPlans.Length - 1 do
                  let plan = allPlans.[planIndex]

                  if plan.HasCrc then
                      let expectedWidth =
                          if plan.Algorithm = 1uy then 1uy
                          elif plan.Algorithm = 2uy then 2uy
                          else 0uy

                      if
                          expectedWidth = 0uy
                          || plan.CrcWidthBytes <> expectedWidth
                          || plan.CrcStartBit = UInt16.MaxValue
                          || plan.CrcStartBit % 8us <> 0us
                          || plan.SpanCount < 1uy
                          || plan.SpanCount > 2uy
                          || int plan.SpanIndex <> expectedSpan
                      then
                          ImageTable

                      expectedSpan <- expectedSpan + int plan.SpanCount

                      if expectedSpan > image.CoverageSpans.Length then
                          ImageTable
                  elif
                      plan.Algorithm <> 0uy
                      || plan.CrcWidthBytes <> 0uy
                      || plan.CrcBigEndian
                      || plan.CrcStartBit <> UInt16.MaxValue
                      || plan.SpanIndex <> UInt16.MaxValue
                      || plan.SpanCount <> 0uy
                      || plan.DataId.IsSome
                  then
                      ImageTable

                  if plan.HasCounter then
                      if planIndex < image.RxProtectionPlans.Length then
                          if int plan.CounterIndex <> expectedRxCounter then
                              ImageTable

                          expectedRxCounter <- expectedRxCounter + 1
                      else
                          let txIndex = planIndex - image.RxProtectionPlans.Length

                          if
                              txIndex >= image.TxMessages.Length
                              || plan.CounterIndex <> image.TxMessages.[txIndex].CounterIndex
                          then
                              ImageTable
                  elif plan.CounterIndex <> UInt16.MaxValue then
                      ImageTable

              if
                  expectedRxCounter <> image.RxCounters.Length
                  || expectedSpan <> image.CoverageSpans.Length
              then
                  ImageTable

              for span in image.CoverageSpans do
                  if span.ByteCount = 0uy then
                      ImageTable

              for plan in allPlans do
                  if
                      plan.HasCrc
                      && int plan.SpanIndex + int plan.SpanCount <= image.CoverageSpans.Length
                  then
                      let spans =
                          image.CoverageSpans
                          |> List.skip (int plan.SpanIndex)
                          |> List.take (int plan.SpanCount)

                      let mutable previousEnd = -1
                      let crcByte = int plan.CrcStartBit / 8
                      let crcEnd = crcByte + int plan.CrcWidthBytes

                      for span in spans do
                          let spanEnd = int span.ByteOffset + int span.ByteCount

                          if
                              int span.ByteOffset < previousEnd
                              || spanEnd > 64
                              || (int span.ByteOffset < crcEnd && spanEnd > crcByte)
                          then
                              ImageTable

                          previousEnd <- spanEnd

              for counter in image.RxCounters do
                  if
                      counter.LengthBits < 1us
                      || counter.LengthBits > 32us
                      || counter.Increment = 0u
                      || counter.Modulus = 1u
                      || (counter.Modulus = 0u && counter.LengthBits <> 32us)
                      || (counter.Modulus <> 0u && counter.Increment >= counter.Modulus)
                      || (counter.LengthBits < 32us
                          && counter.Modulus <> 0u
                          && uint64 counter.Modulus > (1UL <<< int counter.LengthBits))
                  then
                      ImageTable ]

        let nestedErrors =
            [ if image.NestedMuxRecords.Length > MaxPrograms then
                  ImageLimit "nested mux record count exceeds 8192"

              if hasRxq then
                  if image.QualityEntries.Length <> slotCount then
                      ImageTable
              elif not image.QualityEntries.IsEmpty then
                  ImageTable

              for entry in image.QualityEntries do
                  if entry.FreshnessMs > uint32 Int32.MaxValue then
                      ImageTable

              let targets = image.NestedMuxRecords |> List.map _.TargetProgramIndex

              let recordsByTarget =
                  image.NestedMuxRecords
                  |> List.map (fun record -> record.TargetProgramIndex, record)
                  |> Map.ofList

              if
                  targets <> List.sort targets
                  || targets.Length <> (targets |> List.distinct).Length
              then
                  ImageTable

              let containingMessage index =
                  image.Messages
                  |> List.tryFind (fun message ->
                      index >= int message.ProgramIndex
                      && index < int message.ProgramIndex + int message.ProgramCount)

              for record in image.NestedMuxRecords do
                  if record.Predicates.Length < 2 || record.Predicates.Length > 4 then
                      ImageTable

                  if int record.TargetProgramIndex >= rxPrograms.Length then
                      ImageTable
                  else
                      let target = rxPrograms.[int record.TargetProgramIndex]
                      let first = record.Predicates.Head

                      if
                          target.MuxSelectorSlot <> first.SelectorSlot
                          || target.MuxExpected <> first.Expected
                      then
                          ImageTable

                      for predicateIndex in 0 .. record.Predicates.Length - 1 do
                          let predicate = record.Predicates.[predicateIndex]

                          if
                              int predicate.SelectorProgramIndex >= rxPrograms.Length
                              || int predicate.SelectorSlot >= slotCount
                              || predicate.SelectorProgramIndex = record.TargetProgramIndex
                          then
                              ImageTable
                          else
                              let selector = rxPrograms.[int predicate.SelectorProgramIndex]

                              if
                                  selector.SlotIndex <> predicate.SelectorSlot
                                  || selector.LengthBits > 32us
                                  || (selector.OrderFlags &&& 2uy) <> 0uy
                                  || selector.Storage > 7uy
                                  || selector.ConversionIndex <> 0us
                                  || containingMessage (int record.TargetProgramIndex)
                                     <> containingMessage (int predicate.SelectorProgramIndex)
                              then
                                  ImageTable

                              if predicateIndex = 0 then
                                  if
                                      selector.MuxSelectorSlot <> UInt16.MaxValue
                                      || selector.MuxExpected <> UInt32.MaxValue
                                  then
                                      ImageTable
                              elif predicateIndex = 1 then
                                  let outer = record.Predicates.[0]

                                  if
                                      selector.MuxSelectorSlot <> outer.SelectorSlot
                                      || selector.MuxExpected <> outer.Expected
                                  then
                                      ImageTable
                              else
                                  match recordsByTarget |> Map.tryFind predicate.SelectorProgramIndex with
                                  | None -> ImageTable
                                  | Some selectorRecord when
                                      selectorRecord.Predicates <> (record.Predicates |> List.take predicateIndex)
                                      ->
                                      ImageTable
                                  | Some _ -> () ]

        let conversionErrors =
            [ if conversions.Length = 0 || conversions.[0] <> identityConversion then
                  ImageTable

              for conversion in conversions do
                  if
                      not (Double.IsFinite(conversion.Factor))
                      || not (Double.IsFinite(conversion.Offset))
                      || (conversion.IsAffine && conversion.Factor = 0.0)
                      || (not conversion.IsAffine
                          && (conversion.Factor <> 1.0 || conversion.Offset <> 0.0))
                  then
                      ImageTable ]

        [ if image.Messages.Length > MaxMessages || image.TxMessages.Length > MaxMessages then
              ImageLimit "message_count exceeds 4096"

          if image.Programs.Length > MaxPrograms || image.TxPrograms.Length > MaxPrograms then
              ImageLimit "program_count exceeds 8192"

          if image.Conversions.Length > MaxConversions then
              ImageLimit "conversion_count exceeds 1024"

          if slotCount > MaxPrograms || (hasTx && slotCount = 0) then
              ImageTable

          if
              image.MessageNames.Length <> image.Messages.Length
              || image.SignalNames.Length <> slotCount
          then
              ImageTable

          if not hasTx then
              if
                  image.TxPrograms.Length <> 0
                  || image.TxCounters.Length <> 0
                  || image.TxTemplates.Length <> 0
                  || slotCount <> image.Programs.Length
              then
                  ImageTable

              let slots =
                  image.Programs |> List.map (fun program -> program.SlotIndex) |> List.sort

              let expected = [ for index in 0 .. image.Programs.Length - 1 -> uint16 index ]

              if slots <> expected then
                  ImageTable

          let rxIds = image.Messages |> List.map (fun message -> message.EncodedCanId)

          if rxIds <> List.sort rxIds || rxIds.Length <> (rxIds |> List.distinct).Length then
              ImageTable

          for id in rxIds do
              if not (validEncodedCanId id) then
                  ImageTable

          for name in image.SignalNames @ image.MessageNames do
              if nameError name then
                  ImageLimit "a symbol name is not 1..255 UTF-8 bytes"

          yield! conversionErrors
          yield! nestedErrors
          yield! protectionErrors
          yield! messageRangeErrors image.Messages image.Programs.Length false
          yield! programErrors slotCount conversions.Length image.Programs

          for message in image.Messages do
              let start = int message.ProgramIndex
              let count = int message.ProgramCount
              yield! selectorErrors rxPrograms start count

          if hasTx then
              let logicalIds =
                  image.TxMessages |> List.map (fun message -> message.LogicalMessageId)

              if
                  logicalIds <> List.sort logicalIds
                  || logicalIds.Length <> (logicalIds |> List.distinct).Length
              then
                  ImageTable

              let mutable expectedProgram = 0

              let mutable expectedTemplate =
                  TxHeaderSize
                  + image.TxMessages.Length * TxMessageSize
                  + image.TxPrograms.Length * ProgramSize
                  + image.TxCounters.Length * CounterSize

              let counterReferences = Array.zeroCreate image.TxCounters.Length

              for message in image.TxMessages do
                  if int message.ProgramIndex <> expectedProgram then
                      ImageTable

                  expectedProgram <- expectedProgram + int message.ProgramCount

                  if expectedProgram > image.TxPrograms.Length then
                      ImageTable

                  if int message.TemplateOffset <> expectedTemplate then
                      ImageTable

                  expectedTemplate <- expectedTemplate + int message.PayloadLength

                  if
                      not (validEncodedCanId message.EncodedCanId)
                      || not (validPayloadLength message.PayloadLength)
                  then
                      ImageTable

                  let extended = (message.EncodedCanId &&& 0x80000000u) <> 0u

                  let expectedFlags =
                      (if extended then 1uy else 0uy)
                      ||| (if message.PayloadLength > 8uy then 2uy else 0uy)

                  if message.FrameFlags <> expectedFlags then
                      ImageTable

                  if message.ProgramCount = 0us && message.CounterIndex = UInt16.MaxValue then
                      ImageTable

                  if message.CounterIndex <> UInt16.MaxValue then
                      if int message.CounterIndex >= image.TxCounters.Length then
                          ImageTable
                      else
                          counterReferences.[int message.CounterIndex] <-
                              counterReferences.[int message.CounterIndex] + 1

                  let first = int message.ProgramIndex
                  let count = int message.ProgramCount
                  yield! selectorErrors txPrograms first count

                  if first + count <= txPrograms.Length then
                      let messagePrograms =
                          if count = 0 then
                              [||]
                          else
                              txPrograms.[first .. first + count - 1]

                      for program in messagePrograms do
                          if
                              uint32 program.StartBit + uint32 program.LengthBits > uint32 message.PayloadLength * 8u
                          then
                              ImageTable

                      for leftIndex in 0 .. messagePrograms.Length - 1 do
                          for rightIndex in leftIndex + 1 .. messagePrograms.Length - 1 do
                              let left = messagePrograms.[leftIndex]
                              let right = messagePrograms.[rightIndex]

                              let branchOverlap =
                                  left.MuxSelectorSlot <> UInt16.MaxValue
                                  && left.MuxSelectorSlot = right.MuxSelectorSlot
                                  && left.MuxExpected <> right.MuxExpected

                              if
                                  rangesOverlap left.StartBit left.LengthBits right.StartBit right.LengthBits
                                  && not branchOverlap
                              then
                                  ImageTable

                      if
                          message.CounterIndex <> UInt16.MaxValue
                          && int message.CounterIndex < image.TxCounters.Length
                      then
                          let counter = image.TxCounters.[int message.CounterIndex]

                          if
                              uint32 counter.StartBit + uint32 counter.LengthBits > uint32 message.PayloadLength * 8u
                          then
                              ImageTable

                          for program in messagePrograms do
                              if
                                  rangesOverlap counter.StartBit counter.LengthBits program.StartBit program.LengthBits
                              then
                                  ImageTable

              if expectedProgram <> image.TxPrograms.Length then
                  ImageTable

              let templateBase =
                  TxHeaderSize
                  + image.TxMessages.Length * TxMessageSize
                  + image.TxPrograms.Length * ProgramSize
                  + image.TxCounters.Length * CounterSize

              if expectedTemplate <> templateBase + image.TxTemplates.Length then
                  ImageTable

              if counterReferences |> Array.exists (fun count -> count <> 1) then
                  ImageTable

              for counter in image.TxCounters do
                  if counter.LengthBits < 1us || counter.LengthBits > 32us || counter.Increment = 0u then
                      ImageTable

                  if counter.Modulus = 1u then
                      ImageTable

                  if counter.Modulus = 0u && counter.LengthBits <> 32us then
                      ImageTable

                  if counter.Modulus <> 0u then
                      if counter.Increment >= counter.Modulus || counter.InitialValue >= counter.Modulus then
                          ImageTable

                      if
                          counter.LengthBits < 32us
                          && uint64 counter.Modulus > (1UL <<< int counter.LengthBits)
                      then
                          ImageTable

              yield! programErrors slotCount conversions.Length image.TxPrograms ]

    let private putU16 (bytes: byte array) offset value =
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value)

    let private putU32 (bytes: byte array) offset value =
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value)

    let private putDouble (bytes: byte array) offset value =
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset, 8), BitConverter.DoubleToInt64Bits(value))

    let private getU16 (bytes: byte array) offset =
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))

    let private getU32 (bytes: byte array) offset =
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))

    let private getDouble (bytes: byte array) offset =
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8))
        |> BitConverter.Int64BitsToDouble

    let private allZero (bytes: byte array) start count =
        count <= 0 || bytes.AsSpan(start, count).IndexOfAnyExcept(0uy) < 0

    let private symbolSection (image: RuntimeImage) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        writer.Write(uint16 image.SignalNames.Length)
        writer.Write(uint16 image.MessageNames.Length)

        for name in image.SignalNames @ image.MessageNames do
            let bytes = utf8.GetBytes(name)
            writer.Write(uint16 bytes.Length)
            writer.Write(bytes)

        while stream.Length % 4L <> 0L do
            writer.Write(0uy)

        writer.Flush()
        stream.ToArray()

    let private crc32 (bytes: byte array) count =
        let mutable crc = UInt32.MaxValue

        for index in 0 .. count - 1 do
            crc <- crc ^^^ uint32 bytes.[index]

            for _ in 0..7 do
                crc <-
                    if (crc &&& 1u) <> 0u then
                        (crc >>> 1) ^^^ 0xEDB88320u
                    else
                        crc >>> 1

        crc ^^^ UInt32.MaxValue

    let private writeProgram bytes offset (program: ImageProgram) =
        putU16 bytes offset program.StartBit
        putU16 bytes (offset + 2) program.LengthBits
        bytes.[offset + 4] <- program.OrderFlags
        bytes.[offset + 5] <- program.Storage
        putU16 bytes (offset + 6) program.ConversionIndex
        putU16 bytes (offset + 8) program.SlotIndex
        putU16 bytes (offset + 10) program.MuxSelectorSlot
        putU32 bytes (offset + 12) program.MuxExpected

    let write (image: RuntimeImage) : Result<byte array, ValidationError list> =
        let errors = validateRuntimeImage image

        if not errors.IsEmpty then
            Error errors
        else
            let symbols = symbolSection image
            let msgOffset = HeaderSize + DirectorySize
            let msgSize = image.Messages.Length * 8
            let prgOffset = msgOffset + msgSize
            let prgSize = image.Programs.Length * ProgramSize
            let cnvOffset = prgOffset + prgSize
            let cnvSize = image.Conversions.Length * 24
            let symOffset = cnvOffset + cnvSize
            let symSize = symbols.Length
            let hasTx = not image.TxMessages.IsEmpty
            let hasRxq = not image.NestedMuxRecords.IsEmpty || not image.QualityEntries.IsEmpty

            let hasProtection =
                not image.RxProtectionPlans.IsEmpty || not image.TxProtectionPlans.IsEmpty

            let hasExtension = hasRxq || hasProtection
            let legacyEnd = symOffset + symSize

            let txUnalignedSize =
                if hasTx then
                    TxHeaderSize
                    + image.TxMessages.Length * TxMessageSize
                    + image.TxPrograms.Length * ProgramSize
                    + image.TxCounters.Length * CounterSize
                    + image.TxTemplates.Length
                else
                    0

            let txSize = if hasTx then align4 txUnalignedSize else 0
            let extensionOffset = if hasExtension then align4 legacyEnd else 0
            let nestedOffset = ExtensionHeaderSize

            let qualityOffset =
                nestedOffset + image.NestedMuxRecords.Length * NestedMuxRecordSize

            let profileOffset = qualityOffset + image.QualityEntries.Length * 4

            let profileSize =
                if hasProtection then
                    ProtectionHeaderSize
                    + image.RxProtectionPlans.Length * ProtectionPlanSize
                    + image.TxProtectionPlans.Length * ProtectionPlanSize
                    + image.RxCounters.Length * RxCounterSize
                    + image.CoverageSpans.Length * CoverageSpanSize
                else
                    0

            let embeddedTxOffset = profileOffset + profileSize
            let extensionSize = if hasExtension then embeddedTxOffset + txSize else 0

            let txOffset =
                if hasExtension && hasTx then
                    extensionOffset + embeddedTxOffset
                elif hasTx then
                    align4 legacyEnd
                else
                    0

            let contentEnd =
                if hasExtension then extensionOffset + extensionSize
                elif hasTx then txOffset + txSize
                else legacyEnd

            let totalSize = contentEnd + 4

            if totalSize > MaxImageSize then
                Error[ImageLimit "total_size exceeds 1 MiB"]
            else
                let bytes = Array.zeroCreate<byte> totalSize
                Array.Copy(magic, bytes, magic.Length)
                putU16 bytes 8 1us

                putU16
                    bytes
                    10
                    ((if hasTx then 1us else 0us)
                     ||| (if hasRxq then 2us else 0us)
                     ||| (if hasProtection then 4us else 0us))

                putU32 bytes 12 (uint32 totalSize)
                putU16 bytes 16 (uint16 image.Messages.Length)
                putU16 bytes 18 (uint16 image.Programs.Length)
                putU16 bytes 20 (uint16 image.Conversions.Length)

                putU16 bytes 22 (if hasTx || hasExtension then image.PoolSlotCount else 0us)

                putU32
                    bytes
                    24
                    (if hasExtension then uint32 extensionOffset
                     elif hasTx then uint32 txOffset
                     else 0u)

                putU32
                    bytes
                    28
                    (if hasExtension then uint32 extensionSize
                     elif hasTx then uint32 txSize
                     else 0u)

                [| msgOffset, msgSize
                   prgOffset, prgSize
                   cnvOffset, cnvSize
                   symOffset, symSize |]
                |> Array.iteri (fun index (offset, size) ->
                    putU32 bytes (HeaderSize + index * 8) (uint32 offset)
                    putU32 bytes (HeaderSize + index * 8 + 4) (uint32 size))

                image.Messages
                |> List.iteri (fun index message ->
                    let offset = msgOffset + index * 8
                    putU32 bytes offset message.EncodedCanId
                    putU16 bytes (offset + 4) message.ProgramCount
                    putU16 bytes (offset + 6) message.ProgramIndex)

                image.Programs
                |> List.iteri (fun index program -> writeProgram bytes (prgOffset + index * ProgramSize) program)

                image.Conversions
                |> List.iteri (fun index conversion ->
                    let offset = cnvOffset + index * 24
                    bytes.[offset] <- if conversion.IsAffine then 1uy else 0uy
                    putDouble bytes (offset + 8) conversion.Factor
                    putDouble bytes (offset + 16) conversion.Offset)

                Array.Copy(symbols, 0, bytes, symOffset, symbols.Length)

                if hasExtension then
                    putU32 bytes extensionOffset 0x31305845u

                    putU16
                        bytes
                        (extensionOffset + 4)
                        ((if hasRxq then 2us else 0us)
                         ||| (if image.NestedMuxRecords.IsEmpty then 0us else 1us)
                         ||| (if hasTx then 4us else 0us)
                         ||| (if hasProtection then 8us else 0us))

                    bytes.[extensionOffset + 6] <- 4uy
                    putU16 bytes (extensionOffset + 8) (uint16 image.NestedMuxRecords.Length)
                    putU16 bytes (extensionOffset + 10) (uint16 image.QualityEntries.Length)
                    putU32 bytes (extensionOffset + 12) (uint32 nestedOffset)
                    putU32 bytes (extensionOffset + 16) (uint32 qualityOffset)
                    putU32 bytes (extensionOffset + 20) (uint32 embeddedTxOffset)
                    putU32 bytes (extensionOffset + 24) (uint32 txSize)
                    putU32 bytes (extensionOffset + 28) (if hasProtection then uint32 profileOffset else 0u)
                    putU32 bytes (extensionOffset + 32) (uint32 profileSize)

                    image.NestedMuxRecords
                    |> List.iteri (fun index record ->
                        let offset = extensionOffset + nestedOffset + index * NestedMuxRecordSize
                        putU16 bytes offset record.TargetProgramIndex
                        bytes.[offset + 2] <- uint8 record.Predicates.Length

                        for predicateIndex in 0..3 do
                            let predicateOffset = offset + 4 + predicateIndex * 8

                            match record.Predicates |> List.tryItem predicateIndex with
                            | Some predicate ->
                                putU16 bytes predicateOffset predicate.SelectorProgramIndex
                                putU16 bytes (predicateOffset + 2) predicate.SelectorSlot
                                putU32 bytes (predicateOffset + 4) predicate.Expected
                            | None ->
                                putU16 bytes predicateOffset UInt16.MaxValue
                                putU16 bytes (predicateOffset + 2) UInt16.MaxValue
                                putU32 bytes (predicateOffset + 4) UInt32.MaxValue)

                    image.QualityEntries
                    |> List.iteri (fun index entry ->
                        putU32 bytes (extensionOffset + qualityOffset + index * 4) entry.FreshnessMs)

                    if hasProtection then
                        let profile = extensionOffset + profileOffset
                        let rxPlanOffset = ProtectionHeaderSize

                        let txPlanOffset =
                            rxPlanOffset + image.RxProtectionPlans.Length * ProtectionPlanSize

                        let rxCounterOffset =
                            txPlanOffset + image.TxProtectionPlans.Length * ProtectionPlanSize

                        let spanOffset = rxCounterOffset + image.RxCounters.Length * RxCounterSize
                        putU32 bytes profile 0x31305250u
                        putU16 bytes (profile + 4) (uint16 image.RxProtectionPlans.Length)
                        putU16 bytes (profile + 6) (uint16 image.TxProtectionPlans.Length)
                        putU16 bytes (profile + 8) (uint16 image.RxCounters.Length)
                        putU16 bytes (profile + 10) (uint16 image.CoverageSpans.Length)
                        putU32 bytes (profile + 12) (uint32 rxPlanOffset)
                        putU32 bytes (profile + 16) (uint32 txPlanOffset)
                        putU32 bytes (profile + 20) (uint32 rxCounterOffset)
                        putU32 bytes (profile + 24) (uint32 spanOffset)
                        putU32 bytes (profile + 28) (uint32 profileSize)

                        let writePlan offset (plan: ImageProtectionPlan) =
                            bytes.[offset] <-
                                (if plan.HasCrc then 1uy else 0uy) ||| (if plan.HasCounter then 2uy else 0uy)

                            bytes.[offset + 1] <- plan.Algorithm
                            bytes.[offset + 2] <- plan.CrcWidthBytes
                            bytes.[offset + 3] <- if plan.CrcBigEndian then 1uy else 0uy
                            putU16 bytes (offset + 4) plan.CrcStartBit
                            putU16 bytes (offset + 6) plan.SpanIndex
                            bytes.[offset + 8] <- plan.SpanCount
                            bytes.[offset + 9] <- if plan.DataId.IsSome then 2uy else 0uy
                            putU16 bytes (offset + 10) plan.CounterIndex
                            putU16 bytes (offset + 12) (plan.DataId |> Option.defaultValue 0us)

                        image.RxProtectionPlans
                        |> List.iteri (fun index plan ->
                            writePlan (profile + rxPlanOffset + index * ProtectionPlanSize) plan)

                        image.TxProtectionPlans
                        |> List.iteri (fun index plan ->
                            writePlan (profile + txPlanOffset + index * ProtectionPlanSize) plan)

                        image.RxCounters
                        |> List.iteri (fun index counter ->
                            let offset = profile + rxCounterOffset + index * RxCounterSize
                            putU16 bytes offset counter.StartBit
                            putU16 bytes (offset + 2) counter.LengthBits
                            bytes.[offset + 4] <- if counter.BigEndian then 1uy else 0uy
                            putU32 bytes (offset + 8) counter.Modulus
                            putU32 bytes (offset + 12) counter.Increment)

                        image.CoverageSpans
                        |> List.iteri (fun index span ->
                            let offset = profile + spanOffset + index * CoverageSpanSize
                            bytes.[offset] <- span.ByteOffset
                            bytes.[offset + 1] <- span.ByteCount)

                if hasTx then
                    putU32 bytes txOffset 0x31305854u
                    putU16 bytes (txOffset + 4) (uint16 image.TxMessages.Length)
                    putU16 bytes (txOffset + 6) (uint16 image.TxPrograms.Length)
                    putU16 bytes (txOffset + 8) (uint16 image.TxCounters.Length)
                    let txMessageOffset = TxHeaderSize

                    let txProgramOffset = txMessageOffset + image.TxMessages.Length * TxMessageSize

                    let txCounterOffset = txProgramOffset + image.TxPrograms.Length * ProgramSize

                    let txTemplateOffset = txCounterOffset + image.TxCounters.Length * CounterSize

                    putU32 bytes (txOffset + 12) (uint32 txMessageOffset)
                    putU32 bytes (txOffset + 16) (uint32 txProgramOffset)
                    putU32 bytes (txOffset + 20) (uint32 txCounterOffset)
                    putU32 bytes (txOffset + 24) (uint32 txTemplateOffset)
                    putU32 bytes (txOffset + 28) (uint32 image.TxTemplates.Length)

                    image.TxMessages
                    |> List.iteri (fun index message ->
                        let offset = txOffset + txMessageOffset + index * TxMessageSize
                        putU32 bytes offset message.LogicalMessageId
                        putU32 bytes (offset + 4) message.EncodedCanId
                        bytes.[offset + 8] <- message.PayloadLength
                        bytes.[offset + 9] <- message.FrameFlags
                        putU16 bytes (offset + 10) message.ProgramCount
                        putU16 bytes (offset + 12) message.ProgramIndex
                        putU16 bytes (offset + 14) message.CounterIndex
                        putU32 bytes (offset + 16) message.TemplateOffset)

                    image.TxPrograms
                    |> List.iteri (fun index program ->
                        writeProgram bytes (txOffset + txProgramOffset + index * ProgramSize) program)

                    image.TxCounters
                    |> List.iteri (fun index counter ->
                        let offset = txOffset + txCounterOffset + index * CounterSize
                        putU16 bytes offset counter.StartBit
                        putU16 bytes (offset + 2) counter.LengthBits
                        bytes.[offset + 4] <- if counter.BigEndian then 1uy else 0uy
                        putU32 bytes (offset + 8) counter.Modulus
                        putU32 bytes (offset + 12) counter.Increment
                        putU32 bytes (offset + 16) counter.InitialValue)

                    Array.Copy(image.TxTemplates, 0, bytes, txOffset + txTemplateOffset, image.TxTemplates.Length)

                putU32 bytes (totalSize - 4) (crc32 bytes (totalSize - 4))
                Ok bytes

    let private parseSymbols bytes offset size signalCount messageCount =
        if
            size < 4
            || getU16 bytes offset <> uint16 signalCount
            || getU16 bytes (offset + 2) <> uint16 messageCount
        then
            Error[ImageTable]
        else
            let sectionEnd = offset + size
            let mutable cursor = offset + 4
            let names = ResizeArray<string>()
            let mutable failed = false

            for _ in 1 .. signalCount + messageCount do
                if cursor + 2 > sectionEnd then
                    failed <- true
                elif not failed then
                    let length = int (getU16 bytes cursor)
                    cursor <- cursor + 2

                    if length < 1 || length > 255 || cursor + length > sectionEnd then
                        failed <- true
                    else
                        try
                            let name = utf8.GetString(bytes, cursor, length)

                            if name.IndexOf('\000') >= 0 then
                                failed <- true
                            else
                                names.Add(name)
                        with :? DecoderFallbackException ->
                            failed <- true

                        cursor <- cursor + length

            let padding = sectionEnd - cursor

            if failed || padding < 0 || padding > 3 || not (allZero bytes cursor padding) then
                Error[ImageTable]
            else
                let values = names |> Seq.toList
                Ok(values |> List.take signalCount, values |> List.skip signalCount)

    let private parseMessages bytes offset count =
        [ for index in 0 .. count - 1 do
              let entry = offset + index * 8

              yield
                  { EncodedCanId = getU32 bytes entry
                    ProgramCount = getU16 bytes (entry + 4)
                    ProgramIndex = getU16 bytes (entry + 6) } ]

    let private parsePrograms bytes offset count =
        [ for index in 0 .. count - 1 do
              let entry = offset + index * ProgramSize

              yield
                  { StartBit = getU16 bytes entry
                    LengthBits = getU16 bytes (entry + 2)
                    OrderFlags = bytes.[entry + 4]
                    Storage = bytes.[entry + 5]
                    ConversionIndex = getU16 bytes (entry + 6)
                    SlotIndex = getU16 bytes (entry + 8)
                    MuxSelectorSlot = getU16 bytes (entry + 10)
                    MuxExpected = getU32 bytes (entry + 12) } ]

    let private parseConversions (bytes: byte array) offset count =
        let values = ResizeArray<ImageConversion>()
        let mutable failed = false

        for index in 0 .. count - 1 do
            let entry = offset + index * 24
            let kind = bytes.[entry]

            if kind > 1uy || not (allZero bytes (entry + 1) 7) then
                failed <- true

            values.Add(
                { IsAffine = kind = 1uy
                  Factor = getDouble bytes (entry + 8)
                  Offset = getDouble bytes (entry + 16) }
            )

        if failed then
            Error[ImageTable]
        else
            Ok(values |> Seq.toList)

    let private readDirectory bytes totalSize messageCount programCount conversionCount sectionEnd =
        let sections =
            Array.init 4 (fun index ->
                let entry = HeaderSize + index * 8
                int (getU32 bytes entry), int (getU32 bytes (entry + 4)))

        if sections |> Array.exists (fun (offset, _) -> offset % 4 <> 0) then
            Error[ImageAlign]
        elif
            sections
            |> Array.exists (fun (offset, size) ->
                offset < HeaderSize + DirectorySize
                || offset > sectionEnd
                || size < 0
                || size > sectionEnd - offset)
        then
            Error[ImageBounds]
        elif
            snd sections.[0] <> messageCount * 8
            || snd sections.[1] <> programCount * ProgramSize
            || snd sections.[2] <> conversionCount * 24
            || snd sections.[3] % 4 <> 0
        then
            Error[ImageTable]
        else
            let mutable previousEnd = HeaderSize + DirectorySize
            let mutable error = None

            for offset, size in sections do
                if error.IsNone then
                    if offset < previousEnd then
                        error <- Some ImageBounds
                    elif not (allZero bytes previousEnd (offset - previousEnd)) then
                        error <- Some ImageTable
                    else
                        previousEnd <- offset + size

            match error with
            | Some value -> Error[value]
            | None when previousEnd > sectionEnd -> Error[ImageBounds]
            | None when not (allZero bytes previousEnd (sectionEnd - previousEnd)) -> Error[ImageTable]
            | None -> Ok sections

    let private parseTx bytes txOffset txSize =
        if txSize < TxHeaderSize || getU32 bytes txOffset <> 0x31305854u then
            Error[ImageTable]
        elif not (allZero bytes (txOffset + 10) 2) then
            Error[ImageTable]
        else
            let messageCount = int (getU16 bytes (txOffset + 4))
            let programCount = int (getU16 bytes (txOffset + 6))
            let counterCount = int (getU16 bytes (txOffset + 8))
            let messageOffset = int (getU32 bytes (txOffset + 12))
            let programOffset = int (getU32 bytes (txOffset + 16))
            let counterOffset = int (getU32 bytes (txOffset + 20))
            let templateOffset = int (getU32 bytes (txOffset + 24))
            let templateSize = int (getU32 bytes (txOffset + 28))
            let expectedMessageOffset = TxHeaderSize

            let expectedProgramOffset = expectedMessageOffset + messageCount * TxMessageSize

            let expectedCounterOffset = expectedProgramOffset + programCount * ProgramSize
            let expectedTemplateOffset = expectedCounterOffset + counterCount * CounterSize
            let templateEnd = expectedTemplateOffset + templateSize

            if
                messageCount < 1
                || messageCount > MaxMessages
                || programCount > MaxPrograms
                || counterCount > MaxMessages
            then
                Error[ImageLimit "TX count exceeds v1 limits"]
            elif
                messageOffset <> expectedMessageOffset
                || programOffset <> expectedProgramOffset
                || counterOffset <> expectedCounterOffset
                || templateOffset <> expectedTemplateOffset
            then
                Error[ImageTable]
            elif templateEnd > txSize || txSize - templateEnd > 3 then
                Error[ImageBounds]
            elif not (allZero bytes (txOffset + templateEnd) (txSize - templateEnd)) then
                Error[ImageTable]
            else
                let messages =
                    [ for index in 0 .. messageCount - 1 do
                          let entry = txOffset + messageOffset + index * TxMessageSize

                          if not (allZero bytes (entry + 20) 4) then
                              ()

                          yield
                              { LogicalMessageId = getU32 bytes entry
                                EncodedCanId = getU32 bytes (entry + 4)
                                PayloadLength = bytes.[entry + 8]
                                FrameFlags = bytes.[entry + 9]
                                ProgramCount = getU16 bytes (entry + 10)
                                ProgramIndex = getU16 bytes (entry + 12)
                                CounterIndex = getU16 bytes (entry + 14)
                                TemplateOffset = getU32 bytes (entry + 16) } ]

                let reservedBad =
                    [ 0 .. messageCount - 1 ]
                    |> List.exists (fun index ->
                        let entry = txOffset + messageOffset + index * TxMessageSize
                        not (allZero bytes (entry + 20) 4))

                let programs = parsePrograms bytes (txOffset + programOffset) programCount

                let counters =
                    [ for index in 0 .. counterCount - 1 do
                          let entry = txOffset + counterOffset + index * CounterSize

                          yield
                              { StartBit = getU16 bytes entry
                                LengthBits = getU16 bytes (entry + 2)
                                BigEndian = bytes.[entry + 4] = 1uy
                                Modulus = getU32 bytes (entry + 8)
                                Increment = getU32 bytes (entry + 12)
                                InitialValue = getU32 bytes (entry + 16) } ]

                let counterReservedBad =
                    [ 0 .. counterCount - 1 ]
                    |> List.exists (fun index ->
                        let entry = txOffset + counterOffset + index * CounterSize

                        bytes.[entry + 4] > 1uy
                        || not (allZero bytes (entry + 5) 3)
                        || not (allZero bytes (entry + 20) 4))

                if reservedBad || counterReservedBad then
                    Error[ImageTable]
                else
                    Ok(messages, programs, counters, bytes.[txOffset + templateOffset .. txOffset + templateEnd - 1])

    let private parseProtection (bytes: byte array) offset size (messageCount: int) (txMessages: ImageTxMessage list) =
        if
            size < ProtectionHeaderSize
            || getU32 bytes offset <> 0x31305250u
            || not (allZero bytes (offset + 32) 16)
        then
            Error[ImageTable]
        else
            let rxCount = int (getU16 bytes (offset + 4))
            let txCount = int (getU16 bytes (offset + 6))
            let counterCount = int (getU16 bytes (offset + 8))
            let spanCount = int (getU16 bytes (offset + 10))
            let rxOffset = int (getU32 bytes (offset + 12))
            let txOffset = int (getU32 bytes (offset + 16))
            let counterOffset = int (getU32 bytes (offset + 20))
            let spanOffset = int (getU32 bytes (offset + 24))
            let endOffset = int (getU32 bytes (offset + 28))
            let expectedTx = ProtectionHeaderSize + rxCount * ProtectionPlanSize
            let expectedCounter = expectedTx + txCount * ProtectionPlanSize
            let expectedSpan = expectedCounter + counterCount * RxCounterSize
            let expectedEnd = expectedSpan + spanCount * CoverageSpanSize

            if
                rxCount <> messageCount
                || txCount <> txMessages.Length
                || counterCount > MaxMessages
                || spanCount > MaxCoverageSpans
                || rxOffset <> ProtectionHeaderSize
                || txOffset <> expectedTx
                || counterOffset <> expectedCounter
                || spanOffset <> expectedSpan
                || endOffset <> expectedEnd
                || endOffset <> size
            then
                Error[ImageTable]
            else
                let mutable failed = false

                let parsePlan entry =
                    let flags = bytes.[entry]
                    let dataIdCount = bytes.[entry + 9]

                    if
                        flags > 3uy
                        || bytes.[entry + 3] > 1uy
                        || (dataIdCount <> 0uy && dataIdCount <> 2uy)
                        || (dataIdCount = 0uy && getU16 bytes (entry + 12) <> 0us)
                        || getU16 bytes (entry + 14) <> 0us
                    then
                        failed <- true

                    { HasCrc = (flags &&& 1uy) <> 0uy
                      HasCounter = (flags &&& 2uy) <> 0uy
                      Algorithm = bytes.[entry + 1]
                      CrcWidthBytes = bytes.[entry + 2]
                      CrcBigEndian = bytes.[entry + 3] = 1uy
                      CrcStartBit = getU16 bytes (entry + 4)
                      SpanIndex = getU16 bytes (entry + 6)
                      SpanCount = bytes.[entry + 8]
                      DataId =
                        if dataIdCount = 2uy then
                            Some(getU16 bytes (entry + 12))
                        else
                            None
                      CounterIndex = getU16 bytes (entry + 10) }

                let rxPlans =
                    [ for index in 0 .. rxCount - 1 -> parsePlan (offset + rxOffset + index * ProtectionPlanSize) ]

                let txPlans =
                    [ for index in 0 .. txCount - 1 -> parsePlan (offset + txOffset + index * ProtectionPlanSize) ]

                let counters =
                    [ for index in 0 .. counterCount - 1 do
                          let entry = offset + counterOffset + index * RxCounterSize

                          if bytes.[entry + 4] > 1uy || not (allZero bytes (entry + 5) 3) then
                              failed <- true

                          yield
                              { StartBit = getU16 bytes entry
                                LengthBits = getU16 bytes (entry + 2)
                                BigEndian = bytes.[entry + 4] = 1uy
                                Modulus = getU32 bytes (entry + 8)
                                Increment = getU32 bytes (entry + 12) } ]

                let spans =
                    [ for index in 0 .. spanCount - 1 do
                          let entry = offset + spanOffset + index * CoverageSpanSize

                          if not (allZero bytes (entry + 2) 2) then
                              failed <- true

                          yield
                              { ByteOffset = bytes.[entry]
                                ByteCount = bytes.[entry + 1] } ]

                if failed then
                    Error[ImageTable]
                else
                    Ok(rxPlans, txPlans, counters, spans)

    let private parseExtension (bytes: byte array) offset size poolSlotCount programCount hasTx hasRxq hasProtection =
        if size < ExtensionHeaderSize || getU32 bytes offset <> 0x31305845u then
            Error[ImageTable]
        elif
            bytes.[offset + 6] <> 4uy
            || bytes.[offset + 7] <> 0uy
            || not (allZero bytes (offset + 36) 4)
        then
            Error[ImageTable]
        else
            let flags = getU16 bytes (offset + 4)
            let nestedCount = int (getU16 bytes (offset + 8))
            let qualityCount = int (getU16 bytes (offset + 10))
            let nestedOffset = int (getU32 bytes (offset + 12))
            let qualityOffset = int (getU32 bytes (offset + 16))
            let txOffset = int (getU32 bytes (offset + 20))
            let txSize = int (getU32 bytes (offset + 24))
            let profileOffset = int (getU32 bytes (offset + 28))
            let profileSize = int (getU32 bytes (offset + 32))

            let expectedFlags =
                (if hasRxq then 2us else 0us)
                ||| (if nestedCount > 0 then 1us else 0us)
                ||| (if hasTx then 4us else 0us)
                ||| (if hasProtection then 8us else 0us)

            let expectedQuality = ExtensionHeaderSize + nestedCount * NestedMuxRecordSize
            let expectedProfile = expectedQuality + qualityCount * 4
            let expectedTx = expectedProfile + profileSize

            if
                (flags &&& ~~~15us) <> 0us
                || flags <> expectedFlags
                || nestedCount > MaxPrograms
                || qualityCount <> (if hasRxq then poolSlotCount else 0)
                || nestedOffset <> ExtensionHeaderSize
                || qualityOffset <> expectedQuality
                || profileOffset <> (if hasProtection then expectedProfile else 0)
                || profileSize <> (if hasProtection then txOffset - expectedProfile else 0)
                || txOffset <> expectedTx
                || txOffset > size
                || txSize <> size - txOffset
                || (hasTx && txSize = 0)
                || (not hasTx && txSize <> 0)
            then
                Error[ImageTable]
            else
                let mutable failed = false

                let nested =
                    [ for index in 0 .. nestedCount - 1 do
                          let entry = offset + nestedOffset + index * NestedMuxRecordSize
                          let target = getU16 bytes entry
                          let depth = int bytes.[entry + 2]

                          if depth < 2 || depth > 4 || bytes.[entry + 3] <> 0uy || int target >= programCount then
                              failed <- true

                          let predicates =
                              [ for predicateIndex in 0..3 do
                                    let predicateEntry = entry + 4 + predicateIndex * 8
                                    let program = getU16 bytes predicateEntry
                                    let slot = getU16 bytes (predicateEntry + 2)
                                    let expected = getU32 bytes (predicateEntry + 4)

                                    if predicateIndex < depth then
                                        if int program >= programCount || int slot >= poolSlotCount then
                                            failed <- true

                                        yield
                                            { SelectorProgramIndex = program
                                              SelectorSlot = slot
                                              Expected = expected }
                                    elif
                                        program <> UInt16.MaxValue
                                        || slot <> UInt16.MaxValue
                                        || expected <> UInt32.MaxValue
                                    then
                                        failed <- true ]

                          yield
                              { TargetProgramIndex = target
                                Predicates = predicates } ]

                let quality =
                    [ for index in 0 .. qualityCount - 1 do
                          let freshness = getU32 bytes (offset + qualityOffset + index * 4)

                          if freshness > uint32 Int32.MaxValue then
                              failed <- true

                          yield { FreshnessMs = freshness } ]

                if failed then
                    Error[ImageTable]
                else
                    Ok(nested, quality, offset + profileOffset, profileSize, offset + txOffset, txSize)

    let read (bytes: byte array) : Result<RuntimeImage, ValidationError list> =
        if isNull bytes || bytes.Length < HeaderSize + DirectorySize + 4 then
            Error[ImageSize]
        elif not (bytes.AsSpan(0, magic.Length).SequenceEqual(magic)) then
            Error[ImageBadMagic]
        elif getU16 bytes 8 <> 1us then
            Error[ImageBadVersion]
        else
            let totalSize = int (getU32 bytes 12)
            let flags = getU16 bytes 10

            if totalSize <> bytes.Length then
                Error[ImageSize]
            elif totalSize > MaxImageSize then
                Error[ImageLimit "total_size exceeds 1 MiB"]
            elif (flags &&& ~~~7us) <> 0us then
                Error[ImageFeature]
            else
                let hasTx = (flags &&& 1us) <> 0us
                let hasRxq = (flags &&& 2us) <> 0us
                let hasProtection = (flags &&& 4us) <> 0us
                let hasExtension = hasRxq || hasProtection
                let messageCount = int (getU16 bytes 16)
                let programCount = int (getU16 bytes 18)
                let conversionCount = int (getU16 bytes 20)

                let poolSlotCount =
                    if hasTx || hasExtension then
                        int (getU16 bytes 22)
                    else
                        programCount

                let containerOffset =
                    if hasTx || hasExtension then
                        int (getU32 bytes 24)
                    else
                        totalSize - 4

                let containerSize = if hasTx || hasExtension then int (getU32 bytes 28) else 0
                let crcOffset = totalSize - 4

                if
                    not hasTx
                    && not hasExtension
                    && (getU16 bytes 22 <> 0us || not (allZero bytes 24 8))
                then
                    Error[ImageTable]
                elif (hasTx || hasExtension) && poolSlotCount = 0 then
                    Error[ImageTable]
                elif
                    messageCount > MaxMessages
                    || programCount > MaxPrograms
                    || poolSlotCount > MaxPrograms
                    || conversionCount > MaxConversions
                then
                    Error[ImageLimit "count exceeds v1 limits"]
                elif conversionCount = 0 then
                    Error[ImageTable]
                elif (hasTx || hasExtension) && containerOffset % 4 <> 0 then
                    Error[ImageAlign]
                elif
                    (hasTx || hasExtension)
                    && (containerOffset < HeaderSize + DirectorySize
                        || containerOffset > crcOffset
                        || containerSize > crcOffset - containerOffset)
                then
                    Error[ImageBounds]
                elif hasExtension && containerOffset + containerSize <> crcOffset then
                    Error[ImageBounds]
                elif not hasExtension && hasTx && crcOffset - (containerOffset + containerSize) > 3 then
                    Error[ImageBounds]
                elif
                    not hasExtension
                    && hasTx
                    && not (
                        allZero bytes (containerOffset + containerSize) (crcOffset - containerOffset - containerSize)
                    )
                then
                    Error[ImageTable]
                else
                    match readDirectory bytes totalSize messageCount programCount conversionCount containerOffset with
                    | Error errors -> Error errors
                    | Ok sections ->
                        if crc32 bytes crcOffset <> getU32 bytes crcOffset then
                            Error[ImageCrc]
                        else
                            let msgOffset, _ = sections.[0]
                            let prgOffset, _ = sections.[1]
                            let cnvOffset, _ = sections.[2]
                            let symOffset, symSize = sections.[3]
                            let messages = parseMessages bytes msgOffset messageCount
                            let programs = parsePrograms bytes prgOffset programCount

                            match parseConversions bytes cnvOffset conversionCount with
                            | Error errors -> Error errors
                            | Ok conversions ->
                                match parseSymbols bytes symOffset symSize poolSlotCount messageCount with
                                | Error errors -> Error errors
                                | Ok(signalNames, messageNames) ->
                                    let extensionResult =
                                        if hasExtension then
                                            parseExtension
                                                bytes
                                                containerOffset
                                                containerSize
                                                poolSlotCount
                                                programCount
                                                hasTx
                                                hasRxq
                                                hasProtection
                                        else
                                            Ok([], [], containerOffset, 0, containerOffset, containerSize)

                                    match extensionResult with
                                    | Error errors -> Error errors
                                    | Ok(nestedMuxRecords, qualityEntries, profileOffset, profileSize, txOffset, txSize) ->
                                        let txResult =
                                            if hasTx then
                                                parseTx bytes txOffset txSize
                                            else
                                                Ok([], [], [], [||])

                                        match txResult with
                                        | Error errors -> Error errors
                                        | Ok(txMessages, txPrograms, txCounters, txTemplates) ->
                                            let protectionResult =
                                                if hasProtection then
                                                    parseProtection
                                                        bytes
                                                        profileOffset
                                                        profileSize
                                                        messageCount
                                                        txMessages
                                                else
                                                    Ok([], [], [], [])

                                            match protectionResult with
                                            | Error errors -> Error errors
                                            | Ok(rxProtectionPlans, txProtectionPlans, rxCounters, coverageSpans) ->
                                                let image =
                                                    { Messages = messages
                                                      Programs = programs
                                                      Conversions = conversions
                                                      PoolSlotCount = uint16 poolSlotCount
                                                      SignalNames = signalNames
                                                      MessageNames = messageNames
                                                      TxMessages = txMessages
                                                      TxPrograms = txPrograms
                                                      TxCounters = txCounters
                                                      TxTemplates = txTemplates
                                                      NestedMuxRecords = nestedMuxRecords
                                                      QualityEntries = qualityEntries
                                                      RxProtectionPlans = rxProtectionPlans
                                                      TxProtectionPlans = txProtectionPlans
                                                      RxCounters = rxCounters
                                                      CoverageSpans = coverageSpans }

                                                let errors = validateRuntimeImage image

                                                if errors.IsEmpty then Ok image else Error errors

    let inspect (bytes: byte array) : Result<string, ValidationError list> =
        match read bytes with
        | Error errors -> Error errors
        | Ok image ->
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
            writer.WriteStartObject()
            writer.WriteNumber("formatVersion", 1)
            writer.WriteNumber("totalSize", bytes.Length)
            writer.WriteString("crc32Hex", sprintf "0x%08X" (getU32 bytes (bytes.Length - 4)))
            writer.WriteBoolean("crcValid", true)
            writer.WriteNumber("messageCount", image.Messages.Length)
            writer.WriteNumber("signalCount", image.Programs.Length)
            writer.WriteNumber("poolSlotCount", int image.PoolSlotCount)
            writer.WriteNumber("conversionCount", image.Conversions.Length)
            writer.WriteNumber("txMessageCount", image.TxMessages.Length)
            writer.WriteNumber("txProgramCount", image.TxPrograms.Length)
            writer.WriteNumber("txCounterCount", image.TxCounters.Length)
            writer.WriteNumber("nestedMuxRecordCount", image.NestedMuxRecords.Length)
            writer.WriteNumber("qualityEntryCount", image.QualityEntries.Length)
            writer.WritePropertyName("messages")
            writer.WriteStartArray()

            (image.Messages, image.MessageNames)
            ||> List.iter2 (fun message name ->
                writer.WriteStartObject()
                writer.WriteString("name", name)
                writer.WriteString("encodedCanIdHex", sprintf "0x%08X" message.EncodedCanId)
                writer.WriteNumber("programCount", int message.ProgramCount)
                writer.WriteNumber("programIndex", int message.ProgramIndex)
                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.Flush()

            Ok(Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n")
