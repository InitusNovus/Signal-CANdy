namespace Signal.CANdy.Core

open System
open System.IO
open System.Text.RegularExpressions
open YamlDotNet.RepresentationModel
open YamlDotNet.Core

module ProjectManifest =

    type PoolInput =
        | Definition of string
        | Manifest of string

    type WireSource = { Name: string; Path: string }

    type ProjectOutputs =
        { Image: string
          Header: string option
          Inspect: string option
          Activation: string option }

    type ProjectManifest =
        { Name: string
          Pool: PoolInput
          WireSources: WireSource list
          Binding: string
          Target: string
          Outputs: ProjectOutputs }

    type ResolvedPoolInput =
        | ResolvedDefinition of string
        | ResolvedManifest of string

    type ResolvedWireSource = { Name: string; Path: string }

    type ResolvedOutputs =
        { Image: string
          Header: string option
          Inspect: string option
          Activation: string option }

    type ResolvedProject =
        { ManifestPath: string
          RootDirectory: string
          Name: string
          Pool: ResolvedPoolInput
          WireSources: ResolvedWireSource list
          Binding: string
          Target: string
          Outputs: ResolvedOutputs }

    type ProjectParseError = ProjectParseError of string
    type ProjectPathError = ProjectParseError

    type private ResultBuilder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return value = Ok value
        member _.ReturnFrom value = value
        member _.Zero() = Ok()
        member _.Delay generator = generator
        member _.Run generator = generator ()
        member _.Combine(first, second) = Result.bind (fun () -> second ()) first

        member _.For(values, body) =
            values
            |> Seq.fold (fun state value -> Result.bind (fun () -> body value) state) (Ok())

    let private result = ResultBuilder()

    let private identifier =
        Regex("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)

    let private scalar context (node: YamlNode) =
        match node with
        | :? YamlScalarNode as value when not (isNull value.Value) ->
            let text = value.Value

            if
                (value.Style = ScalarStyle.Plain || value.Style = ScalarStyle.Any)
                && (text = "null"
                    || text = "true"
                    || text = "false"
                    || Regex.IsMatch(text, "^[+-]?[0-9]+(?:\\.[0-9]+)?$"))
            then
                Error(sprintf "%s must be an authored string scalar." context)
            else
                Ok text
        | _ -> Error(sprintf "%s must be a scalar string." context)

    let private mapping context allowed (node: YamlNode) =
        match node with
        | :? YamlMappingNode as map ->
            let mutable values = Map.empty
            let mutable error = None

            for entry in map.Children do
                match scalar (context + " key") entry.Key with
                | Error details ->
                    if error.IsNone then
                        error <- Some details
                | Ok key when not (List.contains key allowed) ->
                    if error.IsNone then
                        error <- Some(sprintf "%s contains unknown key '%s'." context key)
                | Ok key when values.ContainsKey key ->
                    if error.IsNone then
                        error <- Some(sprintf "%s contains duplicate key '%s'." context key)
                | Ok key -> values <- values.Add(key, entry.Value)

            match error with
            | Some value -> Error value
            | None -> Ok values
        | _ -> Error(sprintf "%s must be a mapping." context)

    let private required context key values =
        match values |> Map.tryFind key with
        | Some value -> Ok value
        | None -> Error(sprintf "%s is missing required key '%s'." context key)

    let private requiredScalar context key values =
        required context key values |> Result.bind (scalar (context + "." + key))

    let private parseInternal (yaml: string) =
        if
            Regex.IsMatch(yaml, "(?m)^\\s*%")
            || Regex.IsMatch(yaml, "(?m)^\\s*---\\s*$")
            || Regex.IsMatch(yaml, "(?m)(?:^|\\s)[&*][A-Za-z0-9_-]+(?:\\s|$)")
            || Regex.IsMatch(yaml, "(?m)(?:^|\\s)![!A-Za-z]")
            || Regex.IsMatch(yaml, "(?m)^\\s*<<\\s*:")
        then
            Error "Unsupported YAML directive, document marker, anchor, alias, merge, or explicit tag."
        else
            let stream = YamlStream()
            stream.Load(new StringReader(yaml))

            if stream.Documents.Count <> 1 then
                Error "A project must contain exactly one YAML document."
            else
                result {
                    let! root =
                        mapping
                            "Project"
                            [ "format"; "name"; "pool"; "wireSources"; "binding"; "target"; "outputs" ]
                            stream.Documents.[0].RootNode

                    for key in [ "format"; "name"; "pool"; "wireSources"; "binding"; "target"; "outputs" ] do
                        let! _ = required "Project" key root
                        ()

                    let! format = requiredScalar "Project" "format" root

                    if format <> "sc.project/v1" then
                        return! Error "Invalid project format."

                    let! name = requiredScalar "Project" "name" root

                    if not (identifier.IsMatch name) then
                        return! Error "Project name is not a valid identifier."

                    let! poolNode = required "Project" "pool" root
                    let! poolMap = mapping "Project.pool" [ "definition"; "manifest" ] poolNode

                    let! pool =
                        match Map.tryFind "definition" poolMap, Map.tryFind "manifest" poolMap with
                        | Some node, None -> scalar "Project.pool.definition" node |> Result.map Definition
                        | None, Some node -> scalar "Project.pool.manifest" node |> Result.map Manifest
                        | _ -> Error "Project.pool must contain exactly one of definition or manifest."

                    let! wiresNode = required "Project" "wireSources" root

                    let! wires =
                        match wiresNode with
                        | :? YamlSequenceNode as sequence when sequence.Children.Count > 0 ->
                            sequence.Children
                            |> Seq.mapi (fun index node ->
                                result {
                                    let context = sprintf "Project.wireSources[%d]" index
                                    let! values = mapping context [ "name"; "type"; "path" ] node
                                    let! sourceName = requiredScalar context "name" values
                                    let! sourceType = requiredScalar context "type" values
                                    let! sourcePath = requiredScalar context "path" values

                                    if not (identifier.IsMatch sourceName) then
                                        return! Error(sprintf "%s.name is invalid." context)

                                    if sourceType <> "dbc" then
                                        return! Error(sprintf "%s.type must be dbc." context)

                                    return ({ Name = sourceName; Path = sourcePath }: WireSource)
                                })
                            |> Seq.toList
                            |> List.fold
                                (fun state item ->
                                    state
                                    |> Result.bind (fun values -> item |> Result.map (fun value -> values @ [ value ])))
                                (Ok [])
                        | _ -> Error "Project.wireSources must be a non-empty sequence."

                    if wires.Length <> (wires |> List.map _.Name |> List.distinct).Length then
                        return! Error "Wire source names must be unique."

                    let! binding = requiredScalar "Project" "binding" root
                    let! target = requiredScalar "Project" "target" root
                    let! outputNode = required "Project" "outputs" root
                    let! outputMap = mapping "Project.outputs" [ "image"; "header"; "inspect"; "activation" ] outputNode
                    let! image = requiredScalar "Project.outputs" "image" outputMap

                    let! header =
                        match Map.tryFind "header" outputMap with
                        | None -> Ok None
                        | Some node -> scalar "Project.outputs.header" node |> Result.map Some

                    let! inspect =
                        match Map.tryFind "inspect" outputMap with
                        | None -> Ok None
                        | Some node -> scalar "Project.outputs.inspect" node |> Result.map Some

                    let! activation =
                        match Map.tryFind "activation" outputMap with
                        | None -> Ok None
                        | Some node -> scalar "Project.outputs.activation" node |> Result.map Some

                    if not (image.EndsWith(".scimg", StringComparison.OrdinalIgnoreCase)) then
                        return! Error "Image output must end in .scimg."

                    if
                        header
                        |> Option.exists (fun p -> not (p.EndsWith(".h", StringComparison.OrdinalIgnoreCase)))
                    then
                        return! Error "Header output must end in .h."

                    if
                        inspect
                        |> Option.exists (fun p -> not (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                    then
                        return! Error "Inspect output must end in .json."

                    if
                        activation
                        |> Option.exists (fun p ->
                            not (p.EndsWith(".activation.json", StringComparison.OrdinalIgnoreCase)))
                    then
                        return! Error "Activation output must end in .activation.json."

                    return
                        ({ Name = name
                           Pool = pool
                           WireSources = wires
                           Binding = binding
                           Target = target
                           Outputs =
                             { Image = image
                               Header = header
                               Inspect = inspect
                               Activation = activation } }
                        : ProjectManifest)
                }

    let parse yaml =
        try
            parseInternal yaml |> Result.mapError (ProjectParseError >> List.singleton)
        with ex ->
            Error[ProjectParseError ex.Message]

    let private validateRelative field (value: string) =
        let segments = if isNull value then [||] else value.Split('/')

        if
            String.IsNullOrEmpty value
            || value.IndexOf('\000') >= 0
            || value.Contains('\\')
            || value.StartsWith('/')
            || Regex.IsMatch(value, "^[A-Za-z]:")
            || value.EndsWith('/')
            || segments |> Array.exists (fun s -> s = "" || s = "." || s = "..")
        then
            Error[ProjectParseError(sprintf "%s contains an unsafe project-relative path '%s'." field value)]
        else
            Ok value

    let private hasReparse root path =
        let relative = Path.GetRelativePath(root, path)
        let mutable current = root
        let mutable found = false

        for segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) do
            if segment <> "." then
                current <- Path.Combine(current, segment)

                if File.Exists(current) || Directory.Exists(current) then
                    try
                        if (File.GetAttributes(current) &&& FileAttributes.ReparsePoint) <> enum 0 then
                            found <- true
                    with _ ->
                        ()

        found

    let resolve (manifestPath: string) (manifest: ProjectManifest) =
        try
            let manifestFull = Path.GetFullPath(manifestPath)
            let root = Path.GetDirectoryName(manifestFull)

            let resolveOne field value =
                validateRelative field value
                |> Result.bind (fun safe ->
                    let full =
                        Path.GetFullPath(Path.Combine(root, safe.Replace('/', Path.DirectorySeparatorChar)))

                    let prefix =
                        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + string Path.DirectorySeparatorChar

                    if
                        not (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        || hasReparse root full
                    then
                        Error[ProjectParseError(
                                  sprintf
                                      "%s resolves outside the safe manifest root or through a reparse point."
                                      field
                              )]
                    else
                        Ok full)

            let poolText =
                match manifest.Pool with
                | Definition p
                | Manifest p -> p

            result {
                let! pool = resolveOne "pool" poolText

                let! wires =
                    manifest.WireSources
                    |> List.map (fun w ->
                        resolveOne ("wireSources." + w.Name) w.Path
                        |> Result.map (fun p -> { Name = w.Name; Path = p }: ResolvedWireSource))
                    |> List.fold
                        (fun state item ->
                            state
                            |> Result.bind (fun values -> item |> Result.map (fun value -> values @ [ value ])))
                        (Ok [])

                let! binding = resolveOne "binding" manifest.Binding
                let! target = resolveOne "target" manifest.Target
                let! image = resolveOne "outputs.image" manifest.Outputs.Image

                let! header =
                    match manifest.Outputs.Header with
                    | None -> Ok None
                    | Some p -> resolveOne "outputs.header" p |> Result.map Some

                let! inspect =
                    match manifest.Outputs.Inspect with
                    | None -> Ok None
                    | Some p -> resolveOne "outputs.inspect" p |> Result.map Some

                let! activation =
                    match manifest.Outputs.Activation with
                    | None -> Ok None
                    | Some p -> resolveOne "outputs.activation" p |> Result.map Some

                let inputs = manifestFull :: pool :: binding :: target :: (wires |> List.map _.Path)

                let outputs =
                    image :: (header |> Option.toList)
                    @ (inspect |> Option.toList)
                    @ (activation |> Option.toList)

                let comparer = StringComparer.OrdinalIgnoreCase

                let distinct (paths: string list) =
                    paths |> List.distinctBy (fun p -> p.ToUpperInvariant())

                if distinct inputs |> List.length <> inputs.Length then
                    return! Error[ProjectParseError "Input paths collide."]

                if distinct outputs |> List.length <> outputs.Length then
                    return! Error[ProjectParseError "Output paths collide."]

                if
                    outputs
                    |> List.exists (fun o -> inputs |> List.exists (fun input -> comparer.Equals(o, input)))
                then
                    return! Error[ProjectParseError "An output path collides with an input path."]

                for path in inputs do
                    if not (File.Exists(path)) || Directory.Exists(path) then
                        return!
                            Error[ProjectParseError(sprintf "Required input does not exist as a regular file: %s" path)]

                let rec regularFileAncestor (path: string) =
                    let parent = Path.GetDirectoryName(path)

                    if String.IsNullOrEmpty parent || Directory.Exists parent then
                        None
                    elif File.Exists parent then
                        Some parent
                    else
                        regularFileAncestor parent

                for path in outputs do
                    if File.Exists(path) || Directory.Exists(path) then
                        return! Error[ProjectParseError(sprintf "Output already exists: %s" path)]

                    match regularFileAncestor path with
                    | Some parent ->
                        return! Error[ProjectParseError(sprintf "Output parent is a regular file: %s" parent)]
                    | None -> ()

                return
                    { ManifestPath = manifestFull
                      RootDirectory = root
                      Name = manifest.Name
                      Pool =
                        (match manifest.Pool with
                         | Definition _ -> ResolvedDefinition pool
                         | Manifest _ -> ResolvedManifest pool)
                      WireSources = wires
                      Binding = binding
                      Target = target
                      Outputs =
                        { Image = image
                          Header = header
                          Inspect = inspect
                          Activation = activation } }
            }
        with ex ->
            Error[ProjectParseError ex.Message]
