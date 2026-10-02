param(
    [string] $Version = '0.1.0',
    [string] $ExpectedCommit,
    [switch] $RequireFinalizedChangelog
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = Split-Path -Parent $PSScriptRoot
$launchGuard = Join-Path $root 'build/Test-NestedPwshLaunch.ps1'
& $launchGuard -SelfTest
if ($LASTEXITCODE -ne 0) { throw 'Nested PowerShell launch guard self-test failed.' }
& $launchGuard
if ($LASTEXITCODE -ne 0) { throw 'Nested PowerShell launch guard failed.' }
Set-Location $root
Invoke-NestedPwsh -NoProfile -File ./scripts/verify-option-name-oracle.ps1
if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $ExpectedCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the frozen candidate SHA.' }
}
if ($ExpectedCommit -notmatch '^[0-9a-f]{40}$') { throw 'A frozen 40-character candidate SHA is required.' }
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$packageDir = Join-Path $root 'artifacts/packages'
if (Test-Path -LiteralPath $packageDir) { Remove-Item -LiteralPath $packageDir -Recurse -Force }
New-Item -ItemType Directory -Path $packageDir | Out-Null
$started = Get-Date
function Assert-NativeSuccess([string] $step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed with exit code $LASTEXITCODE." }
}
Assert-NativeSuccess 'Pinned option-name oracle'

dotnet restore KeelMatrix.CliContract.sln --configfile NuGet.config --nologo
Assert-NativeSuccess 'Restore'
dotnet format KeelMatrix.CliContract.sln --verify-no-changes --no-restore
Assert-NativeSuccess 'Format verification'
dotnet build KeelMatrix.CliContract.sln -c Release --no-restore --nologo
Assert-NativeSuccess 'Release build'
dotnet test KeelMatrix.CliContract.sln -c Release --no-build --nologo
Assert-NativeSuccess 'Release tests'
Invoke-NestedPwsh -NoProfile -File ./scripts/contract-regressions.ps1
Assert-NativeSuccess 'Consumer contract regressions'
Invoke-NestedPwsh -NoProfile -File ./scripts/verify-determinism.ps1
Assert-NativeSuccess 'Determinism verification'
Invoke-NestedPwsh -NoProfile -File ./scripts/check-no-execution.ps1
Assert-NativeSuccess 'No-execution source scan'
Invoke-NestedPwsh -NoProfile -File ./scripts/test-no-execution.ps1
Assert-NativeSuccess 'No-execution harness'
Invoke-NestedPwsh -NoProfile -File ./scripts/scan-user-facing-surface.ps1 -SelfTest
Assert-NativeSuccess 'User-facing wording scan'
Invoke-NestedPwsh -NoProfile -File ./scripts/verify-error-taxonomy.ps1
Assert-NativeSuccess 'Error taxonomy documentation check'
Invoke-NestedPwsh -NoProfile -File ./scripts/validate-sensitive-paths.ps1 -SelfTest
Assert-NativeSuccess 'Sensitive-path ingress safety'
Invoke-NestedPwsh -NoProfile -File ./scripts/scan-history-wording.ps1 -SelfTest
Assert-NativeSuccess 'Reachable history wording scan'
Invoke-NestedPwsh -NoProfile -File ./scripts/verify-release-tag.ps1 -SelfTest
Assert-NativeSuccess 'Release tag self-test'
Invoke-NestedPwsh -NoProfile -File ./scripts/verify-release-contract.ps1 -SelfTest
Assert-NativeSuccess 'Release contract self-test'
Invoke-NestedPwsh -NoProfile -File ./scripts/verify-workflow-safety.ps1 -SelfTest
Assert-NativeSuccess 'Workflow safety self-test'
Invoke-NestedPwsh -NoProfile -File ./scripts/verify-workflow-safety.ps1
Assert-NativeSuccess 'Workflow safety validation'
if ($RequireFinalizedChangelog) {
    Invoke-NestedPwsh -NoProfile -File ./scripts/verify-release-contract.ps1 -Version $Version -Tag "v$Version"
    Assert-NativeSuccess 'Finalized release contract'
}
$audit = dotnet list KeelMatrix.CliContract.sln package --vulnerable --include-transitive --configfile NuGet.config 2>&1
$audit | Out-Host
if (($audit -join "`n") -match '(?im)^\s*[>]?.*Package.*\s+has the following vulnerable packages|(?im)^\s*>\s+.*\s+(Critical|High|Moderate|Low)\s+') { throw 'Dependency vulnerability audit reported a vulnerable package.' }
Assert-NativeSuccess 'Dependency vulnerability audit'
dotnet pack src/KeelMatrix.CliContract.Tool/KeelMatrix.CliContract.Tool.csproj -c Release --no-build -p:Version=$Version --include-symbols --output $packageDir --nologo
Assert-NativeSuccess 'Package build'
$package = Join-Path $packageDir "KeelMatrix.CliContract.$Version.nupkg"
$symbols = Join-Path $packageDir "KeelMatrix.CliContract.$Version.snupkg"
if (-not (Test-Path -LiteralPath $symbols)) { throw 'Expected symbol package was not produced.' }
Invoke-NestedPwsh -NoProfile -File ./scripts/verify-package-reproducibility.ps1 -PackagePath $package -SymbolPackagePath $symbols -SelfTest
Assert-NativeSuccess 'Package reproducibility'
Invoke-NestedPwsh -NoProfile -File ./scripts/verify-release-artifacts.ps1 -ArtifactDirectory $packageDir -Version $Version -ExpectedCommit $ExpectedCommit -SelfTest
Assert-NativeSuccess 'Package artifact provenance and allowlist'
Invoke-NestedPwsh -NoProfile -File ./scripts/inspect-package.ps1 -PackagePath $package -SymbolPackagePath $symbols -ExpectedCommit $ExpectedCommit -SelfTest
Assert-NativeSuccess 'Package inspection'
Invoke-NestedPwsh -NoProfile -File ./scripts/source-producibility-guard.ps1 -PackagePath $package
Assert-NativeSuccess 'Source-producibility guard and package consumer smoke'
$elapsed = (Get-Date) - $started
Write-Output ("LOCAL_GATE=PASS version={0} commit={1} duration_ms={2}" -f $Version, $ExpectedCommit, [Math]::Round($elapsed.TotalMilliseconds))
