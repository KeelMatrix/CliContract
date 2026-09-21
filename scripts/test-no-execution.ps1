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

$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-no-execution-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $archive = Join-Path $temp 'source.zip'
    $extracted = Join-Path $temp 'source'
    & git -C $root archive -o $archive HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the clean archive regression input.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted
    $archiveCheck = Join-Path $extracted 'scripts/check-no-execution.ps1'

    $cleanOutput = & pwsh -NoProfile -File $archiveCheck 2>&1
    $cleanExitCode = $LASTEXITCODE
    $cleanOutput | Out-Host
    if ($cleanExitCode -eq 0) { throw 'The no-execution check accepted a clean archive without .git.' }
    if (-not ($cleanOutput -match 'git ls-files')) { throw 'The clean-archive failure did not identify git ls-files enumeration.' }

    $archiveFixture = Join-Path $extracted 'fixtures/no-execution/forbidden-reference.txt'
    $explicitOutput = & pwsh -NoProfile -File $archiveCheck -AdditionalPath $archiveFixture 2>&1
    $explicitExitCode = $LASTEXITCODE
    $explicitOutput | Out-Host
    if ($explicitExitCode -eq 0) { throw 'The no-execution check accepted an explicit file from a clean archive.' }
    if (-not ($explicitOutput -match 'git ls-files')) { throw 'The explicit-file clean-archive failure did not identify git ls-files enumeration.' }
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
Write-Output 'PASS: the no-execution check failed closed for clean archives with and without an explicit file.'
