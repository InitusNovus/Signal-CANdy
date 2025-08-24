param(
  [string]$ReportsDir = "tmp/stress_reports",
  [string]$OutCsv = "tmp/stress_reports/summary.csv"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $ReportsDir)) {
  Write-Error "Reports directory not found: $ReportsDir"
}

$rows = @()
$files = Get-ChildItem -Path $ReportsDir -Filter *.txt -File
foreach ($f in $files) {
  $dbc = [IO.Path]::GetFileNameWithoutExtension($f.Name)
  $lines = Get-Content -Path $f.FullName
  $currentTest = $null
  $timeSec = $null
  $passed = $null
  $failed = $null
  $rate = $null
  foreach ($line in $lines) {
    if ($line -match '^Starting stress test: (.+) \(') {
      $currentTest = $Matches[1].Trim()
      $timeSec = $null; $passed = $null; $failed = $null; $rate = $null
    }
    elseif ($line -match '^  Time: ([0-9\.]+) seconds') {
      $timeSec = [double]$Matches[1]
    }
    elseif ($line -match '^  Results: ([0-9]+) passed, ([0-9]+) failed') {
      $passed = [int]$Matches[1]
      $failed = [int]$Matches[2]
    }
    elseif ($line -match '^  Rate: ([0-9\.]+) ops/sec') {
      $rate = [double]$Matches[1]
      if ($currentTest) {
        $rows += [pscustomobject]@{
          DBC = $dbc
          Test = $currentTest
          TimeSec = $timeSec
          Passed = $passed
          Failed = $failed
          RateOpsPerSec = $rate
        }
      }
    }
  }
}

if ($rows.Count -eq 0) {
  Write-Warning "No test entries parsed. Check report format or directory."
}

# Export CSV (UTF8)
$rows | Sort-Object DBC, Test | Export-Csv -Path $OutCsv -NoTypeInformation -Encoding UTF8
Write-Host "Saved summary -> $OutCsv" -ForegroundColor Green
