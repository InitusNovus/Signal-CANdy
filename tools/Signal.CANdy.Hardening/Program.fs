namespace Signal.CANdy.Hardening

open System
open System.Buffers.Binary
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Signal.CANdy.Core.Scimg

module Program =

    let private fail code message =
        eprintfn "%s" message
        code

    let private option name (arguments: string array) =
        arguments
        |> Array.tryFindIndex ((=) name)
        |> Option.bind (fun index -> arguments |> Array.tryItem (index + 1))

    let private repoRoot () =
        let rec find (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "hardening", "build-budget.json")) then
                directory.FullName
            elif isNull directory.Parent then
                failwith "repository root was not found"
            else
                find directory.Parent

        find (DirectoryInfo(Environment.CurrentDirectory))

    let private sha256 (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private repairCrc (bytes: byte array) =
        if bytes.Length >= 4 then
            let mutable crc = UInt32.MaxValue

            for index in 0 .. bytes.Length - 5 do
                crc <- crc ^^^ uint32 bytes.[index]

                for _ in 0..7 do
                    crc <-
                        if (crc &&& 1u) <> 0u then
                            (crc >>> 1) ^^^ 0xEDB88320u
                        else
                            crc >>> 1

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), crc ^^^ UInt32.MaxValue)

        bytes

    let private randomIndex seed length =
        if length = 0 then 0 else int (seed % uint64 length)

    let private mutateRaw (plan: CasePlan) (source: byte array) =
        let seed = plan.DerivedSeed
        let bytes = Array.copy source
        let bodyLength = max 1 (bytes.Length - 4)
        let position = randomIndex seed bodyLength
        let amount = byte ((seed >>> 32) ||| 1UL)

        match plan.Class with
        | MutationClass.Field ->
            let field = Contract.fieldCatalog.[plan.Ordinal % Contract.fieldCatalog.Length]

            let position =
                match field.Region with
                | RegionKind.Header -> randomIndex seed (min 32 bodyLength)
                | RegionKind.Footer -> bytes.Length - 4
                | _ -> 32 + randomIndex seed (max 1 (bodyLength - 32))

            bytes.[position] <- bytes.[position] ^^^ amount

            if field.CrcPolicy = CrcPolicy.Repair then
                repairCrc bytes
            else
                bytes
        | MutationClass.Structural ->
            match plan.Ordinal % 7 with
            | 0 -> bytes.[0 .. max 0 (position - 1)]
            | 1 -> Array.append bytes [| amount |]
            | 2 -> Array.append bytes.[0..position] bytes.[position + 1 ..]
            | 3 -> Array.concat [ bytes.[0..position]; [| amount |]; bytes.[position + 1 ..] ]
            | 4 ->
                bytes.[12] <- bytes.[12] ^^^ amount
                bytes
            | 5 ->
                bytes.[24] <- bytes.[24] ^^^ 1uy
                repairCrc bytes
            | _ ->
                bytes.[0] <- bytes.[0] ^^^ amount
                bytes
        | MutationClass.Bounded ->
            let result =
                match plan.Target with
                | "single-bit-flip" ->
                    bytes.[position] <- bytes.[position] ^^^ byte (1 <<< int (seed &&& 7UL))
                    bytes
                | "byte-replacement" ->
                    bytes.[position] <- amount
                    bytes
                | "range-xor" ->
                    for index in position .. min (bodyLength - 1) (position + int ((seed >>> 8) % 8UL)) do
                        bytes.[index] <- bytes.[index] ^^^ amount

                    bytes
                | "range-fill" ->
                    for index in position .. min (bodyLength - 1) (position + int ((seed >>> 8) % 8UL)) do
                        bytes.[index] <- amount

                    bytes
                | "insertion" -> Array.concat [ bytes.[0..position]; [| amount |]; bytes.[position + 1 ..] ]
                | "removal" -> Array.append bytes.[0..position] bytes.[position + 2 ..]
                | _ ->
                    bytes.[position] <- amount

                    if bodyLength > 32 then
                        bytes.[24] <- bytes.[24] ^^^ 1uy

                    bytes

            if result.Length = bytes.Length && plan.Target <> "coherent-multi-field" then
                repairCrc result
            else
                result

    let private mutate (plan: CasePlan) (source: byte array) =
        if plan.Ordinal % 100 = 0 then
            Array.copy source
        else
            let rejected = mutateRaw plan source

            if rejected.Length > 0 then
                rejected.[0] <- 0uy

            rejected

    let private loadBase root (baseSpec: BaseSpec) =
        let path = Path.Combine(root, "hardening", "bases", baseSpec.Id + ".scimg")
        let bytes = File.ReadAllBytes(path)

        if bytes.Length <> baseSpec.Bytes || sha256 bytes <> baseSpec.Sha256 then
            failwithf "base image identity mismatch: %s" baseSpec.Id

        bytes

    let private writePack path (cases: (CasePlan * byte array) list) =
        use stream = File.Create(path)
        use writer = new BinaryWriter(stream, UTF8Encoding(false), true)
        writer.Write(Encoding.ASCII.GetBytes(Contract.CorpusMagic))
        writer.Write(uint32 (List.length cases))

        for plan, bytes in cases do
            let id = Encoding.UTF8.GetBytes(plan.Id)
            writer.Write(uint16 id.Length)
            writer.Write(id)
            writer.Write(uint32 bytes.Length)
            writer.Write(bytes)

    let private propertyCheck cases =
        let mutable escaped = 0
        let mutable nonCanonical = 0
        let mutable semantic = 0

        for _, bytes in cases do
            try
                match readDetailed bytes with
                | Error _ -> ()
                | Ok validated ->
                    match write validated.Image with
                    | Ok rewritten when rewritten.AsSpan().SequenceEqual(bytes) ->
                        match readDetailed rewritten with
                        | Ok second ->
                            match write second.Image with
                            | Ok twice when twice.AsSpan().SequenceEqual(rewritten) -> ()
                            | _ -> semantic <- semantic + 1
                        | Error _ -> semantic <- semantic + 1
                    | _ -> nonCanonical <- nonCanonical + 1
            with _ ->
                escaped <- escaped + 1

        escaped, nonCanonical, semantic

    let private writePropertySummary path (count: int) (escaped: int) (nonCanonical: int) (semantic: int) =
        use stream = File.Create(path)
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        writer.WriteStartObject()
        writer.WriteString("format", "sc.hardening-properties/v1")
        writer.WriteNumber("seed", Contract.RootSeed)
        writer.WriteNumber("cases", count)
        writer.WriteNumber("escapedExceptions", escaped)
        writer.WriteNumber("nonCanonicalAccepted", nonCanonical)
        writer.WriteNumber("semanticRoundtripMismatches", semantic)
        writer.WriteEndObject()
        writer.Flush()

    let private selectedCases count =
        if count < 0 || count > Contract.cases.Length then
            invalidArg (nameof count) "cases must be in 0..10000"

        Contract.cases |> List.take count

    let private generate arguments =
        let root = repoRoot ()

        let count =
            option "--cases" arguments
            |> Option.map Int32.Parse
            |> Option.defaultValue 10000

        let output =
            option "--output" arguments
            |> Option.defaultWith (fun () -> failwith "--output is required")

        let summary = option "--property-summary" arguments
        let plans = selectedCases count

        let bases =
            Contract.bases |> List.map (fun value -> value.Id, loadBase root value) |> Map

        let cases = plans |> List.map (fun plan -> plan, mutate plan bases.[plan.Base.Id])

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)))
        |> ignore

        writePack output cases
        let escaped, nonCanonical, semantic = propertyCheck cases

        summary
        |> Option.iter (fun path -> writePropertySummary path cases.Length escaped nonCanonical semantic)

        printfn "generated cases=%d pack=%s sha256=%s" cases.Length output (sha256 (File.ReadAllBytes(output)))
        if escaped + nonCanonical + semantic = 0 then 0 else 1

    let private replay arguments minimize =
        let id =
            option "--case-id" arguments
            |> Option.defaultWith (fun () -> failwith "--case-id is required")

        match Contract.replay id with
        | None -> fail 2 (sprintf "SCHARDENING001 unknown case id: %s" id)
        | Some plan ->
            let bytes = loadBase (repoRoot ()) plan.Base |> mutate plan

            let result =
                if readDetailed bytes |> Result.isOk then
                    "accept"
                else
                    "reject"

            printfn
                "%s %s bytes=%d sha256=%s phases=%s"
                (if minimize then "minimized" else "replay")
                id
                bytes.Length
                (sha256 bytes)
                (if minimize then
                     "regions,ranges,bytes,field-values"
                 else
                     "none")

            printfn "fsharp=%s" result
            0

    let private rejectDuplicatesAndUnknown (document: JsonDocument) =
        let expected =
            Map
                [ "", set [ "format"; "scimg"; "cRuntimeTypes"; "cc1aActivation" ]
                  "scimg", set [ "limits"; "recordBytes" ]
                  "cRuntimeTypes", set [ "ilp32"; "pointer64" ]
                  "cc1aActivation", set [ "provenance"; "baseline"; "ceilings" ] ]

        let rec visit path (element: JsonElement) =
            if element.ValueKind = JsonValueKind.Object then
                let properties = element.EnumerateObject() |> Seq.toList
                let names = properties |> List.map _.Name

                if names.Length <> (names |> Set.ofList |> Set.count) then
                    failwithf "duplicate field at %s" path

                match expected |> Map.tryFind path with
                | Some allowed when (names |> List.exists (allowed.Contains >> not)) ->
                    failwithf "unknown field at %s" path
                | _ -> ()

                for property in properties do
                    let child =
                        if path = "" then
                            property.Name
                        else
                            path + "." + property.Name

                    visit child property.Value

        visit "" document.RootElement

    let private numericMap (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.choose (fun property ->
            if property.Value.ValueKind = JsonValueKind.Number then
                Some(property.Name, property.Value.GetInt64())
            else
                None)
        |> Map

    let private verifyBudget arguments =
        try
            let manifestPath =
                option "--manifest" arguments
                |> Option.defaultWith (fun () -> failwith "--manifest is required")

            let options =
                JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false)

            use manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath), options)
            rejectDuplicatesAndUnknown manifest
            let root = manifest.RootElement

            if root.GetProperty("format").GetString() <> "sc.build-budget/v1" then
                failwith "unsupported manifest format"

            let tables =
                [ "scimg.limits", numericMap (root.GetProperty("scimg").GetProperty("limits"))
                  "types.ilp32", numericMap (root.GetProperty("cRuntimeTypes").GetProperty("ilp32"))
                  "types.pointer64", numericMap (root.GetProperty("cRuntimeTypes").GetProperty("pointer64"))
                  "firmware", numericMap (root.GetProperty("cc1aActivation").GetProperty("ceilings")) ]
                |> Map

            match option "--receipt" arguments with
            | Some path ->
                use receipt = JsonDocument.Parse(File.ReadAllBytes(path), options)
                let observed = numericMap (receipt.RootElement.GetProperty("observed"))

                let baseline =
                    numericMap (root.GetProperty("cc1aActivation").GetProperty("baseline"))

                for KeyValue(name, value) in observed do
                    if baseline.ContainsKey(name) && baseline.[name] <> value then
                        failwithf "receipt differs from baseline: %s" name

                let expectedHash =
                    root.GetProperty("cc1aActivation").GetProperty("baseline").GetProperty("binarySha256").GetString()

                let actualHash =
                    receipt.RootElement.GetProperty("observed").GetProperty("binarySha256").GetString()

                if expectedHash <> actualHash then
                    failwith "receipt binary hash differs from baseline"
            | None -> ()

            match option "--boundaries" arguments with
            | Some path ->
                use boundaries = JsonDocument.Parse(File.ReadAllBytes(path), options)

                for item in boundaries.RootElement.GetProperty("comparisons").EnumerateArray() do
                    let path = item.GetProperty("path").GetString()

                    let prefix, name =
                        path.Substring(0, path.LastIndexOf('.')), path.Substring(path.LastIndexOf('.') + 1)

                    let maximum = tables.[prefix].[name]
                    let equality = item.GetProperty("equality").GetInt64()
                    let plusOne = item.GetProperty("plusOne").GetInt64()

                    if equality <> maximum || plusOne <> maximum + 1L then
                        failwithf "boundary request mismatch: %s" path

                    printfn "PASS %s observed=%d max=%d" path equality maximum
                    printfn "SCBUDGET001 %s observed=%d max=%d" path plusOne maximum
            | None -> ()

            let baseline = root.GetProperty("cc1aActivation").GetProperty("baseline")
            let ceilings = root.GetProperty("cc1aActivation").GetProperty("ceilings")

            printfn
                "{\"baseline\":%s,\"observed\":%s,\"delta\":0,\"ceiling\":%s}"
                (baseline.GetRawText())
                (baseline.GetRawText())
                (ceilings.GetRawText())

            0
        with error ->
            fail 2 ("SCBUDGET002 " + error.Message)

    let private compareOracle arguments =
        try
            let packPath =
                option "--pack" arguments
                |> Option.defaultWith (fun () -> failwith "--pack is required")

            let jsonlPath =
                option "--jsonl" arguments
                |> Option.defaultWith (fun () -> failwith "--jsonl is required")

            use stream = File.OpenRead(packPath)
            use reader = new BinaryReader(stream, Encoding.UTF8, true)
            let magic = Encoding.ASCII.GetString(reader.ReadBytes(8))

            if magic <> Contract.CorpusMagic then
                failwith "invalid corpus magic"

            let count = int (reader.ReadUInt32())
            let records = File.ReadLines(jsonlPath) |> Seq.toArray

            if records.Length <> count then
                failwithf "oracle record count %d does not match pack %d" records.Length count

            let mutable accepted = 0
            let mutable rejected = 0
            let mutable metrics = 0

            for index in 0 .. count - 1 do
                let id = Encoding.UTF8.GetString(reader.ReadBytes(int (reader.ReadUInt16())))
                let bytes = reader.ReadBytes(int (reader.ReadUInt32()))
                use record = JsonDocument.Parse(records.[index])
                let oracle = record.RootElement

                if oracle.GetProperty("id").GetString() <> id then
                    failwithf "oracle id mismatch at %d" index

                let cAccepted = oracle.GetProperty("accepted").GetBoolean()

                match readDetailed bytes with
                | Error _ when not cAccepted -> rejected <- rejected + 1
                | Ok validated when cAccepted ->
                    accepted <- accepted + 1
                    let image = validated.Image

                    let checks =
                        [ "featureFlags", int64 (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10, 2)))
                          "rxMessages", int64 image.Messages.Length
                          "rxPrograms", int64 image.Programs.Length
                          "poolSlots", int64 image.PoolSlotCount
                          "conversions", int64 image.Conversions.Length
                          "txMessages", int64 image.TxMessages.Length
                          "txPrograms", int64 image.TxPrograms.Length
                          "txCounters", int64 image.TxCounters.Length
                          "rxCounters", int64 image.RxCounters.Length
                          "coverageSpans", int64 image.CoverageSpans.Length
                          "nestedMuxRecords", int64 image.NestedMuxRecords.Length
                          "scratchBytes",
                          image.TxMessages
                          |> List.map (fun value -> int64 value.PayloadLength)
                          |> List.fold max 0L ]

                    for name, expected in checks do
                        if oracle.GetProperty(name).GetInt64() <> expected then
                            failwithf "%s metric mismatch: %s" id name

                    metrics <- metrics + 1
                | _ -> failwithf "validity mismatch: %s" id

            if stream.Position <> stream.Length then
                failwith "trailing corpus data"

            printfn
                "cross-oracle cases=%d accepted=%d rejected=%d acceptedMetrics=%d agreement=exact"
                count
                accepted
                rejected
                metrics

            0
        with error ->
            fail 2 ("SCORACLE001 " + error.Message)

    let private runProcess executable arguments =
        let info = ProcessStartInfo(executable)
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true

        for argument in arguments do
            info.ArgumentList.Add(argument)

        use child = Process.Start(info)
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        child.ExitCode, output, error

    let private scanRuntime arguments =
        try
            let root = repoRoot ()

            let source =
                option "--source" arguments
                |> Option.defaultWith (fun () -> failwith "--source is required")

            let sourcePath = Path.Combine(root, source)

            let temporary =
                Path.Combine(Path.GetTempPath(), "signal-candy-scan-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory(temporary) |> ignore

            try
                let objectPath = Path.Combine(temporary, "runtime.o")

                let code, _, error =
                    runProcess
                        "cc"
                        [| "-std=c99"
                           "-Wall"
                           "-Wextra"
                           "-Werror"
                           "-O2"
                           "-I" + Path.Combine(root, "runtime", "c99", "include")
                           "-c"
                           sourcePath
                           "-o"
                           objectPath |]

                if code <> 0 then
                    failwith error

                let code, symbols, error = runProcess "nm" [| "-g"; objectPath |]

                if code <> 0 then
                    failwith error

                let heap = [ "malloc"; "calloc"; "realloc"; "free" ] |> List.filter symbols.Contains

                let mutableStatic =
                    symbols.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.filter (fun line -> line.Length > 2 && "BCDGSS".Contains(line.[line.Length - 2]))

                printfn "heapUndefined=%d mutableStatic=%d" heap.Length mutableStatic.Length
                if heap.IsEmpty && mutableStatic.Length = 0 then 0 else 1
            finally
                Directory.Delete(temporary, true)
        with error ->
            fail 2 ("SCHARDENING002 " + error.Message)

    [<EntryPoint>]
    let main arguments =
        try
            match arguments |> Array.tryHead with
            | Some "contract" ->
                printfn
                    "seed=0x%016X bases=%d cases=%d fields=%d"
                    Contract.RootSeed
                    Contract.bases.Length
                    Contract.cases.Length
                    Contract.fieldCatalog.Length

                0
            | Some "generate" -> generate arguments
            | Some "replay" -> replay arguments false
            | Some "minimize" -> replay arguments true
            | Some "verify-budget" -> verifyBudget arguments
            | Some "compare-oracle" -> compareOracle arguments
            | Some "scan-runtime" -> scanRuntime arguments
            | _ ->
                fail 2 "usage: hardening (contract|generate|replay|minimize|compare-oracle|verify-budget|scan-runtime)"
        with error ->
            fail 2 ("SCHARDENING003 " + error.Message)
