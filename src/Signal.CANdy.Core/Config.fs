namespace Signal.CANdy.Core

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open YamlDotNet.Serialization
open Signal.CANdy.Core.Errors

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

    // --- Validation helpers ---
    let private validPhysTypes = [ "float"; "fixed" ]
    let private validPhysModes = [ "double"; "float"; "fixed_double"; "fixed_float" ]
    let private validDispatch = [ "binary_search"; "direct_map" ]
    let private validMoto = [ "msb"; "lsb" ]
    let private prefixRegex = Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")

    let validate (cfg: Config) : Result<Config, ValidationError> =
        if not (List.contains (cfg.PhysType.ToLowerInvariant()) validPhysTypes) then
            Error (ValidationError.InvalidValue (sprintf "Invalid phys_type '%s'" cfg.PhysType))
        elif not (List.contains (cfg.PhysMode.ToLowerInvariant()) validPhysModes) then
            Error (ValidationError.InvalidValue (sprintf "Invalid phys_mode '%s'" cfg.PhysMode))
        elif not (List.contains (cfg.Dispatch.ToLowerInvariant()) validDispatch) then
            Error (ValidationError.InvalidValue (sprintf "Invalid dispatch '%s'" cfg.Dispatch))
        elif not (List.contains (cfg.MotorolaStartBit.ToLowerInvariant()) validMoto) then
            Error (ValidationError.InvalidValue (sprintf "Invalid motorola_start_bit '%s'" cfg.MotorolaStartBit))
        elif not (prefixRegex.IsMatch cfg.FilePrefix) then
            Error (ValidationError.InvalidValue (sprintf "Invalid file_prefix '%s'" cfg.FilePrefix))
        else
            Ok cfg

    // --- YAML loading helpers ---
    let private tryGetString (map: IDictionary<string, obj>) (keys: string list) : string option =
        keys
        |> List.tryPick (fun k ->
            match map.TryGetValue(k) with
            | true, v when not (isNull v) ->
                match v with
                | :? string as s -> Some s
                | _ -> Some (string v)
            | _ -> None)

    let private tryGetBool (map: IDictionary<string, obj>) (keys: string list) : bool option =
        keys
        |> List.tryPick (fun k ->
            match map.TryGetValue(k) with
            | true, v when not (isNull v) ->
                match v with
                | :? bool as b -> Some b
                | :? string as s ->
                    match Boolean.TryParse(s) with | true, b -> Some b | _ -> None
                | _ -> None
            | _ -> None)

    /// Load a YAML config file and return a validated Config
    let loadFromYaml (configPath: string) =
        try
            use reader = new System.IO.StreamReader(configPath)
            let yaml = reader.ReadToEnd()
            let deserializer = DeserializerBuilder().Build()
            let map = deserializer.Deserialize<IDictionary<string, obj>>(yaml)

            let phys = tryGetString map [ "phys_type"; "PhysType" ] |> Option.defaultValue "float"
            let physModeRaw = tryGetString map [ "phys_mode"; "PhysMode" ]
            let physMode =
                match physModeRaw with
                | Some m -> m
                | None ->
                    match phys.ToLowerInvariant() with
                    | "float" -> "double"
                    | "fixed" -> "fixed_double"
                    | _ -> "double"

            let range = tryGetBool map [ "range_check"; "RangeCheck" ] |> Option.defaultValue false
            let disp = tryGetString map [ "dispatch"; "Dispatch" ] |> Option.defaultValue "binary_search"
            let crc = tryGetBool map [ "crc_counter_check"; "CrcCounterCheck" ] |> Option.defaultValue false
            let moto = tryGetString map [ "motorola_start_bit"; "MotorolaStartBit" ] |> Option.defaultValue "msb"
            let filePrefix = tryGetString map [ "file_prefix"; "FilePrefix" ] |> Option.defaultValue "sc_"

            let cfg = {
                PhysType = phys
                PhysMode = physMode
                RangeCheck = range
                Dispatch = disp
                CrcCounterCheck = crc
                MotorolaStartBit = moto
                FilePrefix = filePrefix
            }

            validate cfg
        with ex ->
            Error (ValidationError.IoError ex.Message)
