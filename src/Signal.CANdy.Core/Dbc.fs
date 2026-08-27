namespace Signal.CANdy.Core

open System
open System.IO
open System.Text.RegularExpressions

open Signal.CANdy.Core.Config
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Errors

module Dbc =

    let private isVectorInternalMessageName (name: string) = name = "VECTOR__INDEPENDENT_SIG_MSG"

    // Compute covered bit positions (0..(DLC*8-1)) for a signal, respecting byte order.
    // For BE (Motorola), use sawtooth numbering with MSB-based start bit.
    let private coveredBits (s: Signal) : int list =
        let start = int s.StartBit
        let len = int s.Length

        match s.ByteOrder with
        | ByteOrder.Little -> [ for i in 0 .. len - 1 -> start + i ]
        | ByteOrder.Big ->
            let byte0 = start / 8
            let bit0 = start % 8 // 7..0

            [ for i in 0 .. len - 1 ->
                  let mutable curByte = byte0
                  let mutable curBit = bit0 - i

                  while curBit < 0 do
                      curBit <- curBit + 8
                      curByte <- curByte + 1

                  curByte * 8 + curBit ]

    let private validateDuplicates (messages: Message list) : string option =
        messages
        |> List.groupBy (fun m -> m.Id)
        |> List.tryPick (fun (id, ms) ->
            if List.length ms > 1 then
                Some(sprintf "Duplicate message ID %u found." id)
            else
                None)

    let private muxPath (message: Message) (signal: Signal) =
        let byName =
            message.Signals |> List.map (fun value -> value.Name, value) |> Map.ofList

        let root =
            message.Signals
            |> List.tryFind (fun value -> value.MultiplexerIndicator = Some "M")

        let rec resolve visited (current: Signal) =
            if Set.contains current.Name visited then
                []
            else
                match current.ExtendedMuxParent with
                | Some parent ->
                    match byName |> Map.tryFind parent.SelectorSignalName with
                    | Some selector ->
                        resolve (Set.add current.Name visited) selector
                        @ [ selector.Name, parent.Expected ]
                    | None -> []
                | None ->
                    match current.MultiplexerIndicator, current.MultiplexerSwitchValue, root with
                    | Some "m", Some expected, Some selector -> [ selector.Name, uint32 expected ]
                    | _ -> []

        resolve Set.empty signal

    // Two paths are exclusive when any shared selector has different exact values.
    let private canCoexist (message: Message) (a: Signal) (b: Signal) =
        let left = muxPath message a |> Map.ofList
        let right = muxPath message b |> Map.ofList

        left
        |> Map.exists (fun selector expected ->
            right |> Map.tryFind selector |> Option.exists (fun other -> other <> expected))
        |> not

    let private validateOverlaps (messages: Message list) : string option =
        let overlapsInMessage (m: Message) : string option =
            let rec checkPairs (signals: Signal list) : string option =
                match signals with
                | []
                | [ _ ] -> None
                | s :: rest ->
                    let sBits = coveredBits s |> Set.ofList

                    let conflict =
                        rest
                        |> List.tryPick (fun t ->
                            if canCoexist m s t then
                                let tBits = coveredBits t |> Set.ofList
                                let inter = Set.intersect sBits tBits

                                if not (Set.isEmpty inter) then
                                    Some(
                                        sprintf
                                            "Signal '%s' in message '%s' overlaps with other signals."
                                            t.Name
                                            m.Name
                                    )
                                else
                                    None
                            else
                                None)

                    match conflict with
                    | Some e -> Some e
                    | None -> checkPairs rest

            checkPairs m.Signals

        messages |> List.tryPick overlapsInMessage

    let private validateExceedsDlc (messages: Message list) : string option =
        let exceedInMessage (m: Message) : string option =
            let totalBits = int m.Length * 8

            m.Signals
            |> List.tryPick (fun s ->
                let bits = coveredBits s

                if bits |> List.exists (fun b -> b < 0 || b >= totalBits) then
                    Some(
                        sprintf
                            "Signal '%s' in message '%s' exceeds the message DLC of %d bytes."
                            s.Name
                            m.Name
                            (int m.Length)
                    )
                else
                    None)

        messages |> List.tryPick exceedInMessage

    let private validateDuplicateIdsFromText (filePath: string) : string option =
        let lines = File.ReadAllLines(filePath)

        let ids =
            lines
            |> Seq.choose (fun line ->
                let t = line.Trim()

                if t.StartsWith("BO_ ") then
                    let parts = t.Split([| ' '; ':' |], StringSplitOptions.RemoveEmptyEntries)

                    if parts.Length >= 3 then
                        let name = parts.[2]

                        if isVectorInternalMessageName name then
                            None
                        else
                            match Int32.TryParse(parts.[1]) with
                            | true, id -> Some id
                            | _ -> None
                    else
                        None
                else
                    None)
            |> Seq.toList

        ids
        |> List.groupBy id
        |> List.tryPick (fun (id, xs) ->
            if List.length xs > 1 then
                Some(sprintf "Duplicate message ID %d found." id)
            else
                None)

    let private tryBuildSignalMuxMap (filePath: string) : Map<string * string, string option * int option> =
        let mutable currentMsg: string option = None
        let mutable entries: (string * string * (string option * int option)) list = []

        for raw in File.ReadLines(filePath) do
            let line = raw.Trim()

            if line.StartsWith("BO_ ") then
                let parts = line.Split([| ' '; ':' |], StringSplitOptions.RemoveEmptyEntries)

                if parts.Length >= 3 then
                    currentMsg <- Some parts.[2]
            elif line.StartsWith("SG_") then
                match currentMsg with
                | None -> ()
                | Some msgName ->
                    let colonIdx = line.IndexOf(':')

                    if colonIdx > 0 then
                        let left = line.Substring(0, colonIdx)
                        let parts = left.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)

                        if parts.Length >= 2 then
                            let sigName = parts.[1]
                            let tokens = parts |> Array.skip 2
                            let mutable muxInd: string option = None
                            let mutable muxVal: int option = None

                            for t in tokens do
                                if t = "M" then
                                    muxInd <- Some "M"
                                elif t.Length >= 1 && t.[0] = 'm' then
                                    let nestedSelector = t.Length > 1 && t.EndsWith("M", StringComparison.Ordinal)
                                    muxInd <- Some(if nestedSelector then "mM" else "m")

                                    if t.Length > 1 then
                                        let valueEnd = if nestedSelector then t.Length - 1 else t.Length
                                        let vStr = t.Substring(1, valueEnd - 1)

                                        match Int32.TryParse(vStr) with
                                        | true, v -> muxVal <- Some v
                                        | _ -> ()

                            if muxInd.IsSome || muxVal.IsSome then
                                entries <- (msgName, sigName, (muxInd, muxVal)) :: entries

        entries
        |> List.fold (fun acc (m, s, meta) -> acc |> Map.add (m, s) meta) Map.empty

    let private tryBuildSignalMetaMap (filePath: string) : Map<string * string, bool * ByteOrder> =
        let mutable currentMsg: string option = None
        let mutable entries: (string * string * (bool * ByteOrder)) list = []

        for raw in File.ReadLines(filePath) do
            let line = raw.Trim()

            if line.StartsWith("BO_ ") then
                let parts = line.Split([| ' '; ':' |], StringSplitOptions.RemoveEmptyEntries)

                if parts.Length >= 3 then
                    currentMsg <- Some parts.[2]
            elif line.StartsWith("SG_") then
                match currentMsg with
                | None -> ()
                | Some msgName ->
                    let parts = line.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)

                    if parts.Length >= 2 then
                        let sigName = parts.[1]
                        let colonIdx = line.IndexOf(':')

                        if colonIdx > 0 && colonIdx < line.Length - 1 then
                            let after = line.Substring(colonIdx + 1).Trim()
                            let atIdx = after.IndexOf('@')

                            if atIdx >= 0 && atIdx + 2 < after.Length then
                                let endianCh = after.[atIdx + 1]
                                let signCh = after.[atIdx + 2]

                                if (signCh = '+' || signCh = '-') && (endianCh = '0' || endianCh = '1') then
                                    let isSigned = signCh = '-'
                                    let order = if endianCh = '0' then ByteOrder.Big else ByteOrder.Little
                                    entries <- (msgName, sigName, (isSigned, order)) :: entries

        entries
        |> List.fold (fun acc (m, s, meta) -> acc |> Map.add (m, s) meta) Map.empty

    let private buildIdNameMap (filePath: string) : Map<int, string> =
        let mutable m: Map<int, string> = Map.empty

        for raw in File.ReadLines(filePath) do
            let line = raw.Trim()

            if line.StartsWith("BO_ ") then
                let parts = line.Split([| ' '; ':' |], StringSplitOptions.RemoveEmptyEntries)

                if parts.Length >= 3 then
                    match Int32.TryParse(parts.[1]) with
                    | true, id ->
                        let name = parts.[2]

                        if not (isVectorInternalMessageName name) then
                            m <- m |> Map.add id name
                    | _ -> ()

        m

    let private buildExtendedMuxMap (filePath: string) =
        let idNames = buildIdNameMap filePath
        let pattern = Regex(@"^SG_MUL_VAL_\s+(\d+)\s+(\S+)\s+(\S+)\s+(.+);\s*$")
        let singleton = Regex(@"^(\d+)-(\d+)$")
        let mutable parents: Map<string * string, ExtendedMuxParent> = Map.empty
        let mutable error: string option = None

        for raw in File.ReadLines(filePath) do
            let line = raw.Trim()

            if
                error.IsNone
                && line.StartsWith("SG_MUL_VAL_", StringComparison.Ordinal)
                && line <> "SG_MUL_VAL_"
            then
                let matched = pattern.Match(line)

                if not matched.Success then
                    error <- Some(sprintf "Malformed SG_MUL_VAL_ declaration '%s'." line)
                else
                    let idText = matched.Groups.[1].Value
                    let signalName = matched.Groups.[2].Value
                    let selectorName = matched.Groups.[3].Value
                    let predicateText = matched.Groups.[4].Value.Trim()
                    let range = singleton.Match(predicateText)

                    match Int32.TryParse(idText), range.Success with
                    | (true, id), true when idNames.ContainsKey id ->
                        match UInt32.TryParse(range.Groups.[1].Value), UInt32.TryParse(range.Groups.[2].Value) with
                        | (true, first), (true, last) when first = last ->
                            let key = idNames.[id], signalName

                            if parents.ContainsKey key then
                                error <- Some(sprintf "Signal '%s' has duplicate SG_MUL_VAL_ parents." signalName)
                            else
                                parents <-
                                    parents
                                    |> Map.add
                                        key
                                        { SelectorSignalName = selectorName
                                          Expected = first }
                        | (true, _), (true, _) ->
                            error <-
                                Some(
                                    sprintf
                                        "Signal '%s' uses a mux range; exact singleton predicates are required."
                                        signalName
                                )
                        | _ -> error <- Some(sprintf "Signal '%s' has an invalid mux predicate." signalName)
                    | _ -> error <- Some(sprintf "SG_MUL_VAL_ references an unknown message '%s'." idText)

        match error with
        | Some details -> Error details
        | None -> Ok parents

    let private tryBuildValueTableMap (filePath: string) : Map<string * string, (int * string) list> =
        let idName = buildIdNameMap filePath
        let mutable map: Map<string * string, (int * string) list> = Map.empty
        let rx = Regex(@"^VAL_\s+(\d+)\s+(\S+)\s+(.*);\s*$")
        let rxPair = Regex(@"([+-]?\d+)\s+""([^""]*)""")

        for raw in File.ReadLines(filePath) do
            let line = raw.Trim()
            let m = rx.Match(line)

            if m.Success then
                let idStr = m.Groups.[1].Value
                let sigName = m.Groups.[2].Value
                let pairsStr = m.Groups.[3].Value

                match Int32.TryParse(idStr) with
                | true, id when idName.ContainsKey id ->
                    let msgName = idName.[id]

                    let pairs =
                        rxPair.Matches(pairsStr)
                        |> Seq.cast<Match>
                        |> Seq.choose (fun mm ->
                            match Int32.TryParse(mm.Groups.[1].Value) with
                            | true, v -> Some(v, mm.Groups.[2].Value)
                            | _ -> None)
                        |> Seq.toList

                    if pairs.Length > 0 then
                        map <- map |> Map.add (msgName, sigName) pairs
                | _ -> ()

        map

    let private validateExtendedMux (messages: Message list) =
        let validateMessage (message: Message) =
            let byName =
                message.Signals |> List.map (fun signal -> signal.Name, signal) |> Map.ofList

            let isSelector signal =
                signal.MultiplexerIndicator = Some "M"
                || signal.MultiplexerIndicator = Some "mM"

            let rec resolve visited (signal: Signal) =
                if Set.contains signal.Name visited then
                    Error(sprintf "Multiplexer cycle in message '%s'." message.Name)
                else
                    match signal.ExtendedMuxParent with
                    | None ->
                        if signal.MultiplexerIndicator = Some "mM" then
                            Error(sprintf "Nested selector '%s' is missing its parent." signal.Name)
                        else
                            Ok(muxPath message signal)
                    | Some parent when parent.SelectorSignalName = signal.Name ->
                        Error(sprintf "Signal '%s' cannot multiplex itself." signal.Name)
                    | Some parent ->
                        match byName |> Map.tryFind parent.SelectorSignalName with
                        | None ->
                            Error(sprintf "Signal '%s' has missing parent '%s'." signal.Name parent.SelectorSignalName)
                        | Some selector when not (isSelector selector) ->
                            Error(sprintf "Signal '%s' parent '%s' is not a selector." signal.Name selector.Name)
                        | Some selector when
                            selector.IsSigned
                            || selector.Length < 1us
                            || selector.Length > 32us
                            || selector.Factor <> 1.0
                            || selector.Offset <> 0.0
                            ->
                            Error(
                                sprintf
                                    "Mux selector '%s' must be unsigned 1..32 bits with identity scaling."
                                    selector.Name
                            )
                        | Some selector ->
                            let maximum =
                                if selector.Length = 32us then
                                    uint64 UInt32.MaxValue
                                else
                                    (1UL <<< int selector.Length) - 1UL

                            if uint64 parent.Expected > maximum then
                                Error(sprintf "Mux predicate for '%s' exceeds selector width." signal.Name)
                            elif
                                signal.MultiplexerSwitchValue
                                |> Option.exists (fun expected -> expected < 0 || uint32 expected <> parent.Expected)
                            then
                                Error(sprintf "Mux declaration for '%s' disagrees with SG_MUL_VAL_." signal.Name)
                            else
                                match resolve (Set.add signal.Name visited) selector with
                                | Error details -> Error details
                                | Ok prefix -> Ok(prefix @ [ selector.Name, parent.Expected ])

            message.Signals
            |> List.tryPick (fun signal ->
                match resolve Set.empty signal with
                | Error details -> Some details
                | Ok path when path.Length > 4 ->
                    Some(sprintf "Signal '%s' mux path exceeds maximum depth 4." signal.Name)
                | Ok _ -> None)

        messages |> List.tryPick validateMessage

    /// Parse DBC file into Core IR with validation
    let parseDbcFile (filePath: string) : Result<Ir, ParseError> =
        try
            match validateDuplicateIdsFromText filePath, buildExtendedMuxMap filePath with
            | Some err, _ -> Error(ParseError.InvalidDbc err)
            | None, Error err -> Error(ParseError.InvalidDbc err)
            | None, Ok extendedMuxMap ->
                let metaMap = tryBuildSignalMetaMap filePath
                let muxMap = tryBuildSignalMuxMap filePath
                let valMap = tryBuildValueTableMap filePath
                let dbc = DbcParserLib.Parser.ParseFromPath(filePath)

                let messages =
                    dbc.Messages
                    |> Seq.filter (fun msg -> not (isVectorInternalMessageName msg.Name))
                    |> Seq.map (fun msg ->
                        let signals =
                            msg.Signals
                            |> Seq.map (fun s ->
                                let minVal = if Double.IsNaN s.Minimum then None else Some s.Minimum
                                let maxVal = if Double.IsNaN s.Maximum then None else Some s.Maximum

                                let inferredSigned, inferredOrder =
                                    match metaMap |> Map.tryFind (msg.Name, s.Name) with
                                    | Some(isS, ord) -> isS, ord
                                    | None ->
                                        let byteOrder =
                                            if s.ByteOrder = 0uy then
                                                ByteOrder.Big
                                            else
                                                ByteOrder.Little

                                        (s.Minimum < 0.0), byteOrder

                                let muxInd, muxVal =
                                    match muxMap |> Map.tryFind (msg.Name, s.Name) with
                                    | Some(i, v) -> i, v
                                    | None -> None, None

                                { Name = s.Name
                                  StartBit = s.StartBit
                                  Length = s.Length
                                  Factor = s.Factor
                                  Offset = s.Offset
                                  Minimum = minVal
                                  Maximum = maxVal
                                  Unit = s.Unit
                                  IsSigned = inferredSigned
                                  IsCrc =
                                    s.Name.ToLowerInvariant().Contains("crc")
                                    || s.Name.ToLowerInvariant().Contains("checksum")
                                  IsCounter =
                                    s.Name.ToLowerInvariant().Contains("counter")
                                    || s.Name.ToLowerInvariant().Contains("alive")
                                  ByteOrder = inferredOrder
                                  MultiplexerIndicator = muxInd
                                  MultiplexerSwitchValue = muxVal
                                  ExtendedMuxParent = extendedMuxMap |> Map.tryFind (msg.Name, s.Name)
                                  ValueTable = (valMap |> Map.tryFind (msg.Name, s.Name))
                                  Receivers = []
                                  CrcMeta = None
                                  CounterMeta = None })
                            |> List.ofSeq

                        { Name = msg.Name
                          Id = msg.ID
                          IsExtended = (msg.ID > 0x7FFu)
                          Length = msg.DLC
                          Signals = signals
                          Sender = msg.Transmitter
                          Receivers = []
                          CrcCounterMode = None })
                    |> List.ofSeq

                let validateMuxStructure (msgs: Message list) : string option =
                    let perMessage (m: Message) : string option =
                        let switches = m.Signals |> List.filter (fun s -> s.MultiplexerIndicator = Some "M")

                        if switches.Length > 1 then
                            Some(sprintf "Multiple multiplexer switch signals found in message '%s'." m.Name)
                        else
                            let malformed =
                                m.Signals
                                |> List.tryFind (fun s ->
                                    (s.MultiplexerIndicator = Some "m" || s.MultiplexerIndicator = Some "mM")
                                    && s.MultiplexerSwitchValue.IsNone)

                            match malformed with
                            | Some s ->
                                Some(
                                    sprintf
                                        "Multiplexed signal '%s' in message '%s' is missing a switch value (m<k>)."
                                        s.Name
                                        m.Name
                                )
                            | None -> None

                    msgs |> List.tryPick perMessage

                let combineValidators validators = validators |> List.tryPick id

                match
                    combineValidators
                        [ validateDuplicates messages
                          validateMuxStructure messages
                          validateExtendedMux messages
                          validateOverlaps messages
                          validateExceedsDlc messages ]
                with
                | Some err -> Error(ParseError.InvalidDbc err)
                | None -> Ok { Messages = messages }
        with ex ->
            Error(ParseError.IoError ex.Message)

    let applyConfigMetadata (configOpt: Config.Config option) (ir: Ir) : Ir =
        let mapMode (mode: string) =
            match mode with
            | "validate" -> Some CrcCounterMode.Validate
            | "passthrough" -> Some CrcCounterMode.Passthrough
            | "fail_fast" -> Some CrcCounterMode.FailFast
            | _ -> None

        let tryResolveCrcParams (crcCfg: Config.CrcCounterConfig) (algorithm: string) =
            match algorithm with
            | "CRC8_SAE_J1850" ->
                Some
                    { Width = 8
                      Poly = 0x1DUL
                      Init = 0xFFUL
                      XorOut = 0xFFUL
                      ReflectIn = false
                      ReflectOut = false }
            | "CRC8_8H2F" ->
                Some
                    { Width = 8
                      Poly = 0x2FUL
                      Init = 0xFFUL
                      XorOut = 0xFFUL
                      ReflectIn = false
                      ReflectOut = false }
            | _ ->
                crcCfg.CustomAlgorithms
                |> Option.bind (Map.tryFind algorithm)
                |> Option.map (fun custom ->
                    { Width = custom.Width
                      Poly = custom.Poly
                      Init = custom.Init
                      XorOut = custom.XorOut
                      ReflectIn = custom.ReflectIn
                      ReflectOut = custom.ReflectOut })

        let mapAlgorithmId (algorithm: string) =
            match algorithm with
            | "CRC8_SAE_J1850" -> CrcAlgorithmId.CRC8_SAE_J1850
            | "CRC8_8H2F" -> CrcAlgorithmId.CRC8_8H2F
            | "CRC16_CCITT" -> CrcAlgorithmId.CRC16_CCITT
            | "CRC32P4" -> CrcAlgorithmId.CRC32P4
            | _ -> CrcAlgorithmId.Custom algorithm

        let enrichSignal (crcCfg: Config.CrcCounterConfig) (msgCfg: Config.CrcCounterMessageConfig) (signal: Signal) =
            let signalWithCrc =
                match msgCfg.Crc with
                | Some crcSig when signal.Name = crcSig.Signal ->
                    match tryResolveCrcParams crcCfg crcSig.Algorithm with
                    | Some parameters ->
                        { signal with
                            CrcMeta =
                                Some
                                    { Algorithm = mapAlgorithmId crcSig.Algorithm
                                      Params = parameters
                                      ByteRange =
                                        {| Start = fst crcSig.ByteRange
                                           End = snd crcSig.ByteRange |}
                                      DataId = crcSig.DataId } }
                    | None -> signal
                | _ -> signal

            match msgCfg.Counter with
            | Some counterSig when signal.Name = counterSig.Signal ->
                { signalWithCrc with
                    CounterMeta =
                        Some
                            { Modulus = counterSig.Modulus
                              Increment = counterSig.Increment } }
            | _ -> signalWithCrc

        match configOpt |> Option.bind (fun config -> config.CrcCounter) with
        | None -> ir
        | Some crcCfg ->
            { ir with
                Messages =
                    ir.Messages
                    |> List.map (fun msg ->
                        match crcCfg.Messages |> Map.tryFind msg.Name with
                        | None -> msg
                        | Some msgCfg ->
                            { msg with
                                Signals = msg.Signals |> List.map (enrichSignal crcCfg msgCfg)
                                CrcCounterMode = mapMode crcCfg.Mode }) }
