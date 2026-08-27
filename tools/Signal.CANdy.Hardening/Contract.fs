namespace Signal.CANdy.Hardening

open System

[<RequireQualifiedAccess>]
type RegionKind =
    | Header
    | Directory
    | RxMessage
    | Program
    | Conversion
    | Symbols
    | ExtensionHeader
    | NestedMux
    | Quality
    | ProtectionHeader
    | ProtectionPlan
    | RxCounter
    | CoverageSpan
    | TxHeader
    | TxMessage
    | TxProgram
    | TxCounter
    | TxTemplate
    | Padding
    | Footer

[<RequireQualifiedAccess>]
type FieldEncoding =
    | U8
    | U16LE
    | U32LE
    | U64LE
    | F64LE
    | Bytes

[<RequireQualifiedAccess>]
type FieldDomain =
    | Boundaries
    | Flags
    | Sentinels
    | FloatingPoint
    | Utf8
    | RawBytes
    | Crc

[<RequireQualifiedAccess>]
type CrcPolicy =
    | Preserve
    | Repair

type FieldSpec =
    { Path: string
      Region: RegionKind
      RelativeOffset: int
      Width: int
      Encoding: FieldEncoding
      Domain: FieldDomain
      CrcPolicy: CrcPolicy }

[<RequireQualifiedAccess>]
type MutationClass =
    | Field
    | Structural
    | Bounded

[<RequireQualifiedAccess>]
type MinimizePhase =
    | Regions
    | Ranges
    | Bytes
    | FieldValues

type BaseSpec =
    { Ordinal: int
      Id: string
      Cases: int
      FieldCases: int
      StructuralCases: int
      BoundedCases: int
      Bytes: int
      Sha256: string
      Source: string }

type CasePlan =
    { Id: string
      Base: BaseSpec
      Class: MutationClass
      Target: string
      Ordinal: int
      DerivedSeed: uint64 }

module SplitMix64 =

    [<Literal>]
    let Gamma = 0x9E3779B97F4A7C15UL

    let mix (value: uint64) =
        let mutable z = value
        z <- (z ^^^ (z >>> 30)) * 0xBF58476D1CE4E5B9UL
        z <- (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
        z ^^^ (z >>> 31)

    let next state =
        let state' = state + Gamma
        state', mix state'

module Contract =

    [<Literal>]
    let RootSeed = 0x5343494D47323501UL

    [<Literal>]
    let CorpusMagic = "SCORP01\000"

    let bases =
        [ { Ordinal = 0
            Id = "legacy-rx"
            Cases = 2000
            FieldCases = 500
            StructuralCases = 250
            BoundedCases = 1250
            Bytes = 376
            Sha256 = "d25dc336c2eb44b39873c2cfa45f8cca00fce54558ea793840f682fd0414726b"
            Source = "generated:legacy-rx" }
          { Ordinal = 1
            Id = "tx"
            Cases = 2000
            FieldCases = 500
            StructuralCases = 250
            BoundedCases = 1250
            Bytes = 432
            Sha256 = "681fa350bf5fc1ac4c248ac7ec8bbb1d0962a958d774c8b8ae04abfe723ba013"
            Source = "generated:tx" }
          { Ordinal = 2
            Id = "rxq-nested"
            Cases = 2000
            FieldCases = 500
            StructuralCases = 250
            BoundedCases = 1250
            Bytes = 372
            Sha256 = "1e5f2348ce5474a33a8eda4aa8a7a101a7bafe55450a219ec73a3b75f05f767f"
            Source = "generated:rxq-nested" }
          { Ordinal = 3
            Id = "protection"
            Cases = 2000
            FieldCases = 500
            StructuralCases = 250
            BoundedCases = 1250
            Bytes = 428
            Sha256 = "26e6f8529af6c840d294a87cb967a490b9cd78394b2c9911fee32681660fe7df"
            Source = "generated:protection" }
          { Ordinal = 4
            Id = "activation-a"
            Cases = 1000
            FieldCases = 250
            StructuralCases = 125
            BoundedCases = 625
            Bytes = 444
            Sha256 = "9197bf85693f823f3623f9562a2a892468dc461a1c7cdaf4f60a6dc91cad6d1e"
            Source = "examples/scimg_activation_demo/build/schema_a.scimg" }
          { Ordinal = 5
            Id = "activation-b"
            Cases = 1000
            FieldCases = 250
            StructuralCases = 125
            BoundedCases = 625
            Bytes = 444
            Sha256 = "6b1a5bdf3255bff17e12195bea2fd4703ae6427e06f2e701d7fde231e05312f2"
            Source = "examples/scimg_activation_demo/build/schema_b.scimg" } ]

    let fieldValueFamilies =
        [ "zero"
          "one"
          "valid-maximum"
          "boundary-plus-one"
          "all-bits-set"
          "sentinel-alternative"
          "actual-minus-one"
          "actual-plus-one" ]

    let structuralFamilies =
        [ "truncate-boundary-minus-one"
          "truncate-boundary"
          "truncate-boundary-plus-one"
          "append"
          "insert"
          "remove"
          "stale-metadata-crc"
          "coherent-metadata-crc-repair"
          "extension-insert"
          "extension-remove"
          "section-overlap"
          "section-gap"
          "section-reorder"
          "misalignment" ]

    let boundedMix625 =
        [ "single-bit-flip", 125
          "byte-replacement", 125
          "range-xor", 100
          "range-fill", 75
          "insertion", 75
          "removal", 75
          "coherent-multi-field", 50 ]

    let parserBoundaries =
        [ "imageBytes", 1048576L, 1048577L
          "rxMessages", 4096L, 4097L
          "rxPrograms", 8192L, 8193L
          "conversions", 1024L, 1025L
          "poolSlots", 8192L, 8193L
          "txMessages", 4096L, 4097L
          "txPrograms", 8192L, 8193L
          "txCounters", 4096L, 4097L
          "nestedMuxRecords", 8192L, 8193L
          "muxDepth", 4L, 5L
          "rxCounters", 4096L, 4097L
          "coverageSpans", 16384L, 16385L
          "coverageSpansPerPlan", 2L, 3L
          "symbolUtf8Bytes", 255L, 256L
          "payloadBytes", 64L, 65L
          "freshnessMs", 2147483647L, 2147483648L
          "alignedOffset", 4L, 5L
          "exactEnd", 0L, 1L ]

    let minimizationOrder =
        [ MinimizePhase.Regions
          MinimizePhase.Ranges
          MinimizePhase.Bytes
          MinimizePhase.FieldValues ]

    let private field path region offset width encoding domain crc =
        { Path = path
          Region = region
          RelativeOffset = offset
          Width = width
          Encoding = encoding
          Domain = domain
          CrcPolicy = crc }

    let private repaired path region offset width encoding domain =
        field path region offset width encoding domain CrcPolicy.Repair

    let private preserved path region offset width encoding domain =
        field path region offset width encoding domain CrcPolicy.Preserve

    let private predicateFields =
        [ for index in 0..3 do
              let offset = 4 + index * 8

              repaired
                  (sprintf "nmx.predicate[%d].program" index)
                  RegionKind.NestedMux
                  offset
                  2
                  FieldEncoding.U16LE
                  FieldDomain.Sentinels

              repaired
                  (sprintf "nmx.predicate[%d].slot" index)
                  RegionKind.NestedMux
                  (offset + 2)
                  2
                  FieldEncoding.U16LE
                  FieldDomain.Sentinels

              repaired
                  (sprintf "nmx.predicate[%d].value" index)
                  RegionKind.NestedMux
                  (offset + 4)
                  4
                  FieldEncoding.U32LE
                  FieldDomain.Sentinels ]

    let fieldCatalog =
        [ preserved "header.magic" RegionKind.Header 0 8 FieldEncoding.Bytes FieldDomain.RawBytes
          preserved "header.version" RegionKind.Header 8 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "header.featureFlags" RegionKind.Header 10 2 FieldEncoding.U16LE FieldDomain.Flags
          preserved "header.totalSize" RegionKind.Header 12 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "header.rxMessageCount" RegionKind.Header 16 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "header.poolSlotCount" RegionKind.Header 18 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "header.conversionCount" RegionKind.Header 20 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "header.txMessageCount" RegionKind.Header 22 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "header.containerOffset" RegionKind.Header 24 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "header.containerSize" RegionKind.Header 28 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "header.reserved[0]" RegionKind.Header 30 1 FieldEncoding.U8 FieldDomain.Boundaries
          repaired "header.reserved[1]" RegionKind.Header 31 1 FieldEncoding.U8 FieldDomain.Boundaries
          for index, name in [ 0, "msg"; 1, "prg"; 2, "cnv"; 3, "sym" ] do
              repaired
                  (sprintf "directory.%s.offset" name)
                  RegionKind.Directory
                  (index * 8)
                  4
                  FieldEncoding.U32LE
                  FieldDomain.Boundaries

              repaired
                  (sprintf "directory.%s.size" name)
                  RegionKind.Directory
                  (index * 8 + 4)
                  4
                  FieldEncoding.U32LE
                  FieldDomain.Boundaries
          repaired "directory.padding" RegionKind.Padding 0 1 FieldEncoding.Bytes FieldDomain.RawBytes
          repaired "sections.interPadding" RegionKind.Padding 0 1 FieldEncoding.Bytes FieldDomain.RawBytes
          repaired "msg.canId" RegionKind.RxMessage 0 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "msg.programCount" RegionKind.RxMessage 4 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "msg.programIndex" RegionKind.RxMessage 6 2 FieldEncoding.U16LE FieldDomain.Boundaries
          for prefix, region in [ "prg", RegionKind.Program; "txp", RegionKind.TxProgram ] do
              repaired (prefix + ".startBit") region 0 2 FieldEncoding.U16LE FieldDomain.Boundaries
              repaired (prefix + ".lengthBits") region 2 2 FieldEncoding.U16LE FieldDomain.Boundaries
              repaired (prefix + ".orderFlags") region 4 1 FieldEncoding.U8 FieldDomain.Flags
              repaired (prefix + ".storage") region 5 1 FieldEncoding.U8 FieldDomain.Boundaries
              repaired (prefix + ".conversionIndex") region 6 2 FieldEncoding.U16LE FieldDomain.Sentinels
              repaired (prefix + ".slotIndex") region 8 2 FieldEncoding.U16LE FieldDomain.Sentinels
              repaired (prefix + ".muxSlot") region 10 2 FieldEncoding.U16LE FieldDomain.Sentinels
              repaired (prefix + ".muxValue") region 12 4 FieldEncoding.U32LE FieldDomain.Sentinels
          repaired "cnv.kind" RegionKind.Conversion 0 1 FieldEncoding.U8 FieldDomain.Boundaries
          for index in 0..6 do
              repaired
                  (sprintf "cnv.reserved[%d]" index)
                  RegionKind.Conversion
                  (index + 1)
                  1
                  FieldEncoding.U8
                  FieldDomain.Boundaries
          repaired "cnv.factor" RegionKind.Conversion 8 8 FieldEncoding.F64LE FieldDomain.FloatingPoint
          repaired "cnv.offset" RegionKind.Conversion 16 8 FieldEncoding.F64LE FieldDomain.FloatingPoint
          repaired "sym.signalCount" RegionKind.Symbols 0 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "sym.messageCount" RegionKind.Symbols 2 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "sym.name.length" RegionKind.Symbols 4 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "sym.name.bytes" RegionKind.Symbols 6 1 FieldEncoding.Bytes FieldDomain.Utf8
          repaired "sym.name.nul" RegionKind.Symbols 6 1 FieldEncoding.U8 FieldDomain.Utf8
          repaired "sym.name.malformedUtf8" RegionKind.Symbols 6 1 FieldEncoding.U8 FieldDomain.Utf8
          repaired "sym.finalPadding" RegionKind.Symbols 0 1 FieldEncoding.Bytes FieldDomain.RawBytes
          repaired "ex01.magic" RegionKind.ExtensionHeader 0 4 FieldEncoding.U32LE FieldDomain.RawBytes
          repaired "ex01.flags" RegionKind.ExtensionHeader 4 2 FieldEncoding.U16LE FieldDomain.Flags
          repaired "ex01.maxMuxDepth" RegionKind.ExtensionHeader 6 1 FieldEncoding.U8 FieldDomain.Boundaries
          repaired "ex01.reserved[0]" RegionKind.ExtensionHeader 7 1 FieldEncoding.U8 FieldDomain.Boundaries
          repaired "ex01.nestedCount" RegionKind.ExtensionHeader 8 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "ex01.qualityCount" RegionKind.ExtensionHeader 10 2 FieldEncoding.U16LE FieldDomain.Boundaries
          for name, offset in
              [ "nestedOffset", 12
                "qualityOffset", 16
                "txOffset", 20
                "txSize", 24
                "protectionOffset", 28
                "protectionSize", 32
                "end", 36 ] do
              repaired ("ex01." + name) RegionKind.ExtensionHeader offset 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "nmx.targetProgram" RegionKind.NestedMux 0 2 FieldEncoding.U16LE FieldDomain.Sentinels
          repaired "nmx.depth" RegionKind.NestedMux 2 1 FieldEncoding.U8 FieldDomain.Boundaries
          repaired "nmx.reserved" RegionKind.NestedMux 3 1 FieldEncoding.U8 FieldDomain.Boundaries
          yield! predicateFields
          repaired "quality.freshnessMs" RegionKind.Quality 0 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "pr01.magic" RegionKind.ProtectionHeader 0 4 FieldEncoding.U32LE FieldDomain.RawBytes
          for name, offset in
              [ "rxPlanCount", 4
                "txPlanCount", 6
                "rxCounterCount", 8
                "coverageSpanCount", 10 ] do
              repaired ("pr01." + name) RegionKind.ProtectionHeader offset 2 FieldEncoding.U16LE FieldDomain.Boundaries
          for name, offset in
              [ "rxPlanOffset", 12
                "txPlanOffset", 16
                "rxCounterOffset", 20
                "coverageSpanOffset", 24
                "end", 28 ] do
              repaired ("pr01." + name) RegionKind.ProtectionHeader offset 4 FieldEncoding.U32LE FieldDomain.Boundaries
          for index in 0..15 do
              repaired
                  (sprintf "pr01.reserved[%d]" index)
                  RegionKind.ProtectionHeader
                  (index + 32)
                  1
                  FieldEncoding.U8
                  FieldDomain.Boundaries
          for name, offset in
              [ "flags", 0
                "algorithm", 1
                "crcWidth", 2
                "byteOrder", 3
                "spanCount", 8
                "dataIdCount", 9 ] do
              repaired
                  ("protectionPlan." + name)
                  RegionKind.ProtectionPlan
                  offset
                  1
                  FieldEncoding.U8
                  FieldDomain.Boundaries
          for name, offset in [ "crcStart", 4; "spanIndex", 6; "dataId", 10; "counterIndex", 12 ] do
              repaired
                  ("protectionPlan." + name)
                  RegionKind.ProtectionPlan
                  offset
                  2
                  FieldEncoding.U16LE
                  FieldDomain.Sentinels
          for index in 0..1 do
              repaired
                  (sprintf "protectionPlan.reserved[%d]" index)
                  RegionKind.ProtectionPlan
                  (index + 14)
                  1
                  FieldEncoding.U8
                  FieldDomain.Boundaries
          repaired "rxCounter.startBit" RegionKind.RxCounter 0 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "rxCounter.lengthBits" RegionKind.RxCounter 2 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "rxCounter.byteOrder" RegionKind.RxCounter 4 1 FieldEncoding.U8 FieldDomain.Flags
          for index in 0..2 do
              repaired
                  (sprintf "rxCounter.reserved[%d]" index)
                  RegionKind.RxCounter
                  (index + 5)
                  1
                  FieldEncoding.U8
                  FieldDomain.Boundaries
          repaired "rxCounter.modulus" RegionKind.RxCounter 8 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "rxCounter.increment" RegionKind.RxCounter 12 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "coverageSpan.byteOffset" RegionKind.CoverageSpan 0 1 FieldEncoding.U8 FieldDomain.Boundaries
          repaired "coverageSpan.byteCount" RegionKind.CoverageSpan 1 1 FieldEncoding.U8 FieldDomain.Boundaries
          for index in 0..1 do
              repaired
                  (sprintf "coverageSpan.reserved[%d]" index)
                  RegionKind.CoverageSpan
                  (index + 2)
                  1
                  FieldEncoding.U8
                  FieldDomain.Boundaries
          repaired "tx01.magic" RegionKind.TxHeader 0 4 FieldEncoding.U32LE FieldDomain.RawBytes
          for name, offset in [ "messageCount", 4; "programCount", 6; "counterCount", 8; "reservedCount", 10 ] do
              repaired ("tx01." + name) RegionKind.TxHeader offset 2 FieldEncoding.U16LE FieldDomain.Boundaries
          for name, offset in
              [ "messageOffset", 12
                "programOffset", 16
                "counterOffset", 20
                "templateOffset", 24
                "templateSize", 28 ] do
              repaired ("tx01." + name) RegionKind.TxHeader offset 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "txMessage.logicalId" RegionKind.TxMessage 0 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "txMessage.canId" RegionKind.TxMessage 4 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "txMessage.payloadLength" RegionKind.TxMessage 8 1 FieldEncoding.U8 FieldDomain.Boundaries
          repaired "txMessage.frameFlags" RegionKind.TxMessage 9 1 FieldEncoding.U8 FieldDomain.Flags
          repaired "txMessage.programCount" RegionKind.TxMessage 10 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "txMessage.programIndex" RegionKind.TxMessage 12 2 FieldEncoding.U16LE FieldDomain.Sentinels
          repaired "txMessage.counterIndex" RegionKind.TxMessage 14 2 FieldEncoding.U16LE FieldDomain.Sentinels
          repaired "txMessage.templateOffset" RegionKind.TxMessage 16 4 FieldEncoding.U32LE FieldDomain.Boundaries
          for index in 0..3 do
              repaired
                  (sprintf "txMessage.reserved[%d]" index)
                  RegionKind.TxMessage
                  (index + 20)
                  1
                  FieldEncoding.U8
                  FieldDomain.Boundaries
          repaired "txCounter.startBit" RegionKind.TxCounter 0 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "txCounter.lengthBits" RegionKind.TxCounter 2 2 FieldEncoding.U16LE FieldDomain.Boundaries
          repaired "txCounter.byteOrder" RegionKind.TxCounter 4 1 FieldEncoding.U8 FieldDomain.Flags
          for index in 0..2 do
              repaired
                  (sprintf "txCounter.reserved[%d]" index)
                  RegionKind.TxCounter
                  (index + 5)
                  1
                  FieldEncoding.U8
                  FieldDomain.Boundaries
          repaired "txCounter.modulus" RegionKind.TxCounter 8 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "txCounter.increment" RegionKind.TxCounter 12 4 FieldEncoding.U32LE FieldDomain.Boundaries
          repaired "txCounter.initialValue" RegionKind.TxCounter 16 4 FieldEncoding.U32LE FieldDomain.Boundaries
          for index in 0..3 do
              repaired
                  (sprintf "txCounter.tailReserved[%d]" index)
                  RegionKind.TxCounter
                  (index + 20)
                  1
                  FieldEncoding.U8
                  FieldDomain.Boundaries
          repaired "txTemplate.bytes" RegionKind.TxTemplate 0 1 FieldEncoding.Bytes FieldDomain.RawBytes
          repaired "txTemplate.padding" RegionKind.Padding 0 1 FieldEncoding.Bytes FieldDomain.RawBytes
          preserved "footer.crc32" RegionKind.Footer 0 4 FieldEncoding.U32LE FieldDomain.Crc ]
        |> List.sortBy _.Path

    let deriveSeed baseOrdinal caseOrdinal =
        let independentState =
            RootSeed
            ^^^ (uint64 baseOrdinal * 0xD2B74407B1CE6E93UL)
            ^^^ (uint64 caseOrdinal * 0xCA5A826395121157UL)

        SplitMix64.next independentState |> snd

    let private boundedTargets count =
        let scale = count / 625

        boundedMix625
        |> List.collect (fun (name, amount) -> List.replicate (amount * scale) name)

    let caseAt (baseSpec: BaseSpec) ordinal =
        if ordinal < 0 || ordinal >= baseSpec.Cases then
            invalidArg (nameof ordinal) "case ordinal is outside its base allocation"

        let mutationClass, target =
            if ordinal < baseSpec.FieldCases then
                MutationClass.Field, fieldCatalog.[ordinal % fieldCatalog.Length].Path
            elif ordinal < baseSpec.FieldCases + baseSpec.StructuralCases then
                let local = ordinal - baseSpec.FieldCases
                MutationClass.Structural, structuralFamilies.[local % structuralFamilies.Length]
            else
                let local = ordinal - baseSpec.FieldCases - baseSpec.StructuralCases
                let targets = boundedTargets baseSpec.BoundedCases
                MutationClass.Bounded, targets.[local]

        let className =
            match mutationClass with
            | MutationClass.Field -> "field"
            | MutationClass.Structural -> "structural"
            | MutationClass.Bounded -> "bounded"

        let seed = deriveSeed baseSpec.Ordinal ordinal

        { Id = sprintf "%s/%s/%s/%04d/%016x" baseSpec.Id className target ordinal seed
          Base = baseSpec
          Class = mutationClass
          Target = target
          Ordinal = ordinal
          DerivedSeed = seed }

    let cases =
        bases
        |> List.collect (fun baseSpec -> [ for ordinal in 0 .. baseSpec.Cases - 1 -> caseAt baseSpec ordinal ])

    let replay caseId =
        cases
        |> List.tryFind (fun plan -> String.Equals(plan.Id, caseId, StringComparison.Ordinal))
