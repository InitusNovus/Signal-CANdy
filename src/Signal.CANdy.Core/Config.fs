namespace Signal.CANdy.Core

open System
open System.Collections.Generic
open System.Globalization
open System.Text.RegularExpressions
open YamlDotNet.Serialization
open Signal.CANdy.Core.Errors

module Config =
    type CrcSignalConfig =
        { Signal: string
          Algorithm: string
          ByteRange: int * int
          DataId: uint16 option }

    type CounterSignalConfig =
        { Signal: string
          Modulus: int
          Increment: int }

    type CrcCounterMessageConfig =
        { Crc: CrcSignalConfig option
          Counter: CounterSignalConfig option }

    type CrcCounterConfig =
        { Mode: string
          Messages: Map<string, CrcCounterMessageConfig>
          CustomAlgorithms:
              Map<
                  string,
                  {| Width: int
                     Poly: uint64
                     Init: uint64
                     XorOut: uint64
                     ReflectIn: bool
                     ReflectOut: bool |}
               > option }

    type Config =
        { PhysType: string
          PhysMode: string
          RangeCheck: bool
          Dispatch: string
          CrcCounterCheck: bool
          MotorolaStartBit: string
          FilePrefix: string
          CrcCounter: CrcCounterConfig option }

    // --- Validation helpers ---
    let private validPhysTypes = [ "float"; "fixed" ]
    let private validPhysModes = [ "double"; "float"; "fixed_double"; "fixed_float" ]
    let private validDispatch = [ "binary_search"; "direct_map" ]
    let private validMoto = [ "msb"; "lsb" ]
    let private validCrcCounterModes = [ "validate"; "passthrough"; "fail_fast" ]
    let private builtinAlgorithmWidths =
        [ "CRC8_SAE_J1850", 8
          "CRC8_8H2F", 8
          "CRC16_CCITT", 16
          "CRC32P4", 32 ]
        |> Map.ofList

    let private prefixRegex = Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")

    let private validateCrcCounter (cfg: Config) : ValidationError option =
        match cfg.CrcCounter with
        | None -> None
        | Some crcCfg ->
            if not cfg.CrcCounterCheck then
                Some(
                    ConfigConflict
                        "crc_counter block is present but crc_counter_check is false; set crc_counter_check: true to enable CRC/Counter validation"
                )
            elif not (List.contains crcCfg.Mode validCrcCounterModes) then
                Some(
                    InvalidValue(
                        sprintf
                            "Unknown crc_counter mode '%s'; valid: validate, passthrough, fail_fast"
                            crcCfg.Mode
                    )
                )
            else
                let customAlgorithms = crcCfg.CustomAlgorithms |> Option.defaultValue Map.empty

                let validateMessage (msgName: string) (msgCfg: CrcCounterMessageConfig) : ValidationError option =
                    let crcError =
                        match msgCfg.Crc with
                        | Some crcSig ->
                            if String.IsNullOrWhiteSpace(crcSig.Signal) then
                                Some(
                                    InvalidValue(
                                        sprintf
                                            "Signal name for CRC/Counter in message '%s' must not be empty"
                                            msgName
                                    )
                                )
                            else
                                let builtinWidth = Map.tryFind crcSig.Algorithm builtinAlgorithmWidths
                                let customWidth = customAlgorithms |> Map.tryFind crcSig.Algorithm |> Option.map _.Width

                                match builtinWidth, customWidth with
                                | None, None -> Some(UnknownAlgorithm crcSig.Algorithm)
                                | Some width, _
                                | _, Some width when width <> 8 ->
                                    Some(
                                        ConfigConflict(
                                            sprintf
                                                "CRC algorithm '%s' width must be 8 for MVP; CRC-16/32/64 not yet supported"
                                                crcSig.Algorithm
                                        )
                                    )
                                | _ -> None
                        | None -> None

                    let counterError =
                        match msgCfg.Counter with
                        | Some counterSig when counterSig.Modulus < 2 -> Some(InvalidModulus(msgName, counterSig.Modulus))
                        | Some counterSig when String.IsNullOrWhiteSpace(counterSig.Signal) ->
                            Some(
                                InvalidValue(
                                    sprintf
                                        "Signal name for CRC/Counter in message '%s' must not be empty"
                                        msgName
                                )
                            )
                        | _ -> None

                    Option.orElse crcError counterError

                crcCfg.Messages
                |> Map.toList
                |> List.tryPick (fun (msgName, msgCfg) -> validateMessage msgName msgCfg)

    let validate (cfg: Config) : Result<Config, ValidationError> =
        if not (List.contains (cfg.PhysType.ToLowerInvariant()) validPhysTypes) then
            Error(ValidationError.InvalidValue(sprintf "Invalid phys_type '%s'" cfg.PhysType))
        elif not (List.contains (cfg.PhysMode.ToLowerInvariant()) validPhysModes) then
            Error(ValidationError.InvalidValue(sprintf "Invalid phys_mode '%s'" cfg.PhysMode))
        elif not (List.contains (cfg.Dispatch.ToLowerInvariant()) validDispatch) then
            Error(ValidationError.InvalidValue(sprintf "Invalid dispatch '%s'" cfg.Dispatch))
        elif not (List.contains (cfg.MotorolaStartBit.ToLowerInvariant()) validMoto) then
            Error(ValidationError.InvalidValue(sprintf "Invalid motorola_start_bit '%s'" cfg.MotorolaStartBit))
        elif not (prefixRegex.IsMatch cfg.FilePrefix) then
            Error(ValidationError.InvalidValue(sprintf "Invalid file_prefix '%s'" cfg.FilePrefix))
        else
            match validateCrcCounter cfg with
            | Some err -> Error err
            | None -> Ok cfg

    // --- YAML loading helpers ---
    let private tryGetString (map: IDictionary<string, obj>) (keys: string list) : string option =
        keys
        |> List.tryPick (fun k ->
            match map.TryGetValue(k) with
            | true, v when not (isNull v) ->
                match v with
                | :? string as s -> Some s
                | _ -> Some(string v)
            | _ -> None)

    let private tryGetBool (map: IDictionary<string, obj>) (keys: string list) : bool option =
        keys
        |> List.tryPick (fun k ->
            match map.TryGetValue(k) with
            | true, v when not (isNull v) ->
                match v with
                | :? bool as b -> Some b
                | :? string as s ->
                    match Boolean.TryParse(s) with
                    | true, b -> Some b
                    | _ -> None
                | _ -> None
            | _ -> None)

    let private tryAsMap (value: obj) : IDictionary<string, obj> option =
        match value with
        | :? IDictionary<string, obj> as dict -> Some dict
        | _ -> None

    let private tryParseInt64FromString (s: string) : int64 option =
        let trimmed = s.Trim()

        if trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) then
            let hex = trimmed.Substring(2)

            match Int64.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) with
            | true, value -> Some value
            | _ -> None
        else
            match Int64.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, value -> Some value
            | _ -> None

    let private tryParseInt64Value (value: obj) : int64 option =
        match value with
        | :? int8 as v -> Some(int64 v)
        | :? uint8 as v -> Some(int64 v)
        | :? int16 as v -> Some(int64 v)
        | :? uint16 as v -> Some(int64 v)
        | :? int32 as v -> Some(int64 v)
        | :? uint32 as v -> Some(int64 v)
        | :? int64 as v -> Some v
        | :? uint64 as v when v <= uint64 Int64.MaxValue -> Some(int64 v)
        | :? string as s -> tryParseInt64FromString s
        | _ -> None

    let private tryGetInt64 (map: IDictionary<string, obj>) (keys: string list) : int64 option =
        keys
        |> List.tryPick (fun k ->
            match map.TryGetValue(k) with
            | true, v when not (isNull v) -> tryParseInt64Value v
            | _ -> None)

    /// Load a YAML config file and return a validated Config
    let loadFromYaml (configPath: string) =
        try
            use reader = new System.IO.StreamReader(configPath)
            let yaml = reader.ReadToEnd()
            let deserializer = DeserializerBuilder().Build()
            let map = deserializer.Deserialize<IDictionary<string, obj>>(yaml)

            let phys =
                tryGetString map [ "phys_type"; "PhysType" ] |> Option.defaultValue "float"

            let physModeRaw = tryGetString map [ "phys_mode"; "PhysMode" ]

            let physMode =
                match physModeRaw with
                | Some m -> m
                | None ->
                    match phys.ToLowerInvariant() with
                    | "float" -> "double"
                    | "fixed" -> "fixed_double"
                    | _ -> "double"

            let range =
                tryGetBool map [ "range_check"; "RangeCheck" ] |> Option.defaultValue false

            let disp =
                tryGetString map [ "dispatch"; "Dispatch" ]
                |> Option.defaultValue "binary_search"

            let crc =
                tryGetBool map [ "crc_counter_check"; "CrcCounterCheck" ]
                |> Option.defaultValue false

            let moto =
                tryGetString map [ "motorola_start_bit"; "MotorolaStartBit" ]
                |> Option.defaultValue "msb"

            let filePrefix =
                tryGetString map [ "file_prefix"; "FilePrefix" ] |> Option.defaultValue "sc_"

            let crcCounter =
                match map.TryGetValue("crc_counter") with
                | true, v when not (isNull v) ->
                    match tryAsMap v with
                    | Some crcMap ->
                        let mode = tryGetString crcMap [ "mode" ] |> Option.defaultValue "validate"

                        let customAlgorithms =
                            match crcMap.TryGetValue("algorithms") with
                            | true, algorithmsObj when not (isNull algorithmsObj) ->
                                match tryAsMap algorithmsObj with
                                | Some algorithmsMap ->
                                    let parsed =
                                        algorithmsMap
                                        |> Seq.choose (fun entry ->
                                            match tryAsMap entry.Value with
                                            | Some defMap ->
                                                let width =
                                                    tryGetInt64 defMap [ "width" ]
                                                    |> Option.map int
                                                    |> Option.defaultValue 0

                                                let poly =
                                                    tryGetInt64 defMap [ "poly" ]
                                                    |> Option.map uint64
                                                    |> Option.defaultValue 0UL

                                                let init =
                                                    tryGetInt64 defMap [ "init" ]
                                                    |> Option.map uint64
                                                    |> Option.defaultValue 0UL

                                                let xorOut =
                                                    tryGetInt64 defMap [ "xor_out"; "xorOut" ]
                                                    |> Option.map uint64
                                                    |> Option.defaultValue 0UL

                                                let reflectIn =
                                                    tryGetBool defMap [ "reflect_in"; "reflectIn" ]
                                                    |> Option.defaultValue false

                                                let reflectOut =
                                                    tryGetBool defMap [ "reflect_out"; "reflectOut" ]
                                                    |> Option.defaultValue false

                                                Some(
                                                    entry.Key,
                                                    {| Width = width
                                                       Poly = poly
                                                       Init = init
                                                       XorOut = xorOut
                                                       ReflectIn = reflectIn
                                                       ReflectOut = reflectOut |}
                                                )
                                            | None -> None)
                                        |> Map.ofSeq

                                    if Map.isEmpty parsed then None else Some parsed
                                | None -> None
                            | _ -> None

                        let messages =
                            match crcMap.TryGetValue("messages") with
                            | true, messagesObj when not (isNull messagesObj) ->
                                match tryAsMap messagesObj with
                                | Some messagesMap ->
                                    messagesMap
                                    |> Seq.choose (fun msgEntry ->
                                        match tryAsMap msgEntry.Value with
                                        | Some msgMap ->
                                            let crcCfg =
                                                match msgMap.TryGetValue("crc") with
                                                | true, crcObj when not (isNull crcObj) ->
                                                    match tryAsMap crcObj with
                                                    | Some crcSigMap ->
                                                        let signal =
                                                            tryGetString crcSigMap [ "signal" ]
                                                            |> Option.defaultValue ""

                                                        let algorithm =
                                                            tryGetString crcSigMap [ "algorithm" ]
                                                            |> Option.defaultValue ""

                                                        let byteRange =
                                                            match crcSigMap.TryGetValue("byte_range") with
                                                            | true, byteRangeObj when not (isNull byteRangeObj) ->
                                                                match byteRangeObj with
                                                                | :? IDictionary<string, obj> as rangeMap ->
                                                                    let start =
                                                                        tryGetInt64 rangeMap [ "start" ]
                                                                        |> Option.map int
                                                                        |> Option.defaultValue 0

                                                                    let ending =
                                                                        tryGetInt64 rangeMap [ "end" ]
                                                                        |> Option.map int
                                                                        |> Option.defaultValue 0

                                                                    (start, ending)
                                                                | :? IList<obj> as rangeList when rangeList.Count >= 2 ->
                                                                    let start =
                                                                        tryParseInt64Value rangeList.[0]
                                                                        |> Option.map int
                                                                        |> Option.defaultValue 0

                                                                    let ending =
                                                                        tryParseInt64Value rangeList.[1]
                                                                        |> Option.map int
                                                                        |> Option.defaultValue 0

                                                                    (start, ending)
                                                                | _ -> (0, 0)
                                                            | _ -> (0, 0)

                                                        let dataId =
                                                            tryGetInt64 crcSigMap [ "data_id"; "dataId" ]
                                                            |> Option.map uint16

                                                        Some
                                                            { Signal = signal
                                                              Algorithm = algorithm
                                                              ByteRange = byteRange
                                                              DataId = dataId }
                                                    | None -> None
                                                | _ -> None

                                            let counterCfg =
                                                match msgMap.TryGetValue("counter") with
                                                | true, counterObj when not (isNull counterObj) ->
                                                    match tryAsMap counterObj with
                                                    | Some counterMap ->
                                                        let signal =
                                                            tryGetString counterMap [ "signal" ]
                                                            |> Option.defaultValue ""

                                                        let modulus =
                                                            tryGetInt64 counterMap [ "modulus" ]
                                                            |> Option.map int
                                                            |> Option.defaultValue 0

                                                        let increment =
                                                            tryGetInt64 counterMap [ "increment" ]
                                                            |> Option.map int
                                                            |> Option.defaultValue 1

                                                        Some
                                                            { Signal = signal
                                                              Modulus = modulus
                                                              Increment = increment }
                                                    | None -> None
                                                | _ -> None

                                            Some(
                                                msgEntry.Key,
                                                { Crc = crcCfg
                                                  Counter = counterCfg }
                                            )
                                        | None -> None)
                                    |> Map.ofSeq
                                | None -> Map.empty
                            | _ -> Map.empty

                        Some
                            { Mode = mode
                              Messages = messages
                              CustomAlgorithms = customAlgorithms }
                    | None -> None
                | _ -> None

            let cfg =
                { PhysType = phys
                  PhysMode = physMode
                  RangeCheck = range
                  Dispatch = disp
                  CrcCounterCheck = crc
                  MotorolaStartBit = moto
                  FilePrefix = filePrefix
                  CrcCounter = crcCounter }

            validate cfg
        with ex ->
            Error(ValidationError.IoError ex.Message)
