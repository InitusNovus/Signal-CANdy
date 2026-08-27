namespace Signal.CANdy.Core

open System
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Linked
open Signal.CANdy.Core.Pool
open Signal.CANdy.Core.PoolAbi
open Signal.CANdy.Core.RuntimeCapabilities
open Signal.CANdy.Core.Scimg

module RuntimeRequirements =

    type RuntimeRequirements =
        { RuntimeImageMajor: uint16
          RuntimeImageMinor: uint16
          Features: Set<RuntimeFeature>
          PoolAbiHash: PoolAbiHash
          ImageBytes: uint32
          RuntimeStateBytes: uint32
          RuntimeScratchBytes: uint32
          RxMessages: uint32
          RxPrograms: uint32
          TxMessages: uint32
          TxPrograms: uint32
          PoolSlots: uint32
          Conversions: uint32
          NestedMuxRecords: uint32
          MuxDepth: uint32
          QualityEntries: uint32
          ProtectionPlans: uint32
          TxCounters: uint32
          RxCounters: uint32
          CoverageSpans: uint32
          TxTemplateBytes: uint32
          PayloadBytes: uint32 }

    type RequirementsError = RequirementsError of string

    type private ResultBuilder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return value = Ok value
        member _.ReturnFrom value = value

    let private result = ResultBuilder()

    let runtimeStateBytes abi poolSlots qualityEntries txCounters rxCounters =
        match abi with
        | Ilp32 ->
            let total =
                if txCounters = 0u && qualityEntries = 0u && rxCounters = 0u then
                    0UL
                else
                    8UL
                    + 12UL * uint64 txCounters
                    + (if qualityEntries > 0u then
                           8UL + 8UL * uint64 poolSlots
                       else
                           0UL)
                    + 8UL * uint64 rxCounters

            if total > uint64 UInt32.MaxValue then
                Error[RequirementsError "Runtime state byte count exceeds uint32."]
            else
                Ok(uint32 total)

    let runtimeScratchBytes (image: RuntimeImage) =
        image.TxMessages
        |> List.map (fun message -> uint32 message.PayloadLength)
        |> List.fold max 0u

    let private hasMux (linked: LinkedSchema) =
        let rx =
            linked.Messages
            |> List.collect _.Plans
            |> List.exists (fun p -> p.IsMuxSelector || not p.MuxPath.IsEmpty)

        let tx =
            linked.TxMessages
            |> List.collect _.Plans
            |> List.exists (fun p -> p.IsMuxSelector || not p.MuxPath.IsEmpty)

        rx || tx

    let private maxDepth (linked: LinkedSchema) =
        [ yield! linked.Messages |> List.collect _.Plans |> List.map (fun p -> p.MuxPath.Length)
          yield!
              linked.TxMessages
              |> List.collect _.Plans
              |> List.map (fun p -> p.MuxPath.Length) ]
        |> List.fold max 0
        |> uint32

    let derive (pool: PoolContract) (linked: LinkedSchema) (image: RuntimeImage) (bytes: byte array) =
        result {
            let! hash =
                PoolAbi.compute pool
                |> Result.mapError (List.map (sprintf "%A" >> RequirementsError))

            let! state =
                runtimeStateBytes
                    Ilp32
                    (uint32 image.PoolSlotCount)
                    (uint32 image.QualityEntries.Length)
                    (uint32 image.TxCounters.Length)
                    (uint32 image.RxCounters.Length)

            let rxPlans = linked.Messages |> List.collect _.Plans
            let txPlans = linked.TxMessages |> List.collect _.Plans

            let crcs =
                [ yield! linked.Messages |> List.choose (fun m -> m.Protection |> Option.bind _.Crc)
                  yield! linked.TxMessages |> List.choose _.Crc ]

            let mutable features = Set.empty

            let add condition feature =
                if condition then
                    features <- Set.add feature features

            add (not linked.Messages.IsEmpty) Rx
            add (not linked.TxMessages.IsEmpty) Tx
            add (hasMux linked) Multiplexing

            add
                ((rxPlans |> List.exists (fun p -> p.MuxPath.Length >= 2))
                 || (txPlans |> List.exists (fun p -> p.MuxPath.Length >= 2)))
                NestedMux

            add (not image.QualityEntries.IsEmpty) RxQuality

            add
                ((linked.Messages |> List.exists (fun m -> m.Length > 8us))
                 || (linked.TxMessages |> List.exists (fun m -> m.Length > 8us)))
                CanFd

            add
                ((linked.Messages |> List.exists _.IsExtended)
                 || (linked.TxMessages |> List.exists _.IsExtended))
                ExtendedCan

            add
                ((rxPlans |> List.exists (fun p -> p.ByteOrder = Big))
                 || (txPlans |> List.exists (fun p -> p.ByteOrder = Big))
                 || (linked.Messages
                     |> List.exists (fun m ->
                         m.Protection
                         |> Option.bind _.Counter
                         |> Option.exists (fun c -> c.ByteOrder = Big)))
                 || (linked.TxMessages
                     |> List.exists (fun m -> m.Counter |> Option.exists (fun c -> c.ByteOrder = Big)))
                 || (crcs |> List.exists _.BigEndian))
                Motorola

            add (image.Conversions |> List.exists _.IsAffine) Affine
            add (crcs |> List.exists (fun c -> c.Algorithm = LinkedCrcAlgorithm.Crc8SaeJ1850)) Crc8SaeJ1850
            add (crcs |> List.exists (fun c -> c.Algorithm = LinkedCrcAlgorithm.Crc16CcittFalse)) Crc16CcittFalse
            add (crcs |> List.exists (fun c -> c.DataId.IsSome)) CrcDataId
            add (not image.RxCounters.IsEmpty) RxCounter
            add (not image.TxCounters.IsEmpty) TxCounter

            let payload =
                [ yield! linked.Messages |> List.map (fun m -> uint32 m.Length)
                  yield! linked.TxMessages |> List.map (fun m -> uint32 m.Length) ]
                |> List.fold max 0u

            return
                { RuntimeImageMajor = 1us
                  RuntimeImageMinor = 0us
                  Features = features
                  PoolAbiHash = hash
                  ImageBytes = uint32 bytes.Length
                  RuntimeStateBytes = state
                  RuntimeScratchBytes = runtimeScratchBytes image
                  RxMessages = uint32 image.Messages.Length
                  RxPrograms = uint32 image.Programs.Length
                  TxMessages = uint32 image.TxMessages.Length
                  TxPrograms = uint32 image.TxPrograms.Length
                  PoolSlots = uint32 image.PoolSlotCount
                  Conversions = uint32 image.Conversions.Length
                  NestedMuxRecords = uint32 image.NestedMuxRecords.Length
                  MuxDepth =
                    max
                        (maxDepth linked)
                        (image.NestedMuxRecords
                         |> List.map (fun record -> uint32 record.Predicates.Length)
                         |> List.fold max 0u)
                  QualityEntries = uint32 image.QualityEntries.Length
                  ProtectionPlans = uint32 (image.RxProtectionPlans.Length + image.TxProtectionPlans.Length)
                  TxCounters = uint32 image.TxCounters.Length
                  RxCounters = uint32 image.RxCounters.Length
                  CoverageSpans = uint32 image.CoverageSpans.Length
                  TxTemplateBytes = uint32 image.TxTemplates.Length
                  PayloadBytes = payload }
        }
