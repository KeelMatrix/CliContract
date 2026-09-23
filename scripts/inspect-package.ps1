param(
    [Parameter(Mandatory = $true)] [string] $PackagePath,
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch] $AllowMissingIcon,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$packagePath = (Resolve-Path -LiteralPath $PackagePath).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ForbiddenPatterns {
    $partA = 'pr' + 'obe'
    $partB = 'ph' + 'ase'
    $partC = 'evi' + 'dence'
    $partD = 'orche' + 'stration'
    $partE = 'co' + 'dex'
    $partF = 'Paper' + 'clip'
    $partG = 'fron' + 'tier'
    $partH = 'ag' + 'ent'
    $partI = 'mo' + 'del'
    $partJ = 'INTER' + 'NAL'
    $partK = 'anal' + 'ysis'
    $partL = 'er' + 'ror'
    $partM = 'KE' + 'E-'
    $partN = 'ta' + 'sk'
    $partO = 'com' + 'pany'

    $pattern = '(?i)\b(' + $partA + '|' + $partB + '[ -]?0|' + $partC + '|' + $partD + '|' + $partE + '|' + $partF + '|' + $partG + '|' + $partH + '|' + $partI + '|' + $partI + '[- ]?routing|' + $partN + '[- ]?id|' + $partH + '[- ]?id|' + $partO + '[- ]?' + $partJ + '|' + $partJ + '[_ -]?' + $partL + '|' + $partJ + '\s+' + $partK + '\s+' + $partL + '|' + $partM + '\d+)\b'
    return $pattern
}

function Add-ArchiveMarker {
    param(
        [string] $ArchivePath,
        [string] $Marker
    )

    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry('README.md')
        if ($null -eq $entry) { throw 'Package README entry is required for the gate self-test.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $content = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $entry.Delete()
        $replacement = $archive.CreateEntry('README.md')
        $writer = [IO.StreamWriter]::new($replacement.Open(), [Text.UTF8Encoding]::new($false))
        try {
            $writer.Write($content)
            $writer.WriteLine()
            $writer.Write($Marker)
        }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Add-ArchiveEntry {
    param(
        [string] $ArchivePath,
        [string] $EntryName,
        [string] $Content = 'fixture'
    )

    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.CreateEntry($EntryName)
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($Content) }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Assert-CorePropertiesEntry {
    param(
        [string] $ArchivePath,
        [string[]] $Entries,
        [string] $ExpectedPackageId = 'KeelMatrix.CliContract',
        [string] $ExpectedVersion = '0.1.0'
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

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-package-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $selfTestRoot | Out-Null
        $mutatedPackage = Join-Path $selfTestRoot 'mutated.nupkg'
        Copy-Item -LiteralPath $packagePath -Destination $mutatedPackage
        Add-ArchiveMarker -ArchivePath $mutatedPackage -Marker ('pr' + 'obe')
        $childOutput = @(& pwsh -NoProfile -File $PSCommandPath -PackagePath $mutatedPackage -RepositoryRoot $root 2>&1)
        $childExit = $LASTEXITCODE
        if ($childExit -eq 0) {
            throw 'Package wording gate accepted an injected forbidden term.'
        }
        Write-Output "PACKAGE_NON_VACUITY_CHILD_EXIT=$childExit"
        Write-Output 'PACKAGE_NON_VACUITY=PASS'

        foreach ($case in @(
            @{ Name = 'unexpected-assembly'; Entry = 'tools/net8.0/any/Unexpected.dll' },
            @{ Name = 'sensitive-local-json'; Entry = 'tools/net8.0/any/telemetry.local.json' },
            @{ Name = 'extra-core-properties'; Entry = 'package/services/metadata/core-properties/unexpected.psmdcp' }
        )) {
            $casePackage = Join-Path $selfTestRoot ($case.Name + '.nupkg')
            Copy-Item -LiteralPath $packagePath -Destination $casePackage
            Add-ArchiveEntry -ArchivePath $casePackage -EntryName $case.Entry
            $caseOutput = @(& pwsh -NoProfile -File $PSCommandPath -PackagePath $casePackage -RepositoryRoot $root 2>&1)
            $caseExit = $LASTEXITCODE
            if ($caseExit -eq 0) { throw "Package inspection accepted self-test case $($case.Name)." }
            Write-Output "PACKAGE_NEGATIVE_SELF_TEST=$($case.Name) child_exit=$caseExit"
        }
        Write-Output 'PACKAGE_NEGATIVE_SELF_TEST=PASS'
    }
    finally {
        if (Test-Path -LiteralPath $selfTestRoot) { Remove-Item -LiteralPath $selfTestRoot -Recurse -Force }
    }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $temp)
    $entries = [IO.Compression.ZipFile]::OpenRead($packagePath).Entries | ForEach-Object FullName
    $metadataEntries = @($entries | Where-Object { $_ -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$' })
    Assert-CorePropertiesEntry -ArchivePath $packagePath -Entries $entries
    $nuspecName = $entries | Where-Object { $_ -like '*.nuspec' }
    if (@($nuspecName).Count -ne 1) { throw 'Package must contain exactly one nuspec.' }
    [xml]$nuspec = Get-Content -Raw (Join-Path $temp $nuspecName)
    $metadata = $nuspec.package.metadata
    $iconVerified = $false
    if ($metadata.id -ne 'KeelMatrix.CliContract') { throw "Unexpected package id: $($metadata.id)" }
    if ($metadata.version -ne '0.1.0') { throw "Unexpected package version: $($metadata.version)" }
    if ($metadata.license.GetAttribute('type') -ne 'expression' -or $metadata.license.InnerText -ne 'MIT') { throw 'Package license metadata is not MIT.' }
    if ($metadata.readme -ne 'README.md') { throw 'Package README metadata is not README.md.' }
    $iconPath = Join-Path $root 'icon.png'
    if ($metadata.icon -ne 'icon.png') {
        if (-not $AllowMissingIcon -or (Test-Path -LiteralPath $iconPath)) { throw 'Package icon metadata is not icon.png.' }
        Write-Output 'ICON_GATE=UNVERIFIED package icon metadata and package-root icon are absent.'
    }
    if ($metadata.repository.url -ne 'https://github.com/KeelMatrix/CliContract') { throw 'Repository metadata is incorrect.' }
    $requiredToolPayload = @(
        'tools/net8.0/any/DotnetToolSettings.xml',
        'tools/net8.0/any/KeelMatrix.CliContract.dll',
        'tools/net8.0/any/KeelMatrix.CliContract.runtimeconfig.json',
        'tools/net8.0/any/KeelMatrix.CliContract.pdb',
        'tools/net8.0/any/KeelMatrix.CliContract.deps.json',
        'tools/net8.0/any/KeelMatrix.CliContract.Core.dll',
        'tools/net8.0/any/KeelMatrix.CliContract.Core.pdb',
        'tools/net8.0/any/KeelMatrix.CliContract.xml',
        'tools/net8.0/any/KeelMatrix.Telemetry.dll',
        'tools/net8.0/any/YamlDotNet.dll'
    )
    $required = @('README.md', 'KeelMatrix.CliContract.nuspec') + $requiredToolPayload
    foreach ($entry in $required) { if ($entries -notcontains $entry) { throw "Required package entry is missing: $entry" } }
    if ($entries -notcontains 'LICENSE') { throw 'The package must contain LICENSE.' }
    $allowedEntries = @('_rels/.rels', '[Content_Types].xml', 'README.md', 'LICENSE', 'icon.png', $nuspecName) + $requiredToolPayload + $metadataEntries
    $unexpected = @($entries | Where-Object {
        $_ -notin $allowedEntries
    })
    if ($unexpected.Count -gt 0) { throw "Unexpected package entries: $($unexpected -join ', ')" }
    $sensitive = @($entries | Where-Object {
        $_ -match '(?i)(^|/)(\.env(?:$|[._-])|appsettings(?:$|[._-])|telemetry(?:$|[._-])|secrets?(?:$|[._-])|.*(?:\.local|[-_.]secret|[-_.]config)\.(?:json|xml|yml|yaml|config)$|.*\.(?:key|pfx)$|AGENTS\.md|.*test.*)'
    })
    if ($sensitive.Count -gt 0) { throw "Sensitive or test package entries found: $($sensitive -join ', ')" }
    $pdbEntries = @($entries | Where-Object { $_ -match '\.pdb$' })
    $expectedPdbEntries = @($requiredToolPayload | Where-Object { $_ -match '\.pdb$' })
    if ((@($pdbEntries | Sort-Object) -join '|') -ne (@($expectedPdbEntries | Sort-Object) -join '|')) { throw "Unexpected or missing package symbol entries: $($pdbEntries -join ', ')" }
    $forbiddenSurfacePatterns = @(Get-ForbiddenPatterns)
    foreach ($entry in $entries | Where-Object { $_ -notmatch '\.(dll|pdb)$' }) {
        $text = Get-Content -Raw -LiteralPath (Join-Path $temp $entry)
        foreach ($pattern in $forbiddenSurfacePatterns) {
            if ($text -match $pattern) { throw "Forbidden user-facing wording found in package entry: $entry" }
        }
    }
    if (-not (Test-Path -LiteralPath $iconPath)) {
        if (-not $AllowMissingIcon) { throw 'Required icon path is missing: repository-root icon.png' }
        Write-Output 'ICON_GATE=UNVERIFIED repository-root icon.png is absent.'
    }
    else {
        if ($entries -notcontains 'icon.png') { throw 'Package-root icon.png is missing.' }
        $iconBytes = [IO.File]::ReadAllBytes($iconPath)
        if ($iconBytes.Length -gt 200KB) { throw 'Repository icon exceeds the 200 KB limit.' }
        if ($iconBytes.Length -lt 24 -or $iconBytes[0] -ne 0x89 -or $iconBytes[1] -ne 0x50 -or $iconBytes[2] -ne 0x4E -or $iconBytes[3] -ne 0x47) { throw 'Repository icon is not a PNG.' }
        $width = ([int]$iconBytes[16] -shl 24) -bor ([int]$iconBytes[17] -shl 16) -bor ([int]$iconBytes[18] -shl 8) -bor [int]$iconBytes[19]
        $height = ([int]$iconBytes[20] -shl 24) -bor ([int]$iconBytes[21] -shl 16) -bor ([int]$iconBytes[22] -shl 8) -bor [int]$iconBytes[23]
        if ($width -ne 512 -or $height -ne 512) { throw "Repository icon dimensions are ${width}x${height}, expected 512x512." }
        $repoHash = (Get-FileHash -LiteralPath $iconPath -Algorithm SHA256).Hash
        $packageIconPath = Join-Path $temp 'icon.png'
        $packageHash = (Get-FileHash -LiteralPath $packageIconPath -Algorithm SHA256).Hash
        if ($repoHash -ne $packageHash) { throw 'Package-root icon.png is not byte-identical to repository-root icon.png.' }
        $iconVerified = $true
        Write-Output "ICON_GATE=PASS path=icon.png bytes=$($iconBytes.Length) dimensions=${width}x${height} sha256=$repoHash"
    }
    $dependencyGroups = @($metadata.dependencies.group | ForEach-Object { $_.dependency | ForEach-Object { $_.id } })
    Write-Output "PACKAGE_ID=$($metadata.id)"
    Write-Output "PACKAGE_VERSION=$($metadata.version)"
    Write-Output 'TFM=net8.0'
    Write-Output "DEPENDENCIES=$($dependencyGroups -join ',')"
    Write-Output "ENTRY_COUNT=$(@($entries).Count)"
    if ($iconVerified) { Write-Output 'PACKAGE_INSPECTION=PASS' } else { Write-Output 'PACKAGE_INSPECTION=PASS_WITH_ICON_GATE_UNVERIFIED' }
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
