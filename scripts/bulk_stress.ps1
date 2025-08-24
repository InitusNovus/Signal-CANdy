param(
  [string]$Config = "examples/config_directmap_fixed.yaml",
  [string]$OutDir = "gen"
)

$ErrorActionPreference = "Stop"

function Invoke-Stress {
  param(
    [string]$DbcPath
  )
  Write-Host "=== Processing DBC: $DbcPath ==="

  dotnet run --project src/Generator -- --dbc "$DbcPath" --out "$OutDir" --config "$Config"
  make -C "$OutDir" build
  & "$OutDir/build/test_runner" "test_stress_suite"
}

# Collect DBCs from examples and external_test
$dbcFiles = @()
if (Test-Path "examples") {
  $dbcFiles += Get-ChildItem -Path "examples" -Filter *.dbc -File | ForEach-Object { $_.FullName }
}
if (Test-Path "external_test") {
  $dbcFiles += Get-ChildItem -Path "external_test" -Filter *.dbc -File | ForEach-Object { $_.FullName }
}

if ($dbcFiles.Count -eq 0) {
  Write-Host "No DBC files found under examples/ or external_test/. Add files and re-run." -ForegroundColor Yellow
  exit 1
}

# Optional: write simple reports
$reportRoot = Join-Path -Path (Resolve-Path ".").Path -ChildPath "tmp/stress_reports"
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null

foreach ($dbc in $dbcFiles) {
  $name = [IO.Path]::GetFileNameWithoutExtension($dbc)
  $report = Join-Path $reportRoot "$name.txt"
  try {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $output = Invoke-Stress -DbcPath $dbc 2>&1 | Out-String
    $sw.Stop()
    "DBC: $dbc`nTime: $($sw.Elapsed.ToString())`n--- Output ---`n$output" | Out-File -FilePath $report -Encoding UTF8
    Write-Host "Saved report -> $report" -ForegroundColor Green
  }
  catch {
    "DBC: $dbc`nERROR: $($_.Exception.Message)" | Out-File -FilePath $report -Encoding UTF8
    Write-Host "Error processing $dbc (see $report)" -ForegroundColor Red
  }
}

Write-Host "All done. Reports in $reportRoot" -ForegroundColor Cyan
