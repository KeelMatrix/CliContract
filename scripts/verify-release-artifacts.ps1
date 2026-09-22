param(
    [Parameter(Mandatory = $true)] [string] $ArtifactDirectory,
    [Parameter(Mandatory = $true)] [string] $Version
)

$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$expected = @(
    "KeelMatrix.CliContract.$Version.nupkg",
    "KeelMatrix.CliContract.$Version.snupkg"
)
$actual = @(Get-ChildItem -LiteralPath $directory -File | Select-Object -ExpandProperty Name | Sort-Object)
$unexpected = @($actual | Where-Object { $_ -notin $expected })
$missing = @($expected | Where-Object { $_ -notin $actual })
if ($unexpected.Count -gt 0) { throw "Unexpected release artifact(s): $($unexpected -join ', ')" }
if ($missing.Count -gt 0) { throw "Missing release artifact(s): $($missing -join ', ')" }
foreach ($name in $expected) {
    $path = Join-Path $directory $name
    if ((Get-Item -LiteralPath $path).Length -eq 0) { throw "Release artifact is empty: $name" }
}
Write-Output "ARTIFACT_ALLOWLIST=PASS files=$($actual -join ',')"
