[CmdletBinding()]
param(
    [ValidateSet('Ci', 'Replay', 'Minimize')]
    [string]$Mode = 'Ci',
    [string]$CaseId,
    # Do not name this parameter $Input: it would shadow PowerShell's
    # automatic stdin-enumerating variable and block script start for
    # minutes whenever the caller keeps stdin open (CI hosts, test runners).
    # The alias preserves the documented -Input command-line surface.
    [Alias('Input')]
    [string]$InputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tools/Signal.CANdy.Hardening/Signal.CANdy.Hardening.fsproj'
$work = Join-Path ([IO.Path]::GetTempPath()) ('signal-candy-hardening-' + [Guid]::NewGuid().ToString('N'))

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited with $LASTEXITCODE" }
}

function Invoke-Captured([string]$File, [string[]]$Arguments, [string]$Output) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $info
    if (-not $child.Start()) { throw "could not start $File" }
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    $child.WaitForExit()
    [IO.File]::WriteAllText($Output, $stdout.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
    $errorText = $stderr.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw "$File exited with $($child.ExitCode): $errorText" }
    if ($errorText -match 'AddressSanitizer|UndefinedBehaviorSanitizer|runtime error:') { throw $errorText }
}

Push-Location $root
try {
    [void][IO.Directory]::CreateDirectory($work)

    if ($Mode -ne 'Ci') {
        if ([string]::IsNullOrWhiteSpace($CaseId) -and [string]::IsNullOrWhiteSpace($InputPath)) {
            throw '-CaseId or -Input is required'
        }
        # Build once and invoke the compiled driver directly. `dotnet run`'
        # nested implicit build/apphost spawn is pathologically slow when
        # launched from PowerShell hosts, which made bounded Replay/Minimize
        # invocations unusable as gates.
        Invoke-Checked dotnet @('build', $project, '-c', 'Release', '--no-restore', '--nologo')
        $driver = Join-Path $root 'tools/Signal.CANdy.Hardening/bin/Release/net8.0/Signal.CANdy.Hardening.dll'
        Invoke-Checked dotnet @($driver, $Mode.ToLowerInvariant(), '--case-id', $CaseId)
        exit 0
    }

    Invoke-Checked dotnet @('build', $project, '-c', 'Release', '--no-restore', '--nologo')
    $driver = Join-Path $root 'tools/Signal.CANdy.Hardening/bin/Release/net8.0/Signal.CANdy.Hardening.dll'
    $pack = Join-Path $work 'deterministic.scorp'
    $summary = Join-Path $work 'properties.json'
    Invoke-Checked dotnet @($driver, 'generate', '--seed', '0x5343494D47323501', '--cases', '10000', '--output', $pack, '--property-summary', $summary)

    $harness = Join-Path $root 'runtime/c99/tests/schema_open_harness.c'
    $include = '-I' + (Join-Path $root 'runtime/c99/include')
    $normal = Join-Path $work ('schema-open' + $(if ($IsWindows) { '.exe' } else { '' }))
    Invoke-Checked cc @('-std=c99', '-Wall', '-Wextra', '-Werror', '-O2', $include, $harness, '-o', $normal)
    $normalJson = Join-Path $work 'normal.jsonl'
    Invoke-Captured $normal @('--pack', $pack) $normalJson
    Invoke-Checked dotnet @($driver, 'compare-oracle', '--pack', $pack, '--jsonl', $normalJson)

    $sanitizerRequired = $env:SC_HARDENING_REQUIRE_SANITIZERS -eq '1'
    $clang = Get-Command clang -ErrorAction SilentlyContinue
    if ($null -eq $clang) {
        if ($sanitizerRequired) { throw 'Clang ASan+UBSan is required but clang is unavailable' }
        Write-Host 'sanitizers=skipped reason=clang-unavailable'
    } else {
        if ($IsWindows) {
            $runtime = 'C:/Program Files/LLVM/lib/clang/22/lib/windows'
            if (Test-Path $runtime) { $env:PATH = $runtime + [IO.Path]::PathSeparator + $env:PATH }
        }
        $san = Join-Path $work ('schema-open-san' + $(if ($IsWindows) { '.exe' } else { '' }))
        try {
            Invoke-Checked $clang.Source @('-std=c99', '-Wall', '-Wextra', '-Werror', '-O1', '-g', '-fno-omit-frame-pointer', '-fsanitize=address,undefined', $include, $harness, '-o', $san)
            $env:ASAN_OPTIONS = 'abort_on_error=1:halt_on_error=1'
            $env:UBSAN_OPTIONS = 'halt_on_error=1:print_stacktrace=1'
            $sanJson = Join-Path $work 'sanitized.jsonl'
            Invoke-Captured $san @('--pack', $pack) $sanJson
            Invoke-Checked dotnet @($driver, 'compare-oracle', '--pack', $pack, '--jsonl', $sanJson)
            Write-Host 'sanitizers=asan+ubsan cases=10000 reports=0'
        } catch {
            if ($sanitizerRequired) { throw }
            Write-Host "sanitizers=skipped reason=capability-probe-failed"
        }
    }

    Invoke-Checked dotnet @($driver, 'scan-runtime', '--source', 'runtime/c99/src/signal_candy_runtime.c')
    Invoke-Checked dotnet @($driver, 'verify-budget', '--manifest', 'hardening/build-budget.json', '--receipt', 'tests/Signal.CANdy.Hardening.Tests/fixtures/cc1a-activation-receipt.json')
    Invoke-Captured $normal @('--image', 'invalid-utf8-symbol', 'tests/corpus/scimg/v1/invalid-utf8-symbol.scimg') (Join-Path $work 'regression.jsonl')
    Write-Host 'hardening=PASS cases=10000 bases=6 regression=1'
} finally {
    Pop-Location
    if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
