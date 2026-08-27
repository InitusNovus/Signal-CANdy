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

    type RuntimeImage =
        { Messages: ImageMessage list
          Programs: ImageProgram list
          Conversions: ImageConversion list
          SignalNames: string list
          MessageNames: string list }

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

    let private lowerErrors (schema: LinkedSchema) =
        let plans = schema.Messages |> List.collect _.Plans
        let slots = plans |> List.map _.PoolSlotIndex
        let sortedSlots = slots |> List.sort
        let expectedSlots = [ for index in 0 .. plans.Length - 1 -> uint16 index ]

        [ if schema.Messages.Length > MaxMessages then
              ImageLimit "message_count exceeds 4096"

          if plans.Length > MaxPrograms then
              ImageLimit "signal_count exceeds 8192"

          if sortedSlots <> expectedSlots then
              ImageTable

          let encodedIds =
              schema.Messages
              |> List.map (fun message -> encodedCanId message.Id message.IsExtended)

          if (encodedIds |> List.distinct).Length <> encodedIds.Length then
              ImageTable

          for message in schema.Messages do
              if not (validCanId message.Id message.IsExtended) then
                  ImageTable

              if message.Plans.IsEmpty then
                  ImageTable

              if nameError message.Name then
                  ImageLimit(sprintf "message name '%s' is not 1..255 UTF-8 bytes" message.Name)

              for plan in message.Plans do
                  if nameError plan.PoolSignalName then
                      ImageLimit(sprintf "signal name '%s' is not 1..255 UTF-8 bytes" plan.PoolSignalName)

                  if
                      plan.Length < 1us
                      || plan.Length > 64us
                      || uint32 plan.StartBit + uint32 plan.Length > 512u
                  then
                      ImageTable

                  if
                      isIntegerStorageValue (storageValue plan.Storage)
                      && (plan.Factor <> 1.0 || plan.Offset <> 0.0)
                  then
                      ImageTable

                  if plan.Factor = 0.0 && (plan.Factor <> 1.0 || plan.Offset <> 0.0) then
                      ImageTable

                  match plan.Mux, plan.MuxSelectorSlot, plan.MuxExpected with
                  | Branch expected, Some selectorSlot, Some actual when
                      selectorSlot <> plan.PoolSlotIndex && actual = uint32 expected
                      ->
                      ()
                  | Branch _, _, _ -> ImageTable
                  | Selector, None, None
                  | Unconditional, None, None -> ()
                  | _ -> ImageTable

              let selectors =
                  message.Plans
                  |> List.filter (fun plan ->
                      match plan.Mux with
                      | Selector -> true
                      | _ -> false)

              let branches =
                  message.Plans
                  |> List.filter (fun plan ->
                      match plan.Mux with
                      | Branch _ -> true
                      | _ -> false)

              if selectors.Length > 1 || (not branches.IsEmpty && selectors.Length <> 1) then
                  ImageTable

              match selectors with
              | [ selector ] ->
                  if
                      branches
                      |> List.exists (fun branch -> branch.MuxSelectorSlot <> Some selector.PoolSlotIndex)
                  then
                      ImageTable
              | _ -> () ]

    let lower (schema: LinkedSchema) : Result<RuntimeImage, ValidationError list> =
        let errors = lowerErrors schema

        if not errors.IsEmpty then
            Error errors
        else
            let conversions = ResizeArray<ImageConversion>()
            let conversionIndices = Dictionary<struct (int64 * int64), uint16>()
            conversions.Add(identityConversion)
            conversionIndices.Add(conversionKey 1.0 0.0, 0us)

            for message in schema.Messages do
                for plan in message.Plans do
                    if plan.Factor <> 1.0 || plan.Offset <> 0.0 then
                        let key = conversionKey plan.Factor plan.Offset

                        if not (conversionIndices.ContainsKey(key)) then
                            let index = uint16 conversions.Count
                            conversionIndices.Add(key, index)

                            conversions.Add(
                                { IsAffine = true
                                  Factor = plan.Factor
                                  Offset = plan.Offset }
                            )

            if conversions.Count > MaxConversions then
                Error[ImageLimit "conversion_count exceeds 1024"]
            else
                let symbolPayloadSize =
                    4
                    + (schema.Messages
                       |> List.sumBy (fun message -> 2 + utf8.GetByteCount(message.Name)))
                    + (schema.Messages
                       |> List.collect _.Plans
                       |> List.sumBy (fun plan -> 2 + utf8.GetByteCount(plan.PoolSignalName)))

                let symbolSize = (symbolPayloadSize + 3) / 4 * 4

                let totalSize =
                    HeaderSize
                    + DirectorySize
                    + schema.Messages.Length * 8
                    + (schema.Messages |> List.sumBy (fun message -> message.Plans.Length)) * 16
                    + conversions.Count * 24
                    + symbolSize
                    + 4

                let conversionIndex (plan: DecodePlan) =
                    if plan.Factor = 1.0 && plan.Offset = 0.0 then
                        0us
                    else
                        conversionIndices.[conversionKey plan.Factor plan.Offset]

                let mutable nextProgramIndex = 0
                let programs = ResizeArray<ImageProgram>()
                let messages = ResizeArray<ImageMessage>()
                let messageNames = ResizeArray<string>()

                schema.Messages
                |> List.sortBy (fun message -> encodedCanId message.Id message.IsExtended)
                |> List.iter (fun message ->
                    let orderedPlans =
                        message.Plans
                        |> List.sortBy (fun plan ->
                            let selectorRank =
                                match plan.Mux with
                                | Selector -> 0
                                | _ -> 1

                            selectorRank, plan.StartBit, plan.PoolSlotIndex)

                    messages.Add(
                        { EncodedCanId = encodedCanId message.Id message.IsExtended
                          ProgramCount = uint16 orderedPlans.Length
                          ProgramIndex = uint16 nextProgramIndex }
                    )

                    messageNames.Add(message.Name)

                    orderedPlans
                    |> List.iter (fun plan ->
                        let muxSelectorSlot, muxExpected =
                            match plan.MuxSelectorSlot, plan.MuxExpected with
                            | Some slot, Some expected -> slot, expected
                            | _ -> UInt16.MaxValue, UInt32.MaxValue

                        let orderFlags =
                            (if plan.ByteOrder = Big then 1uy else 0uy)
                            ||| (if plan.IsSigned then 2uy else 0uy)

                        programs.Add(
                            { StartBit = plan.StartBit
                              LengthBits = plan.Length
                              OrderFlags = orderFlags
                              Storage = storageValue plan.Storage
                              ConversionIndex = conversionIndex plan
                              SlotIndex = plan.PoolSlotIndex
                              MuxSelectorSlot = muxSelectorSlot
                              MuxExpected = muxExpected }
                        ))

                    nextProgramIndex <- nextProgramIndex + orderedPlans.Length)

                let signalNames =
                    schema.Messages
                    |> List.collect _.Plans
                    |> List.sortBy _.PoolSlotIndex
                    |> List.map _.PoolSignalName

                if totalSize > MaxImageSize then
                    Error[ImageLimit "total_size exceeds 1 MiB"]
                else
                    Ok
                        { Messages = messages |> Seq.toList
                          Programs = programs |> Seq.toList
                          Conversions = conversions |> Seq.toList
                          SignalNames = signalNames
                          MessageNames = messageNames |> Seq.toList }

    let private messageRangeErrors (image: RuntimeImage) =
        let mutable expectedIndex = 0

        [ for message in image.Messages do
              if message.ProgramCount = 0us || int message.ProgramIndex <> expectedIndex then
                  ImageTable

              let rangeEnd = int message.ProgramIndex + int message.ProgramCount

              if rangeEnd > image.Programs.Length then
                  ImageTable

              expectedIndex <- rangeEnd

          if expectedIndex <> image.Programs.Length then
              ImageTable ]

    let private programErrors (image: RuntimeImage) =
        let conversionArray = image.Conversions |> List.toArray

        [ for program in image.Programs do
              if
                  program.LengthBits < 1us
                  || program.LengthBits > 64us
                  || uint32 program.StartBit + uint32 program.LengthBits > 512u
              then
                  ImageTable

              if program.OrderFlags > 3uy || program.Storage > 9uy then
                  ImageTable

              if int program.ConversionIndex >= conversionArray.Length then
                  ImageTable

              if int program.SlotIndex >= image.Programs.Length then
                  ImageTable

              let isUnconditional = program.MuxSelectorSlot = UInt16.MaxValue
              let hasSentinelExpected = program.MuxExpected = UInt32.MaxValue

              if isUnconditional <> hasSentinelExpected then
                  ImageTable

              if not isUnconditional then
                  if int program.MuxSelectorSlot >= image.Programs.Length then
                      ImageTable

                  if program.MuxSelectorSlot = program.SlotIndex then
                      ImageTable

              if isIntegerStorageValue program.Storage then
                  if program.ConversionIndex <> 0us then
                      ImageTable
                  elif conversionArray.Length > 0 && conversionArray.[0] <> identityConversion then
                      ImageTable ]

    let private selectorErrors (image: RuntimeImage) =
        let programs = image.Programs |> List.toArray

        [ for message in image.Messages do
              let first = int message.ProgramIndex
              let count = int message.ProgramCount

              if first >= 0 && count > 0 && first + count <= programs.Length then
                  let messagePrograms = programs.[first .. first + count - 1]

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

                      match selectorIndex with
                      | Some 0 -> ()
                      | _ -> ImageTable ]

    let private validateRuntimeImage (image: RuntimeImage) =
        let conversionErrors =
            [ if image.Conversions.IsEmpty || image.Conversions.Head <> identityConversion then
                  ImageTable

              for conversion in image.Conversions do
                  if conversion.IsAffine then
                      if conversion.Factor = 0.0 then
                          ImageTable
                  elif conversion.Factor <> 1.0 || conversion.Offset <> 0.0 then
                      ImageTable ]

        let slots = image.Programs |> List.map _.SlotIndex |> List.sort
        let expectedSlots = [ for index in 0 .. image.Programs.Length - 1 -> uint16 index ]

        [ if image.Messages.Length > MaxMessages then
              ImageLimit "message_count exceeds 4096"

          if image.Programs.Length > MaxPrograms then
              ImageLimit "signal_count exceeds 8192"

          if image.Conversions.Length > MaxConversions then
              ImageLimit "conversion_count exceeds 1024"

          if
              image.SignalNames.Length <> image.Programs.Length
              || image.MessageNames.Length <> image.Messages.Length
          then
              ImageTable

          if slots <> expectedSlots then
              ImageTable

          let ids = image.Messages |> List.map _.EncodedCanId

          if ids <> List.sort ids || ids.Length <> (ids |> List.distinct).Length then
              ImageTable

          for message in image.Messages do
              let extended = (message.EncodedCanId &&& 0x80000000u) <> 0u
              let id = message.EncodedCanId &&& 0x7FFFFFFFu

              if not (validCanId id extended) then
                  ImageTable

          for name in image.SignalNames @ image.MessageNames do
              if nameError name then
                  ImageLimit "a symbol name is not 1..255 UTF-8 bytes"

          yield! conversionErrors
          yield! messageRangeErrors image
          yield! programErrors image
          yield! selectorErrors image ]

    let private putU16 (bytes: byte array) offset value =
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value)

    let private putU32 (bytes: byte array) offset value =
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value)

    let private putDouble (bytes: byte array) offset value =
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset, 8), BitConverter.DoubleToInt64Bits(value))

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
                if (crc &&& 1u) <> 0u then
                    crc <- (crc >>> 1) ^^^ 0xEDB88320u
                else
                    crc <- crc >>> 1

        crc ^^^ UInt32.MaxValue

    let write (image: RuntimeImage) : Result<byte array, ValidationError list> =
        let errors = validateRuntimeImage image

        if not errors.IsEmpty then
            Error errors
        else
            let symbols = symbolSection image
            let msgOffset = HeaderSize + DirectorySize
            let msgSize = image.Messages.Length * 8
            let prgOffset = msgOffset + msgSize
            let prgSize = image.Programs.Length * 16
            let cnvOffset = prgOffset + prgSize
            let cnvSize = image.Conversions.Length * 24
            let symOffset = cnvOffset + cnvSize
            let symSize = symbols.Length
            let totalSize = symOffset + symSize + 4

            if totalSize > MaxImageSize then
                Error[ImageLimit "total_size exceeds 1 MiB"]
            else
                let bytes = Array.zeroCreate<byte> totalSize
                Array.Copy(magic, 0, bytes, 0, magic.Length)
                putU16 bytes 8 1us
                putU16 bytes 10 0us
                putU32 bytes 12 (uint32 totalSize)
                putU16 bytes 16 (uint16 image.Messages.Length)
                putU16 bytes 18 (uint16 image.Programs.Length)
                putU16 bytes 20 (uint16 image.Conversions.Length)
                putU16 bytes 22 0us

                [| msgOffset, msgSize
                   prgOffset, prgSize
                   cnvOffset, cnvSize
                   symOffset, symSize |]
                |> Array.iteri (fun index (offset, size) ->
                    let entryOffset = HeaderSize + index * 8
                    putU32 bytes entryOffset (uint32 offset)
                    putU32 bytes (entryOffset + 4) (uint32 size))

                image.Messages
                |> List.iteri (fun index message ->
                    let offset = msgOffset + index * 8
                    putU32 bytes offset message.EncodedCanId
                    putU16 bytes (offset + 4) message.ProgramCount
                    putU16 bytes (offset + 6) message.ProgramIndex)

                image.Programs
                |> List.iteri (fun index program ->
                    let offset = prgOffset + index * 16
                    putU16 bytes offset program.StartBit
                    putU16 bytes (offset + 2) program.LengthBits
                    bytes.[offset + 4] <- program.OrderFlags
                    bytes.[offset + 5] <- program.Storage
                    putU16 bytes (offset + 6) program.ConversionIndex
                    putU16 bytes (offset + 8) program.SlotIndex
                    putU16 bytes (offset + 10) program.MuxSelectorSlot
                    putU32 bytes (offset + 12) program.MuxExpected)

                image.Conversions
                |> List.iteri (fun index conversion ->
                    let offset = cnvOffset + index * 24
                    bytes.[offset] <- if conversion.IsAffine then 1uy else 0uy
                    putDouble bytes (offset + 8) conversion.Factor
                    putDouble bytes (offset + 16) conversion.Offset)

                Array.Copy(symbols, 0, bytes, symOffset, symbols.Length)
                putU32 bytes (totalSize - 4) (crc32 bytes (totalSize - 4))
                Ok bytes

    let private getU16 (bytes: byte array) offset =
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))

    let private getU32 (bytes: byte array) offset =
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))

    let private getDouble (bytes: byte array) offset =
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8))
        |> BitConverter.Int64BitsToDouble

    let private allZero (bytes: byte array) start count =
        count <= 0 || bytes.AsSpan(start, count).IndexOfAnyExcept(0uy) < 0

    let private parseSymbols bytes offset size signalCount messageCount =
        if size < 4 then
            Error[ImageTable]
        elif
            getU16 bytes offset <> uint16 signalCount
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

            let paddingLength = sectionEnd - cursor

            if
                failed
                || paddingLength < 0
                || paddingLength > 3
                || not (allZero bytes cursor paddingLength)
            then
                Error[ImageTable]
            else
                let allNames = names |> Seq.toList
                Ok(allNames |> List.take signalCount, allNames |> List.skip signalCount)

    let private readDirectory (bytes: byte array) totalSize messageCount signalCount conversionCount =
        let sections =
            Array.init 4 (fun index ->
                let offset = HeaderSize + index * 8
                getU32 bytes offset, getU32 bytes (offset + 4))

        if sections |> Array.exists (fun (offset, _) -> offset % 4u <> 0u) then
            Error[ImageAlign]
        elif
            sections
            |> Array.exists (fun (offset, size) ->
                offset < uint32 (HeaderSize + DirectorySize)
                || uint64 offset + uint64 size > uint64 (totalSize - 4))
        then
            Error[ImageBounds]
        elif snd sections.[3] % 4u <> 0u then
            Error[ImageAlign]
        elif
            snd sections.[0] <> uint32 (messageCount * 8)
            || snd sections.[1] <> uint32 (signalCount * 16)
            || snd sections.[2] <> uint32 (conversionCount * 24)
        then
            Error[ImageTable]
        else
            let mutable previousEnd = HeaderSize + DirectorySize
            let mutable badOrder = false
            let mutable badPadding = false

            for offsetValue, sizeValue in sections do
                let offset = int offsetValue
                let size = int sizeValue

                if offset < previousEnd then
                    badOrder <- true
                elif not (allZero bytes previousEnd (offset - previousEnd)) then
                    badPadding <- true

                previousEnd <- offset + size

            if previousEnd > totalSize - 4 then
                Error[ImageBounds]
            elif badOrder then
                Error[ImageBounds]
            elif badPadding || not (allZero bytes previousEnd (totalSize - 4 - previousEnd)) then
                Error[ImageTable]
            else
                Ok(sections |> Array.map (fun (offset, size) -> int offset, int size))

    let private parseMessages bytes offset count =
        [ for index in 0 .. count - 1 do
              let entry = offset + index * 8

              yield
                  { EncodedCanId = getU32 bytes entry
                    ProgramCount = getU16 bytes (entry + 4)
                    ProgramIndex = getU16 bytes (entry + 6) } ]

    let private parsePrograms bytes offset count =
        [ for index in 0 .. count - 1 do
              let entry = offset + index * 16

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
        let conversions = ResizeArray<ImageConversion>()
        let mutable failed = false

        for index in 0 .. count - 1 do
            let entry = offset + index * 24
            let kind = bytes.[entry]
            let factor = getDouble bytes (entry + 8)
            let conversionOffset = getDouble bytes (entry + 16)

            if kind > 1uy || not (allZero bytes (entry + 1) 7) then
                failed <- true
            elif kind = 0uy && (factor <> 1.0 || conversionOffset <> 0.0) then
                failed <- true
            elif kind = 1uy && factor = 0.0 then
                failed <- true

            conversions.Add(
                { IsAffine = kind = 1uy
                  Factor = factor
                  Offset = conversionOffset }
            )

        if failed then
            Error[ImageTable]
        else
            Ok(conversions |> Seq.toList)

    let read (bytes: byte array) : Result<RuntimeImage, ValidationError list> =
        if isNull bytes || bytes.Length < HeaderSize + DirectorySize + 4 then
            Error[ImageSize]
        elif not (bytes.AsSpan(0, magic.Length).SequenceEqual(magic)) then
            Error[ImageBadMagic]
        elif getU16 bytes 8 <> 1us then
            Error[ImageBadVersion]
        else
            let totalSize = int (getU32 bytes 12)

            if totalSize <> bytes.Length then
                Error[ImageSize]
            elif totalSize > MaxImageSize then
                Error[ImageLimit "total_size exceeds 1 MiB"]
            elif getU16 bytes 10 <> 0us || getU16 bytes 22 <> 0us || not (allZero bytes 24 8) then
                Error[ImageTable]
            else
                let messageCount = int (getU16 bytes 16)
                let signalCount = int (getU16 bytes 18)
                let conversionCount = int (getU16 bytes 20)

                if messageCount > MaxMessages then
                    Error[ImageLimit "message_count exceeds 4096"]
                elif signalCount > MaxPrograms then
                    Error[ImageLimit "signal_count exceeds 8192"]
                elif conversionCount > MaxConversions then
                    Error[ImageLimit "conversion_count exceeds 1024"]
                else
                    match readDirectory bytes totalSize messageCount signalCount conversionCount with
                    | Error errors -> Error errors
                    | Ok sections ->
                        let storedCrc = getU32 bytes (totalSize - 4)
                        let actualCrc = crc32 bytes (totalSize - 4)

                        if storedCrc <> actualCrc then
                            Error[ImageCrc]
                        else
                            let msgOffset, _ = sections.[0]
                            let prgOffset, _ = sections.[1]
                            let cnvOffset, _ = sections.[2]
                            let symOffset, symSize = sections.[3]
                            let messages = parseMessages bytes msgOffset messageCount
                            let programs = parsePrograms bytes prgOffset signalCount

                            match parseConversions bytes cnvOffset conversionCount with
                            | Error errors -> Error errors
                            | Ok conversions ->
                                match parseSymbols bytes symOffset symSize signalCount messageCount with
                                | Error errors -> Error errors
                                | Ok(signalNames, messageNames) ->
                                    let image =
                                        { Messages = messages
                                          Programs = programs
                                          Conversions = conversions
                                          SignalNames = signalNames
                                          MessageNames = messageNames }

                                    let errors = validateRuntimeImage image

                                    if errors.IsEmpty then Ok image else Error errors

    let inspect (bytes: byte array) : Result<string, ValidationError list> =
        match read bytes with
        | Error errors -> Error errors
        | Ok image ->
            use stream = new MemoryStream()
            let options = JsonWriterOptions(Indented = true)
            use writer = new Utf8JsonWriter(stream, options)
            writer.WriteStartObject()
            writer.WriteNumber("formatVersion", 1)
            writer.WriteNumber("totalSize", bytes.Length)
            writer.WriteString("crc32Hex", sprintf "0x%08X" (getU32 bytes (bytes.Length - 4)))
            writer.WriteBoolean("crcValid", true)
            writer.WriteNumber("messageCount", image.Messages.Length)
            writer.WriteNumber("signalCount", image.Programs.Length)
            writer.WriteNumber("conversionCount", image.Conversions.Length)
            writer.WritePropertyName("messages")
            writer.WriteStartArray()

            (image.Messages, image.MessageNames)
            ||> List.iter2 (fun message name ->
                let extended = (message.EncodedCanId &&& 0x80000000u) <> 0u
                let canId = message.EncodedCanId &&& 0x7FFFFFFFu
                writer.WriteStartObject()
                writer.WriteString("name", name)
                writer.WriteNumber("canId", canId)
                writer.WriteBoolean("extended", extended)
                writer.WriteNumber("programCount", int message.ProgramCount)
                writer.WriteNumber("firstProgramIndex", int message.ProgramIndex)
                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WritePropertyName("signals")
            writer.WriteStartArray()

            image.Programs
            |> List.sortBy _.SlotIndex
            |> List.iter (fun program ->
                let name = image.SignalNames.[int program.SlotIndex]
                writer.WriteStartObject()
                writer.WriteNumber("slot", int program.SlotIndex)
                writer.WriteString("name", name)
                writer.WriteNumber("startBit", int program.StartBit)
                writer.WriteNumber("lengthBits", int program.LengthBits)
                writer.WriteBoolean("bigEndian", (program.OrderFlags &&& 1uy) <> 0uy)
                writer.WriteBoolean("signed", (program.OrderFlags &&& 2uy) <> 0uy)
                writer.WriteNumber("storage", int program.Storage)
                writer.WriteNumber("conversionIndex", int program.ConversionIndex)
                writer.WriteEndObject())

            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.Flush()

            let json = Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n")
            Ok(json + "\n")
