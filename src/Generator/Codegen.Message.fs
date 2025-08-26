namespace Generator

open System
open System.IO
open Generator.Ir
open Generator.Config
open Generator.Utils

module Message =

    let private fieldDecl (s: Ir.Signal) =
        sprintf "    float %s;" s.Name

    let private genDecodeForSignal (s: Ir.Signal) (doRangeCheck: bool) (config: Generator.Config.Config) =
        let len = int s.Length
        let startEff = Utils.chooseStartBit s config
        let (getFn, _) = Utils.accessorNames s.ByteOrder
        let raw = sprintf "raw_%s" s.Name
        let signFix =
            if s.IsSigned then
                sprintf "    if (%s & (1ULL << (%d - 1))) { %s |= ~((1ULL << %d) - 1); }" raw len raw len
            else ""
        // General float path variants
        let physAssignFloatDouble = sprintf "    msg->%s = (float)((double)%s * %.17g + %.17g);" s.Name raw s.Factor s.Offset
        let physAssignFloatFloat = sprintf "    msg->%s = (float)(((float)%s * (float)%.17g) + (float)%.17g);" s.Name raw s.Factor s.Offset
        // Choose assignment considering PhysType/PhysMode and fixed fast path
        let physAssign =
            match config.PhysType.ToLowerInvariant() with
            | "fixed" ->
                match Utils.tryPowerOfTenScale s.Factor with
                | Some scale when abs (s.Offset - Math.Round(s.Offset)) < 1e-12 ->
                    // Integer fast path (kept as-is)
                    sprintf "    msg->%s = (float)(((double)%s + (%.0f)) / (double)%d);" s.Name raw (Math.Round(s.Offset * (float scale))) scale
                | _ ->
                    match config.PhysMode.ToLowerInvariant() with
                    | "fixed_float" -> physAssignFloatFloat
                    | _ -> physAssignFloatDouble
            | _ ->
                match config.PhysMode.ToLowerInvariant() with
                | "float" -> physAssignFloatFloat
                | _ -> physAssignFloatDouble
        let rangeCheck =
            if doRangeCheck then
                match s.Minimum, s.Maximum with
                | Some minV, Some maxV -> Some (sprintf "    if (msg->%s < %.17g || msg->%s > %.17g) { return false; }" s.Name minV s.Name maxV)
                | Some minV, None -> Some (sprintf "    if (msg->%s < %.17g) { return false; }" s.Name minV)
                | None, Some maxV -> Some (sprintf "    if (msg->%s > %.17g) { return false; }" s.Name maxV)
                | _ -> None
            else None
        [
            sprintf "    uint64_t %s = 0;" raw
            sprintf "    // %s: start=%d len=%d factor=%.17g offset=%.17g" s.Name startEff len s.Factor s.Offset
            sprintf "    %s = %s(data, %d, %d);" raw getFn startEff len
            if signFix <> "" then signFix else null
            physAssign
            match rangeCheck with | Some r -> r | None -> null
        ]
        |> List.choose (fun x -> if isNull (box x) then None else Some x)
        |> String.concat "\n"

    let private genEncodeForSignal (s: Ir.Signal) (doRangeCheck: bool) (config: Generator.Config.Config) =
        let len = int s.Length
        let startEff = Utils.chooseStartBit s config
        let (_, setFn) = Utils.accessorNames s.ByteOrder
        let rangeChecks =
            if doRangeCheck then
                match s.Minimum, s.Maximum with
                | Some minV, Some maxV -> Some (sprintf "    if (msg->%s < %.17g || msg->%s > %.17g) { return false; }" s.Name minV s.Name maxV)
                | Some minV, None -> Some (sprintf "    if (msg->%s < %.17g) { return false; }" s.Name minV)
                | None, Some maxV -> Some (sprintf "    if (msg->%s > %.17g) { return false; }" s.Name maxV)
                | _ -> None
            else None
        // General path variants
        let computeRawDouble =
            sprintf "    double tmp_%s = ((double)msg->%s - %.17g) / %.17g;\n    int64_t raw_%s = (int64_t)(tmp_%s >= 0 ? tmp_%s + 0.5 : tmp_%s - 0.5);" s.Name s.Name s.Offset s.Factor s.Name s.Name s.Name s.Name
        let computeRawFloat =
            sprintf "    float tmp_%s = ((float)msg->%s - (float)%.17g) / (float)%.17g;\n    int64_t raw_%s = (int64_t)llroundf(tmp_%s);" s.Name s.Name s.Offset s.Factor s.Name s.Name
        // Fixed-point fast path for factor=10^-n and integral offset
        let computeRaw =
            match config.PhysType.ToLowerInvariant() with
            | "fixed" ->
                match Utils.tryPowerOfTenScale s.Factor with
                | Some scale when abs (s.Offset - Math.Round(s.Offset)) < 1e-12 ->
                    sprintf "    int64_t raw_%s = (int64_t)llround(((double)msg->%s - %.0f) * (double)%d);" s.Name s.Name (Math.Round s.Offset) scale
                | _ ->
                    match config.PhysMode.ToLowerInvariant() with
                    | "fixed_float" -> computeRawFloat
                    | _ -> computeRawDouble
            | _ ->
                match config.PhysMode.ToLowerInvariant() with
                | "float" -> computeRawFloat
                | _ -> computeRawDouble
        let setBits = sprintf "    %s(data, %d, %d, (uint64_t)raw_%s);" setFn startEff len s.Name
        [
            match rangeChecks with | Some r -> yield r | None -> ()
            yield computeRaw
            yield setBits
        ] |> String.concat "\n"

    let private partitionMultiplex (message: Ir.Message) =
        let switchOpt = message.Signals |> List.tryFind (fun s -> s.MultiplexerIndicator = Some "M")
        let baseSignals = message.Signals |> List.filter (fun s -> s.MultiplexerIndicator.IsNone)
        let branches =
            message.Signals
            |> List.choose (fun s ->
                match s.MultiplexerIndicator, s.MultiplexerSwitchValue with
                | Some ind, Some v when ind = "m" -> Some (v, s)
                | _ -> None)
            |> List.groupBy fst
            |> List.map (fun (k, xs) -> k, xs |> List.map snd)
        switchOpt, baseSignals, branches

    // Sanitize a string to an uppercase C identifier (A-Z0-9_), prefix with N_ if starting with a digit or empty
    let private sanitizeEnumIdent (s: string) : string =
        let up = s.ToUpperInvariant()
        let mapped =
            up
            |> Seq.map (fun ch -> if Char.IsLetterOrDigit ch then ch else '_')
            |> Seq.toArray
            |> fun arr -> new string(arr)
        let trimmed =
            mapped.Trim([|'_'|])
            |> fun t -> if String.IsNullOrWhiteSpace t then "N" else t
        let start = trimmed.[0]
        if Char.IsDigit start then "N_" + trimmed else trimmed

    let generateMessageFiles (message: Ir.Message) (outputPath: string) (config: Generator.Config.Config) =
        let messageNameLower = message.Name.ToLowerInvariant()
        let messageHPath = Path.Combine(outputPath, "include", sprintf "%s.h" messageNameLower)
        let messageCPath = Path.Combine(outputPath, "src", sprintf "%s.c" messageNameLower)

        // Banner for traceability
        let banner =
            sprintf "/* Generated by Signal CANdy\n   file_prefix=%s, phys_type=%s, phys_mode=%s, dispatch=%s, motorola_start_bit=%s */\n"
                config.FilePrefix config.PhysType config.PhysMode config.Dispatch config.MotorolaStartBit

        let signalDeclarationsH =
            message.Signals |> List.map fieldDecl |> String.concat "\n"

        let switchOpt, baseSignals, branches = partitionMultiplex message
        let isMux =
            match switchOpt, branches with
            | Some _, _::_ -> true
            | _ -> false

        // Validity macro names per signal (only used when isMux)
        let validMacro (sigName: string) = sprintf "%s_VALID_%s" (message.Name.ToUpperInvariant()) (sigName.ToUpperInvariant())

        let signalDecodeFor s = genDecodeForSignal s config.RangeCheck config

        let signalDecodeWithValid s =
            let body = signalDecodeFor s
            if isMux then body + (sprintf "\n    msg->valid |= %s;" (validMacro s.Name)) else body

        let signalDecodeC =
            match switchOpt, branches with
            | Some sw, (_ :: _) ->
                let swBlock =
                    let body = signalDecodeWithValid sw
                    body + (sprintf "\n    msg->mux_active = (%s_mux_e)((int)raw_%s);" message.Name sw.Name)
                let rawVar = sprintf "raw_%s" sw.Name
                let baseBlock = baseSignals |> List.map signalDecodeWithValid |> String.concat "\n\n"
                let branchesBlock =
                    branches
                    |> List.map (fun (k, sigs) ->
                        let inner = sigs |> List.map signalDecodeWithValid |> String.concat "\n\n"
                        [ sprintf "    if ((int)%s == %d) {" rawVar k
                          inner
                          "    }" ] |> String.concat "\n")
                    |> String.concat "\n"
                [ (if isMux then "    msg->valid = 0u;" else "")
                  swBlock
                  baseBlock
                  branchesBlock ]
                |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                |> String.concat "\n\n"
            | _ ->
                message.Signals |> List.map signalDecodeFor |> String.concat "\n\n"

        let signalEncodeC =
            match switchOpt, branches with
            | Some sw, (_ :: _) ->
                let len = int sw.Length
                let startEff = Utils.chooseStartBit sw config
                let (_, setFn) = Utils.accessorNames sw.ByteOrder
                let rangeChecks =
                    if config.RangeCheck then
                        match sw.Minimum, sw.Maximum with
                        | Some minV, Some maxV -> Some (sprintf "    if (msg->%s < %.17g || msg->%s > %.17g) { return false; }" sw.Name minV sw.Name maxV)
                        | Some minV, None -> Some (sprintf "    if (msg->%s < %.17g) { return false; }" sw.Name minV)
                        | None, Some maxV -> Some (sprintf "    if (msg->%s > %.17g) { return false; }" sw.Name maxV)
                        | _ -> None
                    else None
                let computeRawDouble =
                    sprintf "    double tmp_%s = ((double)msg->%s - %.17g) / %.17g;\n    int64_t raw_%s = (int64_t)(tmp_%s >= 0 ? tmp_%s + 0.5 : tmp_%s - 0.5);" sw.Name sw.Name sw.Offset sw.Factor sw.Name sw.Name sw.Name sw.Name
                let computeRawFloat =
                    sprintf "    float tmp_%s = ((float)msg->%s - (float)%.17g) / (float)%.17g;\n    int64_t raw_%s = (int64_t)llroundf(tmp_%s);" sw.Name sw.Name sw.Offset sw.Factor sw.Name sw.Name
                let computeRaw =
                    match config.PhysType.ToLowerInvariant() with
                    | "fixed" ->
                        match Utils.tryPowerOfTenScale sw.Factor with
                        | Some scale when abs (sw.Offset - Math.Round(sw.Offset)) < 1e-12 ->
                            sprintf "    int64_t raw_%s = (int64_t)llround(((double)msg->%s - %.0f) * (double)%d);" sw.Name sw.Name (Math.Round sw.Offset) scale
                        | _ ->
                            match config.PhysMode.ToLowerInvariant() with
                            | "fixed_float" -> computeRawFloat
                            | _ -> computeRawDouble
                    | _ ->
                        match config.PhysMode.ToLowerInvariant() with
                        | "float" -> computeRawFloat
                        | _ -> computeRawDouble
                let setBits = sprintf "    %s(data, %d, %d, (uint64_t)raw_%s);" setFn startEff len sw.Name
                let baseBlock = baseSignals |> List.map (fun s -> genEncodeForSignal s config.RangeCheck config) |> String.concat "\n\n"
                let branchesBlock =
                    branches
                    |> List.map (fun (k, sigs) ->
                        let inner = sigs |> List.map (fun s -> genEncodeForSignal s config.RangeCheck config) |> String.concat "\n\n"
                        [ sprintf "    if ((int)raw_%s == %d) {" sw.Name k
                          inner
                          "    }" ] |> String.concat "\n")
                    |> String.concat "\n"
                [ match rangeChecks with | Some r -> yield r | None -> ()
                  yield computeRaw
                  yield setBits
                  yield baseBlock
                  yield branchesBlock ]
                |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                |> String.concat "\n\n"
            | _ ->
                message.Signals |> List.map (fun s -> genEncodeForSignal s config.RangeCheck config) |> String.concat "\n\n"

        let headerContent =
            let headerLines = System.Collections.Generic.List<string>()
            headerLines.Add(banner)
            headerLines.Add (sprintf "#ifndef %s_H" (message.Name.ToUpperInvariant()))
            headerLines.Add (sprintf "#define %s_H" (message.Name.ToUpperInvariant()))
            headerLines.Add ""
            headerLines.Add "#include <stdint.h>"
            headerLines.Add "#include <stdbool.h>"
            headerLines.Add ""
            headerLines.Add "#ifdef __cplusplus"
            headerLines.Add "extern \"C\" {"
            headerLines.Add "#endif"
            headerLines.Add ""
            // Emit value-table enums and to_string prototypes
            let vtSignals = message.Signals |> List.choose (fun s -> s.ValueTable |> Option.map (fun vt -> s, vt))
            vtSignals |> List.iter (fun (s, vt) ->
                let enumName = sprintf "%s_%s_e" message.Name s.Name
                headerLines.Add (sprintf "typedef enum {")
                // Ensure unique labels
                let mutable used = Set.empty
                vt |> List.iter (fun (v, name) ->
                    let baseLabel = sanitizeEnumIdent name
                    let rec uniqueLabel lbl idx =
                        let candidate = if idx = 0 then lbl else sprintf "%s_%d" lbl idx
                        if used.Contains candidate then uniqueLabel lbl (idx+1) else candidate
                    let label = uniqueLabel baseLabel 0
                    used <- used.Add label
                    headerLines.Add (sprintf "    %s_%s_%s = %d," (message.Name.ToUpperInvariant()) (s.Name.ToUpperInvariant()) label v)
                )
                // Close enum (remove trailing comma is optional in C, keep it for simplicity)
                headerLines.Add (sprintf "} %s;" enumName)
                headerLines.Add ""
                headerLines.Add (sprintf "const char* %s_%s_to_string(int v);" message.Name s.Name)
                headerLines.Add ""
            )
            if isMux then
                // Emit enum of known branch values
                let enumName = sprintf "%s_mux_e" message.Name
                headerLines.Add (sprintf "typedef enum { ")
                let enumEntries =
                    branches
                    |> List.map (fun (k, _) -> sprintf "    %s_MUX_%d = %d" (message.Name.ToUpperInvariant()) k k)
                    |> String.concat ",\n"
                headerLines.Add enumEntries
                headerLines.Add (sprintf "} %s;" enumName)
                headerLines.Add ""
                // Validity macros
                message.Signals
                |> List.iteri (fun idx s -> headerLines.Add (sprintf "#define %s (1u << %d)" (validMacro s.Name) idx))
                headerLines.Add ""
            headerLines.Add "typedef struct {"
            headerLines.Add signalDeclarationsH
            if isMux then
                headerLines.Add "    uint32_t valid;"
                headerLines.Add (sprintf "    %s_mux_e mux_active;" message.Name)
            headerLines.Add (sprintf "} %s_t;" message.Name)
            headerLines.Add ""
            headerLines.Add (sprintf "bool %s_decode(%s_t* msg, const uint8_t data[], uint8_t dlc);" message.Name message.Name)
            headerLines.Add (sprintf "bool %s_encode(uint8_t data[], uint8_t* out_dlc, const %s_t* msg);" message.Name message.Name)
            headerLines.Add ""
            headerLines.Add "#ifdef __cplusplus"
            headerLines.Add "}"
            headerLines.Add "#endif"
            headerLines.Add ""
            headerLines.Add (sprintf "#endif // %s_H" (message.Name.ToUpperInvariant()))
            String.concat "\n" (List.ofSeq headerLines)

        let sourceContent =
            let src = System.Collections.Generic.List<string>()
            src.Add(banner)
            src.Add (sprintf "#include \"%s.h\"" messageNameLower)
            let utilsHeader = Utils.utilsHeaderName config
            src.Add (sprintf "#include \"%s\"" utilsHeader)
            src.Add "#include <string.h>"
            src.Add "#include <math.h>"
            src.Add ""
            // to_string implementations for value-table signals
            let vtSignals = message.Signals |> List.choose (fun s -> s.ValueTable |> Option.map (fun vt -> s, vt))
            vtSignals |> List.iter (fun (s, vt) ->
                src.Add (sprintf "const char* %s_%s_to_string(int v) {" message.Name s.Name)
                src.Add "    switch (v) {"
                vt |> List.iter (fun (v, name) ->
                    // Use original name for display
                    src.Add (sprintf "    case %d: return \"%s\";" v (name.Replace("\"","\\\"")))
                )
                src.Add "    default: return \"UNKNOWN\";"
                src.Add "    }"
                src.Add "}"
                src.Add ""
            )
            src.Add (sprintf "bool %s_decode(%s_t* msg, const uint8_t data[], uint8_t dlc) {" message.Name message.Name)
            src.Add (sprintf "    if (dlc < %d) { return false; }" (int message.Length))
            src.Add signalDecodeC
            src.Add "    return true;"
            src.Add "}"
            src.Add ""
            src.Add (sprintf "bool %s_encode(uint8_t data[], uint8_t* out_dlc, const %s_t* msg) {" message.Name message.Name)
            src.Add "    memset(data, 0, 8);"
            src.Add (sprintf "    *out_dlc = %d;" (int message.Length))
            src.Add signalEncodeC
            src.Add "    return true;"
            src.Add "}"
            String.concat "\n" (List.ofSeq src)

        File.WriteAllText(messageHPath, headerContent)
        File.WriteAllText(messageCPath, sourceContent)