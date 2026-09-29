param(
    [Parameter(Mandatory = $true)] [string] $ArtifactDirectory,
    [Parameter(Mandatory = $true)] [string] $Version,
    [Parameter(Mandatory = $true)] [string] $ExpectedCommit,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ($ExpectedCommit -notmatch '^[0-9a-f]{40}$') { throw 'Expected commit must be a 40-character hexadecimal SHA.' }
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

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-CorePropertiesEntry {
    param(
        [string] $ArchivePath,
        [string[]] $Entries,
        [string] $ExpectedPackageId = 'KeelMatrix.CliContract',
    [string] $ExpectedVersion
    )

    $metadataEntries = @($Entries | Where-Object { $_ -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$' })
    if ($metadataEntries.Count -ne 1) {
        throw "Package must contain exactly one core-properties metadata entry; found $($metadataEntries.Count)."
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($metadataEntries[0])
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $content = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }

    try { [xml] $document = $content }
    catch { throw 'Core-properties metadata is not valid XML.' }
    if ($document.DocumentElement.LocalName -ne 'coreProperties' -or $document.DocumentElement.NamespaceURI -ne 'http://schemas.openxmlformats.org/package/2006/metadata/core-properties') {
        throw 'Core-properties metadata has an unexpected XML root.'
    }

    $namespaces = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace('cp', 'http://schemas.openxmlformats.org/package/2006/metadata/core-properties')
    $namespaces.AddNamespace('dc', 'http://purl.org/dc/elements/1.1/')
    $identifier = $document.SelectSingleNode('/cp:coreProperties/dc:identifier', $namespaces)
    $version = $document.SelectSingleNode('/cp:coreProperties/cp:version', $namespaces)
    if ($null -eq $identifier -or $identifier.InnerText -ne $ExpectedPackageId) { throw 'Core-properties metadata package identifier is incorrect.' }
    if ($null -eq $version -or $version.InnerText -ne $ExpectedVersion) { throw 'Core-properties metadata package version is incorrect.' }
}

$packagePath = Join-Path $directory "KeelMatrix.CliContract.$Version.nupkg"
$packageEntries = @([IO.Compression.ZipFile]::OpenRead($packagePath).Entries | ForEach-Object FullName)
Assert-CorePropertiesEntry -ArchivePath $packagePath -Entries $packageEntries -ExpectedVersion $Version
$symbolsPath = Join-Path $directory "KeelMatrix.CliContract.$Version.snupkg"
$symbolEntries = @([IO.Compression.ZipFile]::OpenRead($symbolsPath).Entries | ForEach-Object FullName)
$symbolMetadataEntries = @($symbolEntries | Where-Object { $_ -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$' })
Assert-CorePropertiesEntry -ArchivePath $symbolsPath -Entries $symbolEntries -ExpectedVersion $Version
$expectedSymbolPayload = @(
    'tools/net8.0/any/KeelMatrix.CliContract.pdb',
    'tools/net8.0/any/KeelMatrix.CliContract.Core.pdb'
)
$symbolPdbEntries = @($symbolEntries | Where-Object { $_ -match '\.pdb$' })
if ((@($symbolPdbEntries | Sort-Object) -join '|') -ne (@($expectedSymbolPayload | Sort-Object) -join '|')) {
    throw "Unexpected or missing symbol payload: $($symbolPdbEntries -join ', ')"
}
$symbolNuspecs = @($symbolEntries | Where-Object { $_ -like '*.nuspec' })
if ($symbolNuspecs.Count -ne 1) { throw 'Symbol package must contain exactly one nuspec.' }
$allowedSymbolEntries = @('_rels/.rels', '[Content_Types].xml', $symbolNuspecs[0]) + $expectedSymbolPayload + $symbolMetadataEntries
$unexpectedSymbolEntries = @($symbolEntries | Where-Object {
    $_ -notin $allowedSymbolEntries
})
if ($unexpectedSymbolEntries.Count -gt 0) { throw "Unexpected symbol package entries: $($unexpectedSymbolEntries -join ', ')" }
Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'inspect-package.ps1') -PackagePath $packagePath -SymbolPackagePath $symbolsPath -RepositoryRoot $root -ExpectedCommit $ExpectedCommit
if ($LASTEXITCODE -ne 0) { throw 'Package provenance and payload inspection failed.' }
Write-Output "ARTIFACT_ALLOWLIST=PASS files=$($actual -join ',')"
Write-Output "SYMBOL_ALLOWLIST=PASS entries=$($symbolEntries -join ',')"

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-symbols-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $selfTestRoot | Out-Null
        Copy-Item -LiteralPath (Join-Path $directory "KeelMatrix.CliContract.$Version.nupkg") -Destination $selfTestRoot
        $mutatedPackage = Join-Path $selfTestRoot "KeelMatrix.CliContract.$Version.nupkg"
        $mutatedSymbols = Join-Path $selfTestRoot "KeelMatrix.CliContract.$Version.snupkg"
        Copy-Item -LiteralPath $symbolsPath -Destination $mutatedSymbols
        $missingRoot = Join-Path $selfTestRoot 'missing-artifact'
        New-Item -ItemType Directory -Path $missingRoot | Out-Null
        Copy-Item -LiteralPath $symbolsPath -Destination (Join-Path $missingRoot (Split-Path -Leaf $symbolsPath))
        $missingOutput = @(Invoke-NestedPwsh -NoProfile -File $PSCommandPath -ArtifactDirectory $missingRoot -Version $Version -ExpectedCommit $ExpectedCommit 2>&1)
        $missingExit = $LASTEXITCODE
        if ($missingExit -eq 0) { throw 'Release artifact validation accepted a missing package artifact.' }
        Write-Output "PACKAGE_MISSING_NEGATIVE_SELF_TEST=PASS child_exit=$missingExit"

        $extraRoot = Join-Path $selfTestRoot 'extra-artifact'
        New-Item -ItemType Directory -Path $extraRoot | Out-Null
        Copy-Item -LiteralPath $packagePath -Destination (Join-Path $extraRoot (Split-Path -Leaf $packagePath))
        Copy-Item -LiteralPath $symbolsPath -Destination (Join-Path $extraRoot (Split-Path -Leaf $symbolsPath))
        Set-Content -LiteralPath (Join-Path $extraRoot 'unexpected.txt') -Value 'unexpected' -Encoding utf8NoBOM
        $extraOutput = @(Invoke-NestedPwsh -NoProfile -File $PSCommandPath -ArtifactDirectory $extraRoot -Version $Version -ExpectedCommit $ExpectedCommit 2>&1)
        $extraExit = $LASTEXITCODE
        if ($extraExit -eq 0) { throw 'Release artifact validation accepted an extra artifact.' }
        Write-Output "PACKAGE_EXTRA_NEGATIVE_SELF_TEST=PASS child_exit=$extraExit"

        $archive = [IO.Compression.ZipFile]::Open($mutatedPackage, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $entry = $archive.CreateEntry('package/services/metadata/core-properties/unexpected.psmdcp')
            $stream = $entry.Open()
            try { $stream.Write([byte[]](1, 2, 3), 0, 3) }
            finally { $stream.Dispose() }
        }
        finally { $archive.Dispose() }
        $packageOutput = @(Invoke-NestedPwsh -NoProfile -File $PSCommandPath -ArtifactDirectory $selfTestRoot -Version $Version -ExpectedCommit $ExpectedCommit 2>&1)
        $packageExit = $LASTEXITCODE
        if ($packageExit -eq 0) { throw 'Release artifact validation accepted an extra package metadata entry.' }
        Write-Output "PACKAGE_METADATA_NEGATIVE_SELF_TEST=PASS child_exit=$packageExit"

        Copy-Item -LiteralPath $packagePath -Destination $mutatedPackage -Force
        Copy-Item -LiteralPath $symbolsPath -Destination $mutatedSymbols -Force
        $archive = [IO.Compression.ZipFile]::Open($mutatedSymbols, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $entry = $archive.CreateEntry('package/services/metadata/core-properties/unexpected.psmdcp')
            $stream = $entry.Open()
            try { $stream.Write([byte[]](1, 2, 3), 0, 3) }
            finally { $stream.Dispose() }
        }
        finally { $archive.Dispose() }
        $childOutput = @(Invoke-NestedPwsh -NoProfile -File $PSCommandPath -ArtifactDirectory $selfTestRoot -Version $Version -ExpectedCommit $ExpectedCommit 2>&1)
        $childExit = $LASTEXITCODE
        if ($childExit -eq 0) { throw 'Release artifact validation accepted an extra symbol metadata entry.' }
        Write-Output "SYMBOL_METADATA_NEGATIVE_SELF_TEST=PASS child_exit=$childExit"
    }
    finally {
        if (Test-Path -LiteralPath $selfTestRoot) { Remove-Item -LiteralPath $selfTestRoot -Recurse -Force }
    }
}
