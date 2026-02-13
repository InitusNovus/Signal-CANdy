# PowerShell script to generate and open HTML code coverage report
# Requires: dotnet test with coverlet.collector in test project references
# Prerequisites: Run 'dotnet tool restore' once to install ReportGenerator

Write-Host "🧹 Cleaning old coverage artifacts..." -ForegroundColor Green

# Remove old test results and coverage report
if (Test-Path "TestResults") {
    Remove-Item -Recurse -Force -Path "TestResults" | Out-Null
    Write-Host "  ✓ Removed TestResults/" -ForegroundColor Gray
}

if (Test-Path "coverage_report") {
    Remove-Item -Recurse -Force -Path "coverage_report" | Out-Null
    Write-Host "  ✓ Removed coverage_report/" -ForegroundColor Gray
}

Write-Host ""
Write-Host "🧪 Running tests with XPlat Code Coverage collection..." -ForegroundColor Green
Write-Host ""

# Run tests with code coverage
dotnet test `
    --configuration Release `
    --collect:"XPlat Code Coverage" `
    --results-directory TestResults `
    --logger "console;verbosity=normal"

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "❌ Tests failed or coverage collection failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "📊 Generating HTML coverage report..." -ForegroundColor Green
Write-Host ""

# Generate HTML report using ReportGenerator
dotnet tool run reportgenerator `
    -reports:"TestResults/**/coverage.cobertura.xml" `
    -targetdir:"coverage_report" `
    -reporttypes:Html

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "❌ ReportGenerator failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "✅ Coverage report generated successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "📂 Report location: coverage_report/index.html" -ForegroundColor Cyan
Write-Host "🌐 Opening in default browser..." -ForegroundColor Green
Write-Host ""

# Open the report in default browser
Start-Process "coverage_report/index.html"

Write-Host "✨ Done!" -ForegroundColor Green
