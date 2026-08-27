namespace Signal.CANdy.Core.Tests

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open Xunit
open Xunit.Sdk
open Signal.CANdy.Core
open Signal.CANdy.Core.Binding
open Signal.CANdy.Core.Ir
open Signal.CANdy.Core.Pool

module DifferentialTests =

    type private FrameVector =
        { Name: string
          Message: Message
          Data: byte array
          Expectations: (string * uint64 * uint32) list }

    let private unwrap description result =
        match result with
        | Ok value -> value
        | Error errors -> failwithf "%s failed: %A" description errors

    let private repoRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private fixturePath name =
        Path.Combine(repoRoot, "examples", "scimg_demo", name)

    let private normalizedStartBit (signal: Signal) =
        match signal.ByteOrder with
        | Little -> int signal.StartBit
        | Big ->
            let finalBit = int signal.StartBit + int signal.Length - 1
            (finalBit / 8) * 8 + (7 - finalBit % 8)

    let private extractUnsigned (signal: Signal) (data: byte array) =
        let startBit = normalizedStartBit signal
        let length = int signal.Length

        let getBit bitIndex =
            uint64 ((data.[bitIndex / 8] >>> (bitIndex % 8)) &&& 1uy)

        match signal.ByteOrder with
        | Little ->
            [ 0 .. length - 1 ]
            |> List.fold (fun value index -> value ||| (getBit (startBit + index) <<< index)) 0UL
        | Big ->
            [ 0 .. length - 1 ]
            |> List.fold (fun value index -> (value <<< 1) ||| getBit (startBit + index)) 0UL

    let private signExtend length value =
        if length < 64 && (value &&& (1UL <<< (length - 1))) <> 0UL then
            value ||| (UInt64.MaxValue <<< length)
        else
            value

    let private referenceSlot (ir: Ir) (pool: PoolContract) (bindings: BindingSet) messageName poolSignalName data =
        let binding =
            bindings.Bindings
            |> List.find (fun candidate -> candidate.PoolSignalName = poolSignalName)

        if binding.MessageName <> messageName then
            failwithf "Binding '%s' does not belong to message '%s'." poolSignalName messageName

        let message =
            ir.Messages |> List.find (fun candidate -> candidate.Name = messageName)

        let signal =
            message.Signals
            |> List.find (fun candidate -> candidate.Name = binding.WireSignalName)

        let poolSignal =
            pool.Signals |> List.find (fun candidate -> candidate.Name = poolSignalName)

        let extracted = extractUnsigned signal data

        let integerValue =
            if signal.IsSigned then
                signExtend (int signal.Length) extracted
            else
                extracted

        let numeric =
            if signal.IsSigned then
                float (int64 integerValue)
            else
                float integerValue

        let factor, offset =
            match binding.Conversion with
            | Identity -> signal.Factor, signal.Offset
            | Affine(bindingFactor, bindingOffset) -> bindingFactor, bindingOffset

        let physical = numeric * factor + offset

        match poolSignal.Storage with
        | U8
        | U16
        | U32
        | U64
        | I8
        | I16
        | I32
        | I64 -> integerValue
        | F32 -> uint64 (uint32 (BitConverter.SingleToInt32Bits(float32 physical)))
        | F64 -> uint64 (BitConverter.DoubleToInt64Bits(physical))

    let private setSignalBits (signal: Signal) value (data: byte array) =
        let startBit = normalizedStartBit signal
        let length = int signal.Length

        let setBit bitIndex bit =
            let byteIndex = bitIndex / 8
            let mask = 1uy <<< (bitIndex % 8)

            if bit then
                data.[byteIndex] <- data.[byteIndex] ||| mask
            else
                data.[byteIndex] <- data.[byteIndex] &&& ~~~mask

        match signal.ByteOrder with
        | Little ->
            for index in 0 .. length - 1 do
                setBit (startBit + index) (((value >>> index) &&& 1UL) <> 0UL)
        | Big ->
            for index in 0 .. length - 1 do
                setBit (startBit + index) (((value >>> (length - 1 - index)) &&& 1UL) <> 0UL)

    let private makeData (message: Message) signalValues =
        let data = Array.zeroCreate<byte> (int message.Length)

        for signalName, value in signalValues do
            let signal =
                message.Signals |> List.find (fun candidate -> candidate.Name = signalName)

            setSignalBits signal value data

        data

    let private findExecutable () =
        let fixedGcc = @"C:\msys64\ucrt64\bin\gcc.exe"

        if File.Exists(fixedGcc) then
            Some fixedGcc
        else
            let locator, arguments =
                if OperatingSystem.IsWindows() then
                    "where.exe", [ "gcc" ]
                else
                    "which", [ "gcc" ]

            try
                let startInfo = ProcessStartInfo(locator)
                startInfo.UseShellExecute <- false
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                arguments |> List.iter startInfo.ArgumentList.Add
                use childProcess = Process.Start(startInfo)
                let stdout = childProcess.StandardOutput.ReadToEnd()
                childProcess.WaitForExit()

                if childProcess.ExitCode = 0 then
                    stdout.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.tryHead
                    |> Option.filter File.Exists
                else
                    None
            with _ ->
                None

    let private runProcess executable arguments =
        let startInfo = ProcessStartInfo(executable)
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        arguments |> List.iter startInfo.ArgumentList.Add
        use childProcess = Process.Start(startInfo)
        let stdout = childProcess.StandardOutput.ReadToEnd()
        let stderr = childProcess.StandardError.ReadToEnd()
        childProcess.WaitForExit()
        childProcess.ExitCode, stdout, stderr

    [<Fact>]
    let ``Differential compiler image agrees with independent fixture evaluator`` () =
        let gcc =
            match findExecutable () with
            | Some path -> path
            | None -> raise (SkipException.ForSkip("gcc was not found; skipping C99 differential test."))

        let tempDirectory =
            Path.Combine(Path.GetTempPath(), "scimgdiff-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempDirectory) |> ignore

        try
            let dbcPath = fixturePath "demo.dbc"
            let poolPath = fixturePath "pool.json"
            let bindingPath = fixturePath "binding.json"
            let ir = Dbc.parseDbcFile dbcPath |> unwrap "DBC parse"
            let wire = Wire.toWireModel ir |> unwrap "Wire normalization"

            let pool =
                File.ReadAllText(poolPath) |> Pool.parsePoolDefinition |> unwrap "Pool parse"

            let bindings =
                File.ReadAllText(bindingPath)
                |> Binding.parseBindingSet
                |> unwrap "Binding parse"

            let linked = Linked.link pool wire bindings |> unwrap "Link"
            let image = Scimg.lower linked |> unwrap "Image lowering"
            let firstBytes = Scimg.write image |> unwrap "First image write"
            let secondBytes = Scimg.write image |> unwrap "Second image write"
            Assert.Equal<byte>(firstBytes, secondBytes)

            let message name =
                ir.Messages |> List.find (fun candidate -> candidate.Name = name)

            let std = message "DEMO_STD"
            let big = message "DEMO_BE"
            let mux = message "DEMO_MUX"

            let activeExpectations messageName data names =
                names
                |> List.map (fun name -> name, referenceSlot ir pool bindings messageName name data, 3u)

            let frame name message signalValues expectationNames =
                let data = makeData message signalValues

                { Name = name
                  Message = message
                  Data = data
                  Expectations = activeExpectations message.Name data expectationNames }

            let inactiveMux name selector =
                let data =
                    makeData mux [ "Mux_sel", selector; "Base_u8", 0xA5UL; "Branch_a", 0xBEEFUL ]

                { Name = name
                  Message = mux
                  Data = data
                  Expectations =
                    [ "MuxSelector", referenceSlot ir pool bindings mux.Name "MuxSelector" data, 3u
                      "MuxBranchA", 0UL, 0u ] }

            let vectors =
                [ frame "temp-raw-0" std [ "Temp_raw", 0UL; "Counter", 0UL ] [ "CabinTemperature"; "TripCounter" ]
                  frame
                      "temp-raw-1234"
                      std
                      [ "Temp_raw", 1234UL; "Counter", 127UL ]
                      [ "CabinTemperature"; "TripCounter" ]
                  frame
                      "temp-raw-65535"
                      std
                      [ "Temp_raw", 65535UL; "Counter", 255UL ]
                      [ "CabinTemperature"; "TripCounter" ]
                  frame
                      "big-speed-0-signed-minus-1"
                      big
                      [ "Speed_be", 0UL; "Signed_be", 0xFFUL ]
                      [ "VehicleSpeed"; "AccelStep" ]
                  frame
                      "big-speed-4660-signed-minus-128"
                      big
                      [ "Speed_be", 0x1234UL; "Signed_be", 0x80UL ]
                      [ "VehicleSpeed"; "AccelStep" ]
                  frame
                      "big-speed-65535-signed-minus-1"
                      big
                      [ "Speed_be", 0xFFFFUL; "Signed_be", 0xFFUL ]
                      [ "VehicleSpeed"; "AccelStep" ]
                  inactiveMux "mux-selector-0-inactive" 0UL
                  frame
                      "mux-selector-1-branch-1"
                      mux
                      [ "Mux_sel", 1UL; "Base_u8", 0x11UL; "Branch_a", 1UL ]
                      [ "MuxSelector"; "MuxBranchA" ]
                  frame
                      "mux-selector-1-branch-65535"
                      mux
                      [ "Mux_sel", 1UL; "Base_u8", 0x22UL; "Branch_a", 0xFFFFUL ]
                      [ "MuxSelector"; "MuxBranchA" ]
                  inactiveMux "mux-selector-2-inactive" 2UL ]

            Assert.True(vectors.Length >= 10, sprintf "Expected at least 10 frame vectors, got %d." vectors.Length)

            let slotByName =
                pool.Signals |> List.mapi (fun index signal -> signal.Name, index) |> Map.ofList

            let vectorsText = StringBuilder()

            vectorsText.AppendLine("# Generated by DifferentialTests.fs; see diff_harness.c for the format.")
            |> ignore

            for vector in vectors do
                let encodedData =
                    vector.Data
                    |> Array.map (fun value -> value.ToString("X2"))
                    |> String.concat " "

                vectorsText.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "F {0} {1} {2} {3}\n",
                    vector.Message.Id,
                    (if vector.Message.IsExtended then 1 else 0),
                    vector.Data.Length,
                    encodedData
                )
                |> ignore

                for signalName, raw, flags in vector.Expectations do
                    vectorsText.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "E {0} {1} {2}\n",
                        slotByName.[signalName],
                        raw,
                        flags
                    )
                    |> ignore

            let imagePath = Path.Combine(tempDirectory, "fixture.scimg")
            let vectorsPath = Path.Combine(tempDirectory, "vectors.txt")
            let executablePath = Path.Combine(tempDirectory, "diff_harness.exe")
            File.WriteAllBytes(imagePath, firstBytes)
            File.WriteAllText(vectorsPath, vectorsText.ToString())

            let includePath = Path.Combine(repoRoot, "runtime", "c99", "include")

            let runtimePath =
                Path.Combine(repoRoot, "runtime", "c99", "src", "signal_candy_runtime.c")

            let harnessPath =
                Path.Combine(repoRoot, "runtime", "c99", "tests", "diff_harness.c")

            let compileArguments =
                [ "-std=c99"
                  "-Wall"
                  "-Wextra"
                  "-Werror"
                  "-O2"
                  "-I" + includePath
                  runtimePath
                  harnessPath
                  "-o"
                  executablePath ]

            let compileExit, compileStdout, compileStderr = runProcess gcc compileArguments

            Assert.True(
                (compileExit = 0),
                sprintf
                    "gcc failed with exit code %d.\nstdout:\n%s\nstderr:\n%s"
                    compileExit
                    compileStdout
                    compileStderr
            )

            let runExit, runStdout, runStderr =
                runProcess executablePath [ imagePath; vectorsPath ]

            Assert.True(
                (runExit = 0),
                sprintf
                    "Differential harness failed with exit code %d.\nstdout:\n%s\nstderr:\n%s"
                    runExit
                    runStdout
                    runStderr
            )
        finally
            if Directory.Exists(tempDirectory) then
                Directory.Delete(tempDirectory, true)
