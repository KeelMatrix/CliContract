$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$env:KEELMATRIX_NO_TELEMETRY = '1'
$packageDir = Join-Path $root 'artifacts/packages'
if (Test-Path -LiteralPath $packageDir) { Remove-Item -LiteralPath $packageDir -Recurse -Force }
New-Item -ItemType Directory -Path $packageDir | Out-Null
$started = Get-Date
function Assert-NativeSuccess([string] $step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed with exit code $LASTEXITCODE." }
}
dotnet restore KeelMatrix.CliContract.sln --configfile NuGet.config --nologo
Assert-NativeSuccess 'Restore'
dotnet format KeelMatrix.CliContract.sln --verify-no-changes --no-restore
Assert-NativeSuccess 'Format verification'
dotnet build KeelMatrix.CliContract.sln -c Release --no-restore --nologo
Assert-NativeSuccess 'Release build'
dotnet test KeelMatrix.CliContract.sln -c Release --no-build --nologo
Assert-NativeSuccess 'Release tests'
& pwsh -NoProfile -File ./scripts/contract-regressions.ps1
Assert-NativeSuccess 'Consumer contract regressions'
& pwsh -NoProfile -File ./scripts/verify-determinism.ps1
Assert-NativeSuccess 'Determinism verification'
& pwsh -NoProfile -File ./scripts/check-no-execution.ps1
Assert-NativeSuccess 'No-execution source scan'
& pwsh -NoProfile -File ./scripts/test-no-execution.ps1
Assert-NativeSuccess 'No-execution harness'
& pwsh -NoProfile -File ./scripts/scan-user-facing-surface.ps1 -SelfTest
Assert-NativeSuccess 'User-facing wording scan'
 $audit = dotnet list KeelMatrix.CliContract.sln package --vulnerable --include-transitive --configfile NuGet.config 2>&1
 $audit | Out-Host
 if (($audit -join "`n") -match '(?im)^\s*[>]?\s*.*Package.*\s+has the following vulnerable packages|(?im)^\s*>\s+.*\s+(Critical|High|Moderate|Low)\s+') { throw 'Dependency vulnerability audit reported a vulnerable package.' }
Assert-NativeSuccess 'Dependency vulnerability audit'
dotnet pack src/KeelMatrix.CliContract.Tool/KeelMatrix.CliContract.Tool.csproj -c Release --no-build --include-symbols --output $packageDir --nologo
Assert-NativeSuccess 'Package build'
$package = Join-Path $packageDir 'KeelMatrix.CliContract.0.1.0.nupkg'
$symbols = Join-Path $packageDir 'KeelMatrix.CliContract.0.1.0.snupkg'
if (-not (Test-Path -LiteralPath $symbols)) { throw 'Expected symbol package was not produced.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$symbolEntries = [IO.Compression.ZipFile]::OpenRead($symbols).Entries | ForEach-Object FullName
if (-not ($symbolEntries | Where-Object { $_ -like '*.pdb' })) { throw 'Symbol package contains no PDB entries.' }
Write-Output "SYMBOL_PACKAGE=PASS entries=$(@($symbolEntries).Count)"
& pwsh -NoProfile -File ./scripts/inspect-package.ps1 -PackagePath $package -SelfTest
Assert-NativeSuccess 'Package inspection'
& pwsh -NoProfile -File ./scripts/package-consumer-smoke.ps1 -PackagePath $package
Assert-NativeSuccess 'Package consumer smoke'
$elapsed = (Get-Date) - $started
Write-Output ("LOCAL_GATE=PASS duration_ms={0}" -f [Math]::Round($elapsed.TotalMilliseconds))
