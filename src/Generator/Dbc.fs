namespace Generator

open Generator.Ir
open System
open System.IO
open System.Text.RegularExpressions

module Dbc =

    let private isVectorInternalMessageName (name: string) =
        // Known Vector internal/auxiliary message to ignore
        name = "VECTOR__INDEPENDENT_SIG_MSG"

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
                while curBit < 0 do curBit <- curBit + 8; curByte <- curByte + 1
                curByte * 8 + curBit ]

    let private validateDuplicates (messages: Message list) : string option =
        messages
        |> List.groupBy (fun m -> m.Id)
        |> List.tryPick (fun (id, ms) -> if List.length ms > 1 then Some (sprintf "Duplicate message ID %u found." id) else None)

    // Determine whether two signals can coexist in the same frame instance.
    // Overlap is only an error if they can coexist.
    let private canCoexist (a: Signal) (b: Signal) : bool =
        let aMuxI, aMuxV = a.MultiplexerIndicator, a.MultiplexerSwitchValue
        let bMuxI, bMuxV = b.MultiplexerIndicator, b.MultiplexerSwitchValue
        match aMuxI, aMuxV, bMuxI, bMuxV with
        // Both are m<val> but different values => do not coexist
        | Some indA, Some va, Some indB, Some vb when indA = "m" && indB = "m" && va <> vb -> false
        // Otherwise, they can (base with anyone, same m value, M with anyone)
        | _ -> true

    let private validateOverlaps (messages: Message list) : string option =
        let overlapsInMessage (m: Message) : string option =
            let rec checkPairs (signals: Signal list) : string option =
                match signals with
                | [] | [_] -> None
                | s::rest ->
                    let sBits = coveredBits s |> Set.ofList
                    let conflict =
                        rest
                        |> List.tryPick (fun t ->
                            if canCoexist s t then
                                let tBits = coveredBits t |> Set.ofList
                                let inter = Set.intersect sBits tBits
                                if not (Set.isEmpty inter) then Some (sprintf "Signal '%s' in message '%s' overlaps with other signals." t.Name m.Name) else None
                            else None)
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
                    Some (sprintf "Signal '%s' in message '%s' exceeds the message DLC of %d bytes." s.Name m.Name (int m.Length))
                else None)
        messages |> List.tryPick exceedInMessage

    let private validateDuplicateIdsFromText (filePath: string) : string option =
        try
            let lines = File.ReadAllLines(filePath)
            let ids =
                lines
                |> Seq.choose (fun line ->
                    let t = line.Trim()
                    if t.StartsWith("BO_ ") then
                        // BO_ <id> <name>: <dlc> <transmitter>
                        let parts = t.Split([|' '; ':'|], StringSplitOptions.RemoveEmptyEntries)
                        if parts.Length >= 3 then
                            let name = parts.[2]
                            if isVectorInternalMessageName name then None else
                            match Int32.TryParse(parts.[1]) with
                            | true, id -> Some id
                            | _ -> None
                        else None
                    else None)
                |> Seq.toList
            ids
            |> List.groupBy id
            |> List.tryPick (fun (id, xs) -> if List.length xs > 1 then Some (sprintf "Duplicate message ID %d found." id) else None)
        with _ -> None

    // Map of (messageName, signalName) -> (muxIndicator, muxValue)
    let private tryBuildSignalMuxMap (filePath: string) : Map<string * string, string option * int option> =
        let mutable currentMsg : string option = None
        let mutable entries : (string*string*(string option * int option)) list = []
        try
            for raw in File.ReadLines(filePath) do
                let line = raw.Trim()
                if line.StartsWith("BO_ ") then
                    let parts = line.Split([|' '; ':'|], StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length >= 3 then currentMsg <- Some parts.[2]
                elif line.StartsWith("SG_") then
                    match currentMsg with
                    | None -> ()
                    | Some msgName ->
                        // Extract token(s) between the signal name and ':'
                        let colonIdx = line.IndexOf(':')
                        if colonIdx > 0 then
                            let left = line.Substring(0, colonIdx)
                            // left looks like: SG_ <sigName> [M|m<digits>] (optional)
                            let parts = left.Split([|' '|], StringSplitOptions.RemoveEmptyEntries)
                            if parts.Length >= 2 then
                                let sigName = parts.[1]
                                // Search tokens after sigName for M or m<digits>
                                let tokens = parts |> Array.skip 2
                                let mutable muxInd : string option = None
                                let mutable muxVal : int option = None
                                for t in tokens do
                                    if t = "M" then
                                        muxInd <- Some "M"
                                    elif t.Length >= 1 && t.[0] = 'm' then
                                        // Mark as multiplexed; if no digits follow or parse fails, leave value as None (malformed)
                                        muxInd <- Some "m"
                                        if t.Length > 1 then
                                            let vStr = t.Substring(1)
                                            match Int32.TryParse(vStr) with
                                            | true, v -> muxVal <- Some v
                                            | _ -> ()
                                if muxInd.IsSome || muxVal.IsSome then
                                    entries <- (msgName, sigName, (muxInd, muxVal)) :: entries
            entries |> List.fold (fun acc (m,s,meta) -> acc |> Map.add (m,s) meta) Map.empty
        with _ -> Map.empty

    // Map of (messageName, signalName) -> (isSigned, byteOrder)
    let private tryBuildSignalMetaMap (filePath: string) : Map<string * string, bool * ByteOrder> =
        let mutable currentMsg : string option = None
        let mutable entries : (string*string*(bool*ByteOrder)) list = []
        try
            for raw in File.ReadLines(filePath) do
                let line = raw.Trim()
                if line.StartsWith("BO_ ") then
                    let parts = line.Split([|' '; ':'|], StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length >= 3 then currentMsg <- Some parts.[2]
                elif line.StartsWith("SG_") then
                    match currentMsg with
                    | None -> ()
                    | Some msgName ->
                        let parts = line.Split([|' '|], StringSplitOptions.RemoveEmptyEntries)
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
            entries |> List.fold (fun acc (m,s,meta) -> acc |> Map.add (m,s) meta) Map.empty
        with _ -> Map.empty

    // Build BO_ id -> message name map
    let private buildIdNameMap (filePath: string) : Map<int, string> =
        let mutable m : Map<int,string> = Map.empty
        try
            for raw in File.ReadLines(filePath) do
                let line = raw.Trim()
                if line.StartsWith("BO_ ") then
                    let parts = line.Split([|' '; ':'|], StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length >= 3 then
                        match Int32.TryParse(parts.[1]) with
                        | true, id ->
                            let name = parts.[2]
                            if not (isVectorInternalMessageName name) then
                                m <- m |> Map.add id name
                        | _ -> ()
            m
        with _ -> Map.empty

    // Map of (messageName, signalName) -> value table entries
    let private tryBuildValueTableMap (filePath: string) : Map<string * string, (int * string) list> =
        try
            let idName = buildIdNameMap filePath
            let mutable map : Map<string*string,(int*string) list> = Map.empty
            let rx = Regex(@"^VAL_\s+(\d+)\s+(\S+)\s+(.*);\s*$")
            // ([+-]?digits) whitespace "name" pairs; use verbatim string and double quotes inside
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
                                | true, v -> Some (v, mm.Groups.[2].Value)
                                | _ -> None)
                            |> Seq.toList
                        if pairs.Length > 0 then
                            map <- map |> Map.add (msgName, sigName) pairs
                    | _ -> ()
            map
        with _ -> Map.empty

    let parseDbcFile (filePath: string) : Result<Ir, string list> =
        // Pre-parse validation for duplicate IDs based on raw text
        match validateDuplicateIdsFromText filePath with
        | Some err -> Error [err]
        | None ->
            try
                printfn "Attempting to parse DBC file: %s" filePath
                let metaMap = tryBuildSignalMetaMap filePath
                let muxMap = tryBuildSignalMuxMap filePath
                let valMap = tryBuildValueTableMap filePath
                let dbc = DbcParserLib.Parser.ParseFromPath(filePath)
                printfn "DBC object parsed. Messages count: %d" (Seq.length dbc.Messages)

                let messages =
                    dbc.Messages
                    |> Seq.filter (fun msg ->
                        if isVectorInternalMessageName msg.Name then
                            printfn "Skipping Vector internal message: %s" msg.Name
                            false
                        else true)
                    |> Seq.map (fun msg ->
                        // Map signals
                        let signals =
                            msg.Signals
                            |> Seq.map (fun s ->
                                let minVal = if Double.IsNaN s.Minimum then None else Some s.Minimum
                                let maxVal = if Double.IsNaN s.Maximum then None else Some s.Maximum
                                let inferredSigned, inferredOrder =
                                    match metaMap |> Map.tryFind (msg.Name, s.Name) with
                                    | Some (isS, ord) -> isS, ord
                                    | None -> (s.Minimum < 0.0), ByteOrder.Little
                                let muxInd, muxVal =
                                    match muxMap |> Map.tryFind (msg.Name, s.Name) with
                                    | Some (i, v) -> i, v
                                    | None -> None, None
                                {
                                    Name = s.Name
                                    StartBit = s.StartBit
                                    Length = s.Length
                                    Factor = s.Factor
                                    Offset = s.Offset
                                    Minimum = minVal
                                    Maximum = maxVal
                                    Unit = s.Unit
                                    IsSigned = inferredSigned
                                    IsCrc = s.Name.ToLowerInvariant().Contains("crc") || s.Name.ToLowerInvariant().Contains("checksum")
                                    IsCounter = s.Name.ToLowerInvariant().Contains("counter") || s.Name.ToLowerInvariant().Contains("alive")
                                    ByteOrder = inferredOrder
                                    MultiplexerIndicator = muxInd
                                    MultiplexerSwitchValue = muxVal
                                    ValueTable = (valMap |> Map.tryFind (msg.Name, s.Name))
                                    Receivers = []
                                }
                            )
                            |> List.ofSeq

                        {
                            Name = msg.Name
                            Id = msg.ID
                            IsExtended = (msg.ID > 0x7FFu)
                            Length = msg.DLC
                            Signals = signals
                            Sender = msg.Transmitter
                            Receivers = []
                        }
                    )
                    |> List.ofSeq

                printfn "Total messages mapped to IR: %d" messages.Length

                // Multiplexer structural validation: at most one M per message, and m entries well-formed
                let validateMuxStructure (messages: Message list) : string option =
                    let perMessage (m: Message) : string option =
                        let switches = m.Signals |> List.filter (fun s -> s.MultiplexerIndicator = Some "M")
                        if switches.Length > 1 then
                            Some (sprintf "Multiple multiplexer switch signals found in message '%s'." m.Name)
                        else
                            // Ensure any 'm' entry has a value (defensive)
                            let malformed =
                                m.Signals
                                |> List.tryFind (fun s -> s.MultiplexerIndicator = Some "m" && s.MultiplexerSwitchValue.IsNone)
                            match malformed with
                            | Some s -> Some (sprintf "Multiplexed signal '%s' in message '%s' is missing a switch value (m<k>)." s.Name m.Name)
                            | None -> None
                    messages |> List.tryPick perMessage

                // Validations
                match validateDuplicates messages with
                | Some err -> Error [err]
                | None ->
                    match validateOverlaps messages with
                    | Some err -> Error [err]
                    | None ->
                        match validateMuxStructure messages with
                        | Some err -> Error [err]
                        | None ->
                            match validateExceedsDlc messages with
                            | Some err -> Error [err]
                            | None -> Ok { Messages = messages }
            with
            | ex ->
                Error [ sprintf "Error parsing DBC file: %s" ex.Message ]
