$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$check = Join-Path $root 'scripts/check-no-execution.ps1'
$fixture = Join-Path $root 'fixtures/no-execution/forbidden-reference.txt'
$output = & pwsh -NoProfile -File $check -AdditionalPath $fixture 2>&1
$exitCode = $LASTEXITCODE
$output | Out-Host
if ($exitCode -eq 0) { throw 'The no-execution check accepted the forbidden-reference fixture.' }
if (-not ($output -match 'ProcessStartInfo')) { throw 'The no-execution check did not report the forbidden-reference fixture.' }
Write-Output 'PASS: the no-execution check rejected the forbidden-reference fixture.'
