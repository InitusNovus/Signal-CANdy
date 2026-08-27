namespace Signal.CANdy.Hardening.Tests

open System
open System.Diagnostics
open System.IO

module TestSupport =

    let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    type ProcessResult =
        { ExitCode: int
          StandardOutput: string
          StandardError: string }

    let private runProcessCore (timeoutMilliseconds: int) workingDirectory fileName arguments =
        let startInfo = ProcessStartInfo(fileName)
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true

        for argument in arguments do
            startInfo.ArgumentList.Add(argument)

        use child = new Process(StartInfo = startInfo)

        if not (child.Start()) then
            failwithf "could not start %s" fileName

        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()

        if not (child.WaitForExit(timeoutMilliseconds)) then
            child.Kill(true)
            failwithf "%s did not exit within %d milliseconds" fileName timeoutMilliseconds

        { ExitCode = child.ExitCode
          StandardOutput = output.GetAwaiter().GetResult()
          StandardError = error.GetAwaiter().GetResult() }

    let runProcess workingDirectory fileName arguments =
        runProcessCore 30000 workingDirectory fileName arguments

    let runProcessWithTimeout (timeoutMilliseconds: int) workingDirectory fileName arguments =
        runProcessCore timeoutMilliseconds workingDirectory fileName arguments

    let withTempDirectory action =
        let path =
            Path.Combine(Path.GetTempPath(), "signal-candy-hardening-tests", Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(path) |> ignore

        try
            action path
        finally
            if Directory.Exists(path) then
                Directory.Delete(path, true)
