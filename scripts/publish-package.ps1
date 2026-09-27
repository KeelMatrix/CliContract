param(
    [Parameter(Mandatory = $true)] [string] $Package,
    [Parameter(Mandatory = $true)] [string] $Symbols,
    [Parameter(Mandatory = $true)] [string] $Credential,
    [string] $NuGetExecutable = 'dotnet'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Credential)) { throw 'A temporary NuGet credential is required.' }
foreach ($path in @($Package, $Symbols)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release artifact is missing: $path" }
}

& $NuGetExecutable nuget push $Package --api-key $Credential --source https://api.nuget.org/v3/index.json --no-symbols
if ($LASTEXITCODE -ne 0) { throw "Primary package push failed with exit code $LASTEXITCODE; symbol push was not attempted." }

& $NuGetExecutable nuget push $Symbols --api-key $Credential --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw "Symbol package push failed with exit code $LASTEXITCODE." }

Write-Output 'PACKAGE_PUBLISH=PASS'
