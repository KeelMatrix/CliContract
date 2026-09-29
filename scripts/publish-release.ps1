param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [Parameter(Mandatory = $true)] [string] $Tag,
    [Parameter(Mandatory = $true)] [string] $ExpectedCommit,
    [string] $ArtifactDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/packages'),
    [switch] $Publish,
    [switch] $SkipPreparation
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ($Tag -ne "v$Version") { throw "Release tag $Tag does not match version $Version." }
if ($ExpectedCommit -notmatch '^[0-9a-f]{40}$') { throw 'Expected commit must be a 40-character hexadecimal SHA.' }

function Assert-NativeSuccess([string] $Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE." }
}

if (-not $SkipPreparation) {
    Invoke-NestedPwsh -NoProfile -File (Join-Path $root 'scripts/verify-release-tag.ps1') -Tag $Tag
    Assert-NativeSuccess 'Release tag validation'
    Invoke-NestedPwsh -NoProfile -File (Join-Path $root 'scripts/local-gate.ps1') -Version $Version -ExpectedCommit $ExpectedCommit -RequireFinalizedChangelog
    Assert-NativeSuccess 'Release-equivalent validation'
}
else {
    Invoke-NestedPwsh -NoProfile -File (Join-Path $root 'scripts/verify-release-contract.ps1') -Version $Version -Tag $Tag
    Assert-NativeSuccess 'Finalized release contract validation'
    Invoke-NestedPwsh -NoProfile -File (Join-Path $root 'scripts/verify-release-artifacts.ps1') -ArtifactDirectory $ArtifactDirectory -Version $Version -ExpectedCommit $ExpectedCommit
    Assert-NativeSuccess 'Validated artifact set'
}

$package = Join-Path $ArtifactDirectory "KeelMatrix.CliContract.$Version.nupkg"
$symbols = Join-Path $ArtifactDirectory "KeelMatrix.CliContract.$Version.snupkg"
foreach ($path in @($package, $symbols)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Validated release artifact is missing: $path" } }
if (-not $Publish) {
    Write-Output "RELEASE_PREPARED=PASS version=$Version commit=$ExpectedCommit package=$package symbols=$symbols"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($env:RELEASE_NUGET_CREDENTIAL)) {
    throw 'The OIDC login output is required in RELEASE_NUGET_CREDENTIAL before publication.'
}
dotnet nuget push $package --source https://api.nuget.org/v3/index.json --api-key $env:RELEASE_NUGET_CREDENTIAL --no-symbols
Assert-NativeSuccess 'NuGet package publication'
dotnet nuget push $symbols --source https://symbols.nuget.org/upload --api-key $env:RELEASE_NUGET_CREDENTIAL
Assert-NativeSuccess 'NuGet symbol publication'
Write-Output "RELEASE_PUBLICATION=PASS version=$Version commit=$ExpectedCommit"
