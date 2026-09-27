param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,
    [Parameter(Mandatory = $true)]
    [string] $SymbolPackagePath
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/KeelMatrix.CliContract.Tool/KeelMatrix.CliContract.Tool.csproj'
$packageName = Split-Path -Leaf $PackagePath
$symbolName = Split-Path -Leaf $SymbolPackagePath
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-reproducibility-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $temp | Out-Null
    $output = @(& dotnet pack $project -c Release --no-build --no-restore --include-symbols --output $temp --nologo 2>&1)
    $exitCode = $LASTEXITCODE
    $output | Out-Host
    if ($exitCode -ne 0) { throw "Repeat package build failed with exit code $exitCode." }

    $repeatPackage = Join-Path $temp $packageName
    $repeatSymbols = Join-Path $temp $symbolName
    if (-not (Test-Path -LiteralPath $repeatPackage) -or -not (Test-Path -LiteralPath $repeatSymbols)) {
        throw 'Repeat package build did not produce both expected archives.'
    }

    $packageHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
    $repeatPackageHash = (Get-FileHash -LiteralPath $repeatPackage -Algorithm SHA256).Hash
    $symbolHash = (Get-FileHash -LiteralPath $SymbolPackagePath -Algorithm SHA256).Hash
    $repeatSymbolHash = (Get-FileHash -LiteralPath $repeatSymbols -Algorithm SHA256).Hash
    if ($packageHash -ne $repeatPackageHash -or $symbolHash -ne $repeatSymbolHash) {
        throw "Repeated package build changed artifact identity. nupkg=$packageHash/$repeatPackageHash snupkg=$symbolHash/$repeatSymbolHash"
    }
    Write-Output "PACKAGE_REPRODUCIBILITY=PASS nupkg_sha256=$packageHash snupkg_sha256=$symbolHash"
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
