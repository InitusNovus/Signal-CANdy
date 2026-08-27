namespace Signal.CANdy.Core.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text
open Xunit
open Signal.CANdy.Core.ArtifactWriter

module ArtifactWriterTests =

    type private CommitStream(commit: byte array -> unit, failWrite: bool) =
        inherit MemoryStream()

        let mutable committed = false

        override this.Write(buffer, offset, count) =
            if failWrite then
                raise (IOException("injected write failure"))

            base.Write(buffer, offset, count)

        override this.Write(buffer: ReadOnlySpan<byte>) =
            if failWrite then
                raise (IOException("injected write failure"))

            base.Write(buffer)

        override this.Dispose(disposing) =
            if disposing && not committed then
                committed <- true
                commit (this.ToArray())

            base.Dispose(disposing)

    type private FakeFileSystem(?failCreateNumber: int, ?failWriteNumber: int, ?failMoveNumber: int) =
        let files = Dictionary<string, byte array>(StringComparer.OrdinalIgnoreCase)
        let directories = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let operations = ResizeArray<string>()
        let mutable creates = 0
        let mutable moves = 0

        member _.Files = files
        member _.Directories = directories
        member _.Operations = List.ofSeq operations

        member _.SeedFile(path, bytes) = files.[path] <- bytes

        interface IArtifactFileSystem with
            member _.FileExists(path) = files.ContainsKey(path)
            member _.DirectoryExists(path) = directories.Contains(path)

            member _.CreateDirectory(path) =
                operations.Add("mkdir:" + path)
                directories.Add(path) |> ignore

            member _.CreateNew(path) =
                creates <- creates + 1
                operations.Add("create:" + path)

                if failCreateNumber = Some creates then
                    raise (IOException("injected create failure"))

                if files.ContainsKey(path) then
                    raise (IOException("CreateNew collision"))

                let failWrite = failWriteNumber = Some creates
                new CommitStream((fun bytes -> files.[path] <- bytes), failWrite) :> Stream

            member _.Move(source, destination) =
                moves <- moves + 1
                operations.Add(sprintf "move:%s->%s" source destination)

                if failMoveNumber = Some moves then
                    raise (IOException("injected move failure"))

                let bytes = files.[source]
                files.Remove(source) |> ignore
                files.Add(destination, bytes)

            member _.DeleteFile(path) =
                operations.Add("delete:" + path)
                files.Remove(path) |> ignore

            member _.DeleteDirectory(path) =
                operations.Add("rmdir:" + path)
                directories.Remove(path) |> ignore

    let private unwrap result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "Expected success, got %A" errors

    let private artifact kind destination bytes =
        { Kind = kind
          Destination = destination
          Content = bytes }

    let private isTemporary (path: string) =
        Path.GetFileName(path).Contains(".signal-candy-", StringComparison.Ordinal)
        && path.EndsWith(".tmp", StringComparison.Ordinal)

    [<Fact>]
    let ``Header renderer is deterministic ASCII and derives symbols from image stem`` () =
        let bytes = [| for value in 0uy .. 31uy -> value |]

        let first =
            renderHeader "demo-project" "build/protection_demo.scimg" bytes |> unwrap

        let second =
            renderHeader "demo-project" "elsewhere/protection_demo.scimg" bytes |> unwrap

        Assert.Equal<byte>(first, second)
        Assert.All(first, fun value -> Assert.InRange(value, 0uy, 127uy))

        let text = Encoding.ASCII.GetString(first)
        Assert.DoesNotContain("\r", text)
        Assert.EndsWith("\n", text)
        Assert.Contains("GSCIMG_PROTECTION_DEMO_H", text)
        Assert.Contains("GSCIMG_PROTECTION_DEMO_BYTE_COUNT 32u", text)
        Assert.Contains("gScimgProtectionDemoBytes", text)

        let dataLines =
            text.Split('\n')
            |> Array.filter (fun line -> line.StartsWith("    0x", StringComparison.Ordinal))

        Assert.Equal(2, dataLines.Length)
        Assert.All(dataLines, fun line -> Assert.Equal(16, line.Split("0x").Length - 1))

    [<Theory>]
    [<InlineData(".scimg")>]
    [<InlineData("---.scimg")>]
    [<InlineData("é.scimg")>]
    let ``Header renderer rejects stems that cannot form a C identifier`` imagePath =
        renderHeader "demo" imagePath [| 1uy |] |> Result.isError |> Assert.True

    [<Fact>]
    let ``Real atomic writer leaves final bytes and no temporary residue`` () =
        let root =
            Path.Combine(Path.GetTempPath(), "signal-candy-artifacts-" + Guid.NewGuid().ToString("N"))

        let image = Path.Combine(root, "image", "demo.scimg")
        let header = Path.Combine(root, "header", "demo.h")

        try
            [ artifact Image image [| 1uy; 2uy |]; artifact Header header [| 3uy; 4uy |] ]
            |> writeAtomic
            |> Result.isOk
            |> Assert.True

            Assert.Equal<byte>([| 1uy; 2uy |], File.ReadAllBytes(image))
            Assert.Equal<byte>([| 3uy; 4uy |], File.ReadAllBytes(header))

            Assert.Empty(
                Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories)
                |> Seq.filter isTemporary
            )
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)

    [<Fact>]
    let ``Atomic writer stages and commits in image header inspect order`` () =
        let fs = FakeFileSystem()

        let artifacts =
            [ artifact Inspect "out/image.json" [| 3uy |]
              artifact Header "out/image.h" [| 2uy |]
              artifact Image "out/image.scimg" [| 1uy |] ]

        writeAtomicWith fs artifacts |> Result.isOk |> Assert.True
        Assert.Equal<byte>([| 1uy |], fs.Files.["out/image.scimg"])
        Assert.Equal<byte>([| 2uy |], fs.Files.["out/image.h"])
        Assert.Equal<byte>([| 3uy |], fs.Files.["out/image.json"])
        Assert.DoesNotContain(fs.Files.Keys, fun path -> isTemporary path)

        let moves =
            fs.Operations
            |> List.filter (fun operation -> operation.StartsWith("move:", StringComparison.Ordinal))

        Assert.Contains("image.scimg", moves.[0])
        Assert.Contains("image.h", moves.[1])
        Assert.Contains("image.json", moves.[2])

    [<Fact>]
    let ``Existing output prevents every directory and temporary creation`` () =
        let fs = FakeFileSystem()
        fs.SeedFile("out/image.h", [| 9uy |])

        let artifacts =
            [ artifact Image "out/image.scimg" [| 1uy |]
              artifact Header "out/image.h" [| 2uy |] ]

        writeAtomicWith fs artifacts |> Result.isError |> Assert.True
        Assert.Equal<byte>([| 9uy |], fs.Files.["out/image.h"])
        Assert.Empty(fs.Operations)

    [<Fact>]
    let ``Existing output directory prevents staging`` () =
        let fs = FakeFileSystem()
        fs.Directories.Add("out/image.scimg") |> ignore

        [ artifact Image "out/image.scimg" [| 1uy |] ]
        |> writeAtomicWith fs
        |> Result.isError
        |> Assert.True

        Assert.Empty(fs.Operations)

    [<Fact>]
    let ``Temporary write failure removes all staged files finals and created directories`` () =
        let fs = FakeFileSystem(failWriteNumber = 2)

        let artifacts =
            [ artifact Image "image/image.scimg" [| 1uy |]
              artifact Header "header/image.h" [| 2uy |]
              artifact Inspect "inspect/image.json" [| 3uy |] ]

        writeAtomicWith fs artifacts |> Result.isError |> Assert.True
        Assert.Empty(fs.Files)
        Assert.Empty(fs.Directories)
        Assert.DoesNotContain(fs.Files.Keys, fun path -> isTemporary path)

    [<Fact>]
    let ``Second rename failure rolls back first final and every temporary`` () =
        let fs = FakeFileSystem(failMoveNumber = 2)

        let artifacts =
            [ artifact Image "out/image.scimg" [| 1uy |]
              artifact Header "out/image.h" [| 2uy |]
              artifact Inspect "out/image.json" [| 3uy |] ]

        writeAtomicWith fs artifacts |> Result.isError |> Assert.True
        Assert.Empty(fs.Files)
        Assert.Empty(fs.Directories)
        Assert.DoesNotContain(fs.Files.Keys, fun path -> isTemporary path)

    [<Fact>]
    let ``Atomic writer rejects duplicate case-only destinations before staging`` () =
        let fs = FakeFileSystem()

        [ artifact Image "out/image.scimg" [| 1uy |]
          artifact Header "OUT/IMAGE.SCIMG" [| 2uy |] ]
        |> writeAtomicWith fs
        |> Result.isError
        |> Assert.True

        Assert.Empty(fs.Operations)
