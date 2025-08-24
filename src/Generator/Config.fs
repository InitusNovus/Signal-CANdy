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
            let range = tryGetBool map [ "range_check"; "RangeCheck" ] |> Option.defaultValue false
            let disp = tryGetString map [ "dispatch"; "Dispatch" ] |> Option.defaultValue "binary_search"
            let crc = tryGetBool map [ "crc_counter_check"; "CrcCounterCheck" ] |> Option.defaultValue false
            let moto = tryGetString map [ "motorola_start_bit"; "MotorolaStartBit" ] |> Option.defaultValue "msb"
            // Optional: file prefix for generated common files (utils/registry). Defaults to "sc_".
            let filePrefix = tryGetString map [ "file_prefix"; "FilePrefix" ] |> Option.defaultValue "sc_"
            Some { PhysType = phys; PhysMode = physMode; RangeCheck = range; Dispatch = disp; CrcCounterCheck = crc; MotorolaStartBit = moto; FilePrefix = filePrefix }
        with
        | ex ->
            eprintfn "Error loading config file: %s" ex.Message
            None
