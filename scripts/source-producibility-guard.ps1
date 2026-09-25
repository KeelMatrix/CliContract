param(
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$started = Get-Date
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$env:KEELMATRIX_NO_TELEMETRY = '1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-source-guard-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    & dotnet test tests/KeelMatrix.CliContract.Tests/KeelMatrix.CliContract.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SourceProducibilityGuardTests --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The source-producibility edge matrix failed.' }
    Write-Output 'CASE=source-field-edge-matrix PASS'

    if (-not $PackagePath) {
        $packageDirectory = Join-Path $temp 'package'
        New-Item -ItemType Directory -Path $packageDirectory | Out-Null
        & dotnet pack src/KeelMatrix.CliContract.Tool/KeelMatrix.CliContract.Tool.csproj -c Release --no-restore --no-build --output $packageDirectory --nologo
        if ($LASTEXITCODE -ne 0) { throw 'The guard package could not be created.' }
        $PackagePath = Join-Path $packageDirectory 'KeelMatrix.CliContract.0.1.0.nupkg'
    }

    & pwsh -NoProfile -File ./scripts/package-consumer-smoke.ps1 -PackagePath $PackagePath -SelfTest
    if ($LASTEXITCODE -ne 0) { throw 'The installed-package source-producibility guard failed.' }
    Write-Output 'CASE=installed-source-round-trip-and-hostile-set PASS'
    $durationMs = [math]::Round(((Get-Date) - $started).TotalMilliseconds)
    Write-Output "SOURCE_PRODUCIBILITY_GUARD=PASS duration_ms=$durationMs"
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
