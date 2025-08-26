namespace Generator

open System
open System.IO
open System.Collections.Generic
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions

module Config =
    type Config = {
        PhysType: string
        PhysMode: string
        RangeCheck: bool
        Dispatch: string
        CrcCounterCheck: bool
        MotorolaStartBit: string
        FilePrefix: string
    }

    let private tryGetString (map: IDictionary<string, obj>) (keys: string list) : string option =
        keys
        |> List.tryPick (fun k ->
            match map.TryGetValue(k) with
            | true, v when v <> null ->
                match v with
                | :? string as s -> Some s
                | _ -> Some (string v)
            | _ -> None)

    let private tryGetBool (map: IDictionary<string, obj>) (keys: string list) : bool option =
        keys
        |> List.tryPick (fun k ->
            match map.TryGetValue(k) with
            | true, v when v <> null ->
                match v with
                | :? bool as b -> Some b
                | :? string as s ->
                    match Boolean.TryParse(s) with | true, b -> Some b | _ -> None
                | _ -> None
            | _ -> None)

    // Function to load config from YAML (supports both snake_case and PascalCase keys)
    let loadConfig (configPath: string) : Config option =
        try
            use reader = new StreamReader(configPath)
            let yaml = reader.ReadToEnd()
            let deserializer = DeserializerBuilder().Build()
            let map = deserializer.Deserialize<IDictionary<string, obj>>(yaml)
            let phys = tryGetString map [ "phys_type"; "PhysType" ] |> Option.defaultValue "float"
            
            // Validate phys_type
            let validPhysTypes = ["float"; "fixed"]
            if not (List.contains (phys.ToLowerInvariant()) validPhysTypes) then
                eprintfn "Warning: Invalid phys_type '%s'. Valid values: %s. Using default 'float'." phys (String.concat ", " validPhysTypes)
            
            // New: phys_mode provides fine-grained control over internal math
            let physModeRaw = tryGetString map [ "phys_mode"; "PhysMode" ]
            let physMode =
                match physModeRaw with
                | Some m -> m
                | None ->
                    match phys.ToLowerInvariant() with
                    | "float" -> "double"          // backward-compat: old float defaults to double intermediates
                    | "fixed" -> "fixed_double"    // old fixed falls back to double when fast path not applicable
                    | _ -> "double"
            
            // Validate phys_mode
            let validPhysModes = ["double"; "float"; "fixed_double"; "fixed_float"]
            if not (List.contains (physMode.ToLowerInvariant()) validPhysModes) then
                eprintfn "Warning: Invalid phys_mode '%s'. Valid values: %s. Using default 'double'." physMode (String.concat ", " validPhysModes)
            
            let range = tryGetBool map [ "range_check"; "RangeCheck" ] |> Option.defaultValue false
            let disp = tryGetString map [ "dispatch"; "Dispatch" ] |> Option.defaultValue "binary_search"
            
            // Validate dispatch
            let validDispatch = ["binary_search"; "direct_map"]
            if not (List.contains (disp.ToLowerInvariant()) validDispatch) then
                eprintfn "Warning: Invalid dispatch '%s'. Valid values: %s. Using default 'binary_search'." disp (String.concat ", " validDispatch)
            
            let crc = tryGetBool map [ "crc_counter_check"; "CrcCounterCheck" ] |> Option.defaultValue false
            let moto = tryGetString map [ "motorola_start_bit"; "MotorolaStartBit" ] |> Option.defaultValue "msb"
            
            // Validate motorola_start_bit
            let validMoto = ["msb"; "lsb"]
            if not (List.contains (moto.ToLowerInvariant()) validMoto) then
                eprintfn "Warning: Invalid motorola_start_bit '%s'. Valid values: %s. Using default 'msb'." moto (String.concat ", " validMoto)
            
            // Optional: file prefix for generated common files (utils/registry). Defaults to "sc_".
            let filePrefix = tryGetString map [ "file_prefix"; "FilePrefix" ] |> Option.defaultValue "sc_"
            
            // Validate file_prefix (basic C identifier rules)
            if not (System.Text.RegularExpressions.Regex.IsMatch(filePrefix, @"^[a-zA-Z_][a-zA-Z0-9_]*$")) then
                eprintfn "Warning: file_prefix '%s' may not be a valid C identifier prefix. Proceeding anyway." filePrefix
            
            Some { PhysType = phys; PhysMode = physMode; RangeCheck = range; Dispatch = disp; CrcCounterCheck = crc; MotorolaStartBit = moto; FilePrefix = filePrefix }
        with
        | ex ->
            eprintfn "Error loading config file: %s" ex.Message
            None
