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

function Push-ReleaseArtifact([string] $Path, [string] $Kind) {
    # Explicit snupkg pushes need SymbolPackagePublish discovery from the v3
    # index. A direct upload URL can exit zero without sending anything.
    $pushArguments = @('nuget', 'push', $Path, '--source', 'https://api.nuget.org/v3/index.json',
        '--api-key', $env:RELEASE_NUGET_CREDENTIAL, '--force-english-output')
    if ($Kind -eq 'package') { $pushArguments += '--no-symbols' }
    $output = @(dotnet @pushArguments 2>&1 | ForEach-Object { $_.ToString() })
    $code = $LASTEXITCODE
    $output | Write-Output
    if ($code -ne 0) { throw "NuGet $Kind publication failed with exit code $code." }

    # With the pinned SDK, the English receipt follows a successful PUT.
    # Require the documented acceptance response, too: an empty/no-op command
    # or an unrelated 2xx response must not become a release success claim.
    $text = $output -join "`n"
    $put = [regex]::Match($text, '(?m)^\s+PUT (?<uri>\S+)\s*$')
    $status = if ($Kind -eq 'symbol') { 'Created' } else { '(?:Created|Accepted)' }
    $accepted = [regex]::Match($text, "(?m)^\s+$status (?<uri>\S+) \d+ms\s*$")
    if (-not $put.Success -or -not $accepted.Success -or
        $put.Groups['uri'].Value -cne $accepted.Groups['uri'].Value -or
        $output -cnotcontains 'Your package was pushed.') {
        throw "NuGet $Kind publication returned no accepted upload receipt. Stop and reconcile provider state before another attempt."
    }
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
Push-ReleaseArtifact -Path $package -Kind 'package'
Push-ReleaseArtifact -Path $symbols -Kind 'symbol'
Write-Output "RELEASE_PUBLICATION=PASS version=$Version commit=$ExpectedCommit"
