param(
    [Parameter(Mandatory = $true)] [string] $Package,
    [Parameter(Mandatory = $true)] [string] $Symbols,
    [string] $NuGetExecutable = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$apiKey = $env:NUGET_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey)) { throw 'NUGET_API_KEY is required.' }

& $NuGetExecutable nuget push $Package --api-key $apiKey --source https://api.nuget.org/v3/index.json --no-symbols
if ($LASTEXITCODE -ne 0) { throw "Primary package push failed with exit code $LASTEXITCODE; symbol push was not attempted." }

& $NuGetExecutable nuget push $Symbols --api-key $apiKey --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw "Symbol package push failed with exit code $LASTEXITCODE." }
