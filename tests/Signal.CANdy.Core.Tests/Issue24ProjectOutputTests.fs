namespace Signal.CANdy.Core.Tests

open System
open System.Collections.Generic
open System.IO
open Xunit
open Signal.CANdy.Core
open Signal.CANdy.Core.ArtifactWriter
open Signal.CANdy.Core.ProjectManifest

module Issue24ProjectOutputTests =

    let private yaml mapLine =
        """format: sc.project/v1
name: issue24-map
pool:
  definition: pool.json
wireSources:
  - name: source
    type: dbc
    path: source.dbc
binding: binding.json
target: target.json
outputs:
  image: build/image.scimg
  header: build/image.h
  inspect: build/image.inspect.json
"""
        + mapLine
        + "  activation: build/image.activation.json\n"

    let private unwrap description result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "%s failed: %A" description errors

    let private withRoot action =
        let root =
            Path.Combine(Path.GetTempPath(), "signal-candy-issue24-project-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        try
            [ "project.yaml"; "pool.json"; "source.dbc"; "binding.json"; "target.json" ]
            |> List.iter (fun name -> File.WriteAllText(Path.Combine(root, name), "fixture"))

            action root
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)

    let private resolve root text =
        let path = Path.Combine(root, "project.yaml")
        File.WriteAllText(path, text)

        Signal.CANdy.Core.ProjectManifest.parse text
        |> Result.bind (Signal.CANdy.Core.ProjectManifest.resolve path)

    [<Fact>]
    let ``Project outputs map is optional exact suffix checked and manifest relative`` () =
        let old = Signal.CANdy.Core.ProjectManifest.parse (yaml "") |> unwrap "old project"
        Assert.Equal(None, old.Outputs.Map)

        let parsed =
            Signal.CANdy.Core.ProjectManifest.parse (yaml "  map: build/image.map.json\n")
            |> unwrap "map project"

        Assert.Equal(Some "build/image.map.json", parsed.Outputs.Map)

        [ "  map: build/image.json\n"
          "  map: build/image.map.txt\n"
          "  map: build/image.map.json.tmp\n" ]
        |> List.iter (fun line ->
            yaml line
            |> Signal.CANdy.Core.ProjectManifest.parse
            |> Result.isError
            |> Assert.True)

        withRoot (fun root ->
            let resolved =
                resolve root (yaml "  map: build/image.map.json\n") |> unwrap "resolve"

            Assert.Equal(Some(Path.Combine(root, "build", "image.map.json")), resolved.Outputs.Map)
            Assert.False(Directory.Exists(Path.Combine(root, "build"))))

    [<Fact>]
    let ``Map output participates in input output collision and existing destination checks`` () =
        withRoot (fun root ->
            File.WriteAllText(Path.Combine(root, "pool.map.json"), "fixture")

            yaml "  map: pool.map.json\n"
            |> _.Replace("definition: pool.json", "definition: pool.map.json")
            |> resolve root
            |> Result.isError
            |> Assert.True

            yaml "  map: build/IMAGE.MAP.JSON\n"
            |> _.Replace("build/image.inspect.json", "build/image.map.json")
            |> resolve root
            |> Result.isError
            |> Assert.True

            Directory.CreateDirectory(Path.Combine(root, "build")) |> ignore
            File.WriteAllText(Path.Combine(root, "build", "image.map.json"), "existing")

            yaml "  map: build/image.map.json\n"
            |> resolve root
            |> Result.isError
            |> Assert.True)

    type private CommitStream(commit: byte array -> unit) =
        inherit MemoryStream()

        override this.Dispose(disposing) =
            if disposing then
                commit (this.ToArray())

            base.Dispose(disposing)

    type private RecordingFileSystem(failMove: int option) =
        let files = Dictionary<string, byte array>(StringComparer.OrdinalIgnoreCase)
        let directories = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let moves = ResizeArray<string>()
        let mutable moveCount = 0

        member _.Files = files
        member _.Directories = directories
        member _.Moves = List.ofSeq moves

        interface IArtifactFileSystem with
            member _.FileExists path = files.ContainsKey(path)
            member _.DirectoryExists path = directories.Contains(path)
            member _.CreateDirectory path = directories.Add(path) |> ignore

            member _.CreateNew path =
                new CommitStream(fun bytes -> files.[path] <- bytes) :> Stream

            member _.Move(source, destination) =
                moveCount <- moveCount + 1
                moves.Add(destination)

                if failMove = Some moveCount then
                    raise (IOException("injected map move failure"))

                let bytes = files.[source]
                files.Remove(source) |> ignore
                files.Add(destination, bytes)

            member _.DeleteFile path = files.Remove(path) |> ignore
            member _.DeleteDirectory path = directories.Remove(path) |> ignore

    let private artifact kind path value =
        { Kind = kind
          Destination = path
          Content = [| value |] }

    [<Fact>]
    let ``Map artifact commits after legacy inspect and before activation`` () =
        let fs = RecordingFileSystem(None)

        [ artifact Activation "out/image.activation.json" 5uy
          artifact Map "out/image.map.json" 4uy
          artifact Inspect "out/image.inspect.json" 3uy
          artifact Header "out/image.h" 2uy
          artifact Image "out/image.scimg" 1uy ]
        |> writeAtomicWith fs
        |> Result.isOk
        |> Assert.True

        Assert.Equal<string list>(
            [ "out/image.scimg"
              "out/image.h"
              "out/image.inspect.json"
              "out/image.map.json"
              "out/image.activation.json" ],
            fs.Moves
        )

    [<Fact>]
    let ``Map publication failure rolls back every final temporary and directory`` () =
        let fs = RecordingFileSystem(Some 4)

        [ artifact Image "out/image.scimg" 1uy
          artifact Header "out/image.h" 2uy
          artifact Inspect "out/image.inspect.json" 3uy
          artifact Map "out/image.map.json" 4uy
          artifact Activation "out/image.activation.json" 5uy ]
        |> writeAtomicWith fs
        |> Result.isError
        |> Assert.True

        Assert.Empty(fs.Files)
        Assert.Empty(fs.Directories)
