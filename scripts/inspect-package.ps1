param(
    [Parameter(Mandatory = $true)] [string] $PackagePath,
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)] [string] $SymbolPackagePath,
    [string] $ExpectedCommit,
    [switch] $AllowMissingIcon,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$packagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$ExpectedCommit = if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) { (& git -C $root rev-parse HEAD).Trim() } else { $ExpectedCommit.Trim().ToLowerInvariant() }
if ($LASTEXITCODE -ne 0 -and [string]::IsNullOrWhiteSpace($ExpectedCommit)) { throw 'Could not determine the expected candidate commit.' }
if ($ExpectedCommit -notmatch '^[0-9a-f]{40}$') { throw "Expected commit must be a 40-character hexadecimal SHA: $ExpectedCommit" }
$hasSymbolPackage = -not [string]::IsNullOrWhiteSpace($SymbolPackagePath)
if ($hasSymbolPackage) {
    $symbolPackagePath = (Resolve-Path -LiteralPath $SymbolPackagePath).Path
}
$policyPath = Join-Path $PSScriptRoot 'sensitive-path-policy.json'
$sensitivePolicy = Get-Content -Raw -LiteralPath (Resolve-Path -LiteralPath $policyPath) | ConvertFrom-Json
$sensitivePatterns = @($sensitivePolicy.families | ForEach-Object { $_.regex })
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ForbiddenPatterns {
    $patterns = [Collections.Generic.List[object]]::new()
    foreach ($value in @('probe', 'evidence', 'orchestration', 'Codex', 'Paperclip', 'frontier', 'agent', 'model', 'founder', 'acceptance', 'task')) {
        $patterns.Add([pscustomobject]@{ Name = $value; Pattern = '(?i)' + (New-WrappedTokenPattern -Value $value) })
    }
    $patterns.Add([pscustomobject]@{ Name = 'phase-0'; Pattern = '(?i)(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value 'phase') + '[\s\p{Z}\p{Pd}_]*0(?![\p{L}\p{N}_])' })
    $patterns.Add([pscustomobject]@{ Name = 'internal-error'; Pattern = '(?i)(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value 'INTERNAL') + '[\s\p{Z}\p{Pd}_]*' + (New-WrappedLiteral -Value 'ERROR') + '(?![\p{L}\p{N}_])' })
    $patterns.Add([pscustomobject]@{ Name = 'internal analysis error'; Pattern = '(?i)(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value 'INTERNAL') + '[\s\p{Z}]+analysis[\s\p{Z}]+' + (New-WrappedLiteral -Value 'error') + '(?![\p{L}\p{N}_])' })
    $patterns.Add([pscustomobject]@{ Name = 'issue identifier'; Pattern = '(?i)(?<![\p{L}\p{N}_])K' + (New-WrappedLiteral -Value 'E') + (New-WrappedLiteral -Value 'E') + '[\s\p{Z}\p{Pd}_]*-?[\s\p{Z}]*\d(?:[\s\p{Z}]*\d)*(?![\p{L}\p{N}_])' })
    return $patterns.ToArray()
}

function ConvertTo-ByteSlice {
    param(
        [byte[]] $Bytes,
        [int] $StartIndex,
        [int] $Length
    )
    $slice = [byte[]]::new($Length)
    if ($Length -gt 0) { [Buffer]::BlockCopy($Bytes, $StartIndex, $slice, 0, $Length) }
    return ,$slice
}

function Test-BytePrefix {
    param(
        [byte[]] $Bytes,
        [byte[]] $Prefix
    )
    if ($Bytes.Length -lt $Prefix.Length) { return $false }
    for ($index = 0; $index -lt $Prefix.Length; $index++) {
        if ($Bytes[$index] -ne $Prefix[$index]) { return $false }
    }
    return $true
}

function Test-AllowedTextCharacters {
    param([string] $Text)
    foreach ($character in $Text.ToCharArray()) {
        if ([char]::IsControl($character) -and $character -notin @([char] 0x09, [char] 0x0a, [char] 0x0d)) {
            return $false
        }
    }
    return $true
}

function Try-DecodeText {
    param(
        [byte[]] $Bytes,
        [Text.Encoding] $Encoding,
        [int] $Offset = 0
    )
    try {
        $payload = ConvertTo-ByteSlice -Bytes $Bytes -StartIndex $Offset -Length ($Bytes.Length - $Offset)
        $text = $Encoding.GetString($payload)
        if (-not (Test-AllowedTextCharacters -Text $text)) { return $null }
        return $text
    }
    catch {
        return $null
    }
}

function Try-DecodeUtf32NoBom {
    param(
        [byte[]] $Bytes,
        [Text.Encoding] $Encoding,
        [bool] $BigEndian
    )
    if ($Bytes.Length -eq 0 -or $Bytes.Length % 4 -ne 0) { return $null }
    $highByteOffset = if ($BigEndian) { 0 } else { 3 }
    for ($index = $highByteOffset; $index -lt $Bytes.Length; $index += 4) {
        if ($Bytes[$index] -ne 0) { return $null }
    }
    return Try-DecodeText -Bytes $Bytes -Encoding $Encoding
}

function Get-NullRatio {
    param(
        [byte[]] $Bytes,
        [int] $Modulo,
        [int] $Position
    )
    $total = 0
    $zeros = 0
    for ($index = $Position; $index -lt $Bytes.Length; $index += $Modulo) {
        $total++
        if ($Bytes[$index] -eq 0) { $zeros++ }
    }
    if ($total -eq 0) { return 0.0 }
    return [double] $zeros / $total
}

function Decode-PackageText {
    param(
        [byte[]] $Bytes,
        [string] $EntryName
    )

    if ($Bytes.Length -eq 0) { return '' }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $utf16le = [Text.UnicodeEncoding]::new($false, $false, $true)
    $utf16be = [Text.UnicodeEncoding]::new($true, $false, $true)
    $utf32le = [Text.UTF32Encoding]::new($false, $false, $true)
    $utf32be = [Text.UTF32Encoding]::new($true, $false, $true)

    try {
        if (Test-BytePrefix -Bytes $Bytes -Prefix ([byte[]](0xff, 0xfe, 0x00, 0x00))) { $text = Try-DecodeText -Bytes $Bytes -Encoding $utf32le -Offset 4 }
        elseif (Test-BytePrefix -Bytes $Bytes -Prefix ([byte[]](0x00, 0x00, 0xfe, 0xff))) { $text = Try-DecodeText -Bytes $Bytes -Encoding $utf32be -Offset 4 }
        elseif (Test-BytePrefix -Bytes $Bytes -Prefix ([byte[]](0xef, 0xbb, 0xbf))) { $text = Try-DecodeText -Bytes $Bytes -Encoding $utf8 -Offset 3 }
        elseif (Test-BytePrefix -Bytes $Bytes -Prefix ([byte[]](0xff, 0xfe))) { $text = Try-DecodeText -Bytes $Bytes -Encoding $utf16le -Offset 2 }
        elseif (Test-BytePrefix -Bytes $Bytes -Prefix ([byte[]](0xfe, 0xff))) { $text = Try-DecodeText -Bytes $Bytes -Encoding $utf16be -Offset 2 }
        else {
            $text = $null
            if ($null -eq $text) {
                $text = Try-DecodeUtf32NoBom -Bytes $Bytes -Encoding $utf32le -BigEndian:$false
            }
            if ($null -eq $text) {
                $text = Try-DecodeUtf32NoBom -Bytes $Bytes -Encoding $utf32be -BigEndian:$true
            }
            if ($null -eq $text -and $Bytes.Length % 2 -eq 0 -and (Get-NullRatio -Bytes $Bytes -Modulo 2 -Position 1) -ge 0.50) {
                $text = Try-DecodeText -Bytes $Bytes -Encoding $utf16le
            }
            if ($null -eq $text -and $Bytes.Length % 2 -eq 0 -and (Get-NullRatio -Bytes $Bytes -Modulo 2 -Position 0) -ge 0.50) {
                $text = Try-DecodeText -Bytes $Bytes -Encoding $utf16be
            }
            if ($null -eq $text) { $text = Try-DecodeText -Bytes $Bytes -Encoding $utf8 }
        }
        if ($null -eq $text) { throw 'unsupported or invalid text encoding' }
        return $text
    }
    catch {
        throw "Could not decode package text entry '$EntryName': $($_.Exception.Message)"
    }
}

function ConvertTo-ScanText {
    param([string] $Text)

    $normalized = $Text.Normalize([Text.NormalizationForm]::FormKC)
    $normalized = [regex]::Replace($normalized, '[\u00ad\u034f\u061c\u180e\u200b-\u200f\u202a-\u202e\u2060\u2066-\u2069\ufeff]', '')
    $map = @{
        ([char] 0x0430) = 'a'; ([char] 0x0410) = 'A'; ([char] 0x0435) = 'e'; ([char] 0x0415) = 'E'
        ([char] 0x043e) = 'o'; ([char] 0x041e) = 'O'; ([char] 0x0440) = 'p'; ([char] 0x0420) = 'P'
        ([char] 0x0441) = 'c'; ([char] 0x0421) = 'C'; ([char] 0x0445) = 'x'; ([char] 0x0425) = 'X'
        ([char] 0x0456) = 'i'; ([char] 0x0406) = 'I'; ([char] 0x0458) = 'j'; ([char] 0x0408) = 'J'
        ([char] 0x03b1) = 'a'; ([char] 0x0391) = 'A'; ([char] 0x03bf) = 'o'; ([char] 0x039f) = 'O'
        ([char] 0x03c1) = 'p'; ([char] 0x03a1) = 'P'; ([char] 0x03b5) = 'e'; ([char] 0x0395) = 'E'
    }
    $builder = [Text.StringBuilder]::new($normalized.Length)
    foreach ($character in $normalized.ToCharArray()) {
        if ($map.ContainsKey($character)) { [void] $builder.Append($map[$character]) }
        else { [void] $builder.Append($character) }
    }
    return $builder.ToString()
}

function New-WrappedLiteral {
    param([string] $Value)
    $lineWrap = '(?:[^\S\r\n\u2028\u2029]*(?:\r\n|[\r\n\u2028\u2029])[^\S\r\n\u2028\u2029]*)?'
    return (($Value.ToCharArray() | ForEach-Object { [regex]::Escape([string] $_) }) -join $lineWrap)
}

function New-WrappedTokenPattern {
    param([string] $Value)
    return '(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value $Value) + '(?![\p{L}\p{N}_])'
}

function Get-ArchiveEntryBytes {
    param([IO.Compression.ZipArchiveEntry] $Entry)
    $memory = [IO.MemoryStream]::new()
    try {
        $stream = $Entry.Open()
        try { $stream.CopyTo($memory) }
        finally { $stream.Dispose() }
        return ,$memory.ToArray()
    }
    finally { $memory.Dispose() }
}

function Get-ArchiveEntryText {
    param([IO.Compression.ZipArchive] $Archive, [string] $EntryName)
    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) { throw "Archive entry is missing: $EntryName" }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $stream = $entry.Open()
    try {
        $reader = [IO.StreamReader]::new($stream, $utf8, $false)
        try { return $reader.ReadToEnd().TrimStart([char]0xfeff) }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-NuspecDocument {
    param([IO.Compression.ZipArchive] $Archive, [string] $ArchiveLabel)
    $nuspecs = @($Archive.Entries | Where-Object { $_.FullName -match '(?i)^[^/]+\.nuspec$' })
    if ($nuspecs.Count -ne 1) { throw "$ArchiveLabel must contain exactly one root nuspec." }
    try { [xml] $document = Get-ArchiveEntryText -Archive $Archive -EntryName $nuspecs[0].FullName }
    catch { throw "$ArchiveLabel nuspec is not valid UTF-8 XML: $($_.Exception.Message)" }
    return [pscustomobject]@{ Name = $nuspecs[0].FullName; Document = $document }
}

function Assert-CommitFields {
    param([xml] $Document, [string] $Expected, [string] $ArchiveLabel)
    $commitNodes = @($Document.SelectNodes('//*[local-name()="commit"]')) + @($Document.SelectNodes('//@commit'))
    if ($commitNodes.Count -eq 0) { throw "$ArchiveLabel has no repository commit field." }
    foreach ($node in $commitNodes) {
        $value = if ($node -is [Xml.XmlAttribute]) { $node.Value } else { $node.InnerText }
        if ($value -ne $Expected) { throw "$ArchiveLabel repository commit does not match the frozen candidate $Expected." }
    }
}

function Get-PdbProvenance {
    param([byte[]] $Bytes, [string] $EntryName)
    Add-Type -AssemblyName System.Reflection.Metadata
    Add-Type -AssemblyName System.Collections.Immutable
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    try {
        $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($stream)
        try {
            $reader = $provider.GetMetadataReader()
            $documents = @($reader.Documents | ForEach-Object { $reader.GetString($reader.GetDocument($_).Name) })
            $sourceLink = $null
            $sourceLinkGuid = [Guid]'CC110556-A091-4D38-9FEC-25AB9A351A6A'
            foreach ($handle in $reader.CustomDebugInformation) {
                $custom = $reader.GetCustomDebugInformation($handle)
                if ($reader.GetGuid($custom.Kind) -eq $sourceLinkGuid) {
                    $sourceLink = [Text.UTF8Encoding]::new($false, $true).GetString($reader.GetBlobBytes($custom.Value))
                }
            }
            if ([string]::IsNullOrWhiteSpace($sourceLink)) { throw "$EntryName has no SourceLink custom debug information." }
            return [pscustomobject]@{ Documents = $documents; SourceLink = $sourceLink }
        }
        finally { $provider.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Assert-PdbProvenance {
    param([byte[]] $Bytes, [string] $EntryName, [string] $Expected)
    try { $provenance = Get-PdbProvenance -Bytes $Bytes -EntryName $EntryName }
    catch { throw "Could not parse portable PDB ${EntryName}: $($_.Exception.Message)" }
    foreach ($document in $provenance.Documents) {
        if ($document -match '\\|^[A-Za-z]:|^//|^/Users/|^/home/|^/private/|^/Volumes/|\.\.([/\\]|$)' -or ($document.StartsWith('/') -and -not $document.StartsWith('/_/'))) {
            throw "Portable PDB contains a private or absolute document path: $EntryName -> $document"
        }
        if ([string]::IsNullOrWhiteSpace($document)) { throw "Portable PDB contains an empty document path: $EntryName" }
    }
    try { $sourceLink = $provenance.SourceLink | ConvertFrom-Json }
    catch { throw "SourceLink data in $EntryName is not valid JSON." }
    $documents = @($sourceLink.documents.PSObject.Properties)
    if ($documents.Count -ne 1 -or $documents[0].Name -ne '/_/*' -or $documents[0].Value -ne "https://raw.githubusercontent.com/KeelMatrix/CliContract/$Expected/*") {
        throw "SourceLink data in $EntryName does not point to the frozen candidate $Expected."
    }
    return $provenance
}

function Assert-ArchiveProvenance {
    param([string] $ArchivePath, [string] $ArchiveLabel, [string] $Expected)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $nuspec = Get-NuspecDocument -Archive $archive -ArchiveLabel $ArchiveLabel
        Assert-CommitFields -Document $nuspec.Document -Expected $Expected -ArchiveLabel $ArchiveLabel
        $pdbs = @($archive.Entries | Where-Object { $_.FullName -match '(?i)\.pdb$' })
        if ($pdbs.Count -eq 0) { throw "$ArchiveLabel contains no portable PDB entries." }
        $provenance = @{}
        foreach ($entry in $pdbs) {
            $provenance[$entry.FullName] = Assert-PdbProvenance -Bytes (Get-ArchiveEntryBytes -Entry $entry) -EntryName "$ArchiveLabel/$($entry.FullName)" -Expected $Expected
        }
        return [pscustomobject]@{ Nuspec = $nuspec; Pdbs = $pdbs; Provenance = $provenance }
    }
    finally { $archive.Dispose() }
}

function Assert-ArchiveTextSurface {
    param(
        [string] $ArchivePath,
        [string] $ArchiveName
    )

    $patterns = @(Get-ForbiddenPatterns)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.FullName) -or $entry.FullName.EndsWith('/') -or $entry.FullName -match '(?i)\.(?:dll|pdb|png)$') { continue }
            $text = Decode-PackageText -Bytes (Get-ArchiveEntryBytes -Entry $entry) -EntryName ("{0}:{1}" -f $ArchiveName, $entry.FullName)
            $text = ConvertTo-ScanText -Text $text
            foreach ($pattern in $patterns) {
                if ($text -match $pattern.Pattern) { throw "Forbidden user-facing wording found in package entry: $ArchiveName/$($entry.FullName)" }
            }
        }
    }
    finally { $archive.Dispose() }
}

function Replace-ArchiveEntryBytes {
    param(
        [string] $ArchivePath,
        [string] $EntryName,
        [byte[]] $Bytes
    )

    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) { throw "Package self-test entry is missing: $EntryName" }
        $entry.Delete()
        $replacement = $archive.CreateEntry($EntryName)
        $stream = $replacement.Open()
        try { $stream.Write($Bytes, 0, $Bytes.Length) }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Replace-ByteSequence {
    param([byte[]] $Bytes, [byte[]] $Old, [byte[]] $New)
    if ($Old.Length -ne $New.Length) { throw 'Self-test byte replacements must preserve length.' }
    for ($start = 0; $start -le $Bytes.Length - $Old.Length; $start++) {
        $match = $true
        for ($offset = 0; $offset -lt $Old.Length; $offset++) { if ($Bytes[$start + $offset] -ne $Old[$offset]) { $match = $false; break } }
        if ($match) {
            $copy = [byte[]]::new($Bytes.Length)
            [Buffer]::BlockCopy($Bytes, 0, $copy, 0, $Bytes.Length)
            [Buffer]::BlockCopy($New, 0, $copy, $start, $New.Length)
            return ,$copy
        }
    }
    throw 'Self-test byte sequence was not found.'
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

function Add-NuspecDependency {
    param(
        [string] $ArchivePath,
        [string] $NuspecEntry = 'KeelMatrix.CliContract.nuspec'
    )

    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry($NuspecEntry)
        if ($null -eq $entry) { throw 'Package nuspec is required for the dependency self-test.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $content = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $entry.Delete()
        $replacement = $archive.CreateEntry($NuspecEntry)
        $writer = [IO.StreamWriter]::new($replacement.Open(), [Text.UTF8Encoding]::new($false))
        try {
            $writer.Write($content.Replace('</metadata>', '<dependencies><group targetFramework="net8.0"><dependency id="Unexpected.Dependency" version="[1.0.0]" /></group></dependencies></metadata>'))
        }
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
    if (-not $hasSymbolPackage) { throw 'Package inspection self-test requires the symbol package path.' }
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-package-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $selfTestRoot | Out-Null
        $mutatedPackage = Join-Path $selfTestRoot 'mutated.nupkg'
        $mutatedSymbols = Join-Path $selfTestRoot 'mutated.snupkg'
        Copy-Item -LiteralPath $packagePath -Destination $mutatedPackage
        Copy-Item -LiteralPath $symbolPackagePath -Destination $mutatedSymbols
        Add-ArchiveMarker -ArchivePath $mutatedPackage -Marker ('pr' + 'obe')
        $childOutput = @(& pwsh -NoProfile -File $PSCommandPath -PackagePath $mutatedPackage -SymbolPackagePath $mutatedSymbols -RepositoryRoot $root 2>&1)
        $childExit = $LASTEXITCODE
        if ($childExit -eq 0) {
            throw 'Package wording gate accepted an injected forbidden term.'
        }
        Write-Output "PACKAGE_NON_VACUITY_CHILD_EXIT=$childExit"
        Write-Output 'PACKAGE_NON_VACUITY=PASS'

        foreach ($case in @(
            @{ Name = 'unexpected-assembly'; Entry = 'tools/net8.0/any/Unexpected.dll' },
            @{ Name = 'sensitive-local-json'; Entry = 'tools/net8.0/any/telemetry.local.json' },
            @{ Name = 'extra-core-properties'; Entry = 'package/services/metadata/core-properties/unexpected.psmdcp' },
            @{ Name = 'unexpected-dependency'; Entry = $null }
        )) {
            $casePackage = Join-Path $selfTestRoot ($case.Name + '.nupkg')
            Copy-Item -LiteralPath $packagePath -Destination $casePackage
            if ($null -eq $case.Entry) {
                Add-NuspecDependency -ArchivePath $casePackage
            }
            else {
                Add-ArchiveEntry -ArchivePath $casePackage -EntryName $case.Entry
            }
            $caseOutput = @(& pwsh -NoProfile -File $PSCommandPath -PackagePath $casePackage -SymbolPackagePath $mutatedSymbols -RepositoryRoot $root 2>&1)
            $caseExit = $LASTEXITCODE
            if ($caseExit -eq 0) { throw "Package inspection accepted self-test case $($case.Name)." }
            Write-Output "PACKAGE_NEGATIVE_SELF_TEST=$($case.Name) child_exit=$caseExit"
        }
        Write-Output 'PACKAGE_NEGATIVE_SELF_TEST=PASS'

        $provenanceCases = @(
            @{ Name = 'wrong-valid-commit'; Archive = 'nupkg'; Entry = 'KeelMatrix.CliContract.nuspec'; Old = [Text.Encoding]::UTF8.GetBytes($ExpectedCommit); New = [Text.Encoding]::UTF8.GetBytes(('0' * 40)) },
            @{ Name = 'incorrect-sourcelink-url'; Archive = 'nupkg'; Entry = 'tools/net8.0/any/KeelMatrix.CliContract.pdb'; Old = [Text.Encoding]::UTF8.GetBytes('raw.githubusercontent.com'); New = [Text.Encoding]::UTF8.GetBytes('bad.githubusercontent.com') },
            @{ Name = 'absolute-pdb-path'; Archive = 'nupkg'; Entry = 'tools/net8.0/any/KeelMatrix.CliContract.pdb'; Old = [Text.Encoding]::UTF8.GetBytes('src'); New = [Text.Encoding]::UTF8.GetBytes('C:\') },
            @{ Name = 'stale-symbol-pdb'; Archive = 'snupkg'; Entry = 'tools/net8.0/any/KeelMatrix.CliContract.pdb'; Old = $null; New = $null }
        )
        foreach ($case in $provenanceCases) {
            $casePackage = Join-Path $selfTestRoot ($case.Name + '.nupkg')
            $caseSymbols = Join-Path $selfTestRoot ($case.Name + '.snupkg')
            Copy-Item -LiteralPath $packagePath -Destination $casePackage
            Copy-Item -LiteralPath $symbolPackagePath -Destination $caseSymbols
            if ($case.Name -eq 'stale-symbol-pdb') {
                $archive = [IO.Compression.ZipFile]::OpenRead($caseSymbols)
                try { $bytes = Get-ArchiveEntryBytes -Entry $archive.GetEntry($case.Entry) }
                finally { $archive.Dispose() }
                $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
                Replace-ArchiveEntryBytes -ArchivePath $caseSymbols -EntryName $case.Entry -Bytes $bytes
            }
            else {
                $target = if ($case.Archive -eq 'nupkg') { $casePackage } else { $caseSymbols }
                $archive = [IO.Compression.ZipFile]::OpenRead($target)
                try { $bytes = Get-ArchiveEntryBytes -Entry $archive.GetEntry($case.Entry) }
                finally { $archive.Dispose() }
                Replace-ArchiveEntryBytes -ArchivePath $target -EntryName $case.Entry -Bytes (Replace-ByteSequence -Bytes $bytes -Old $case.Old -New $case.New)
            }
            $caseOutput = @(& pwsh -NoProfile -File $PSCommandPath -PackagePath $casePackage -SymbolPackagePath $caseSymbols -RepositoryRoot $root -ExpectedCommit $ExpectedCommit 2>&1)
            $caseExit = $LASTEXITCODE
            if ($caseExit -eq 0) { throw "Package provenance inspection accepted self-test case $($case.Name)." }
            Write-Output "PACKAGE_PROVENANCE_NEGATIVE_SELF_TEST=$($case.Name) child_exit=$caseExit"
        }
        Write-Output 'PACKAGE_PROVENANCE_NEGATIVE_SELF_TEST=PASS'

        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $utf8Bom = [Text.UTF8Encoding]::new($true, $true)
        $utf16le = [Text.UnicodeEncoding]::new($false, $false, $true)
        $utf16be = [Text.UnicodeEncoding]::new($true, $false, $true)
        $utf32le = [Text.UTF32Encoding]::new($false, $false, $true)
        $utf32be = [Text.UTF32Encoding]::new($true, $false, $true)
        $cleanText = 'safe text α世界😀'
        $cleanDecoderCases = @(
            @{ Name = 'utf8-no-bom'; Bytes = $utf8.GetBytes($cleanText) },
            @{ Name = 'utf8-bom'; Bytes = [byte[]]($utf8Bom.GetPreamble() + $utf8Bom.GetBytes($cleanText)) },
            @{ Name = 'utf16le-no-bom'; Bytes = $utf16le.GetBytes($cleanText) },
            @{ Name = 'utf16le-bom'; Bytes = [byte[]](([Text.Encoding]::Unicode.GetPreamble()) + $utf16le.GetBytes($cleanText)) },
            @{ Name = 'utf16be-no-bom'; Bytes = $utf16be.GetBytes($cleanText) },
            @{ Name = 'utf16be-bom'; Bytes = [byte[]](([Text.Encoding]::BigEndianUnicode.GetPreamble()) + $utf16be.GetBytes($cleanText)) },
            @{ Name = 'utf32le-no-bom'; Bytes = $utf32le.GetBytes($cleanText) },
            @{ Name = 'utf32le-bom'; Bytes = [byte[]](([Text.UTF32Encoding]::new($false, $true, $true).GetPreamble()) + $utf32le.GetBytes($cleanText)) },
            @{ Name = 'utf32be-no-bom'; Bytes = $utf32be.GetBytes($cleanText) },
            @{ Name = 'utf32be-bom'; Bytes = [byte[]](([Text.UTF32Encoding]::new($true, $true, $true).GetPreamble()) + $utf32be.GetBytes($cleanText)) }
        )
        foreach ($archiveCase in @('nupkg', 'snupkg')) {
            foreach ($case in $cleanDecoderCases) {
                $caseRoot = Join-Path $selfTestRoot ("clean-decoder-$archiveCase-$($case.Name)")
                New-Item -ItemType Directory -Path $caseRoot | Out-Null
                $casePackage = Join-Path $caseRoot 'KeelMatrix.CliContract.0.1.0.nupkg'
                $caseSymbols = Join-Path $caseRoot 'KeelMatrix.CliContract.0.1.0.snupkg'
                Copy-Item -LiteralPath $packagePath -Destination $casePackage
                Copy-Item -LiteralPath $symbolPackagePath -Destination $caseSymbols
                if ($archiveCase -eq 'nupkg') {
                    Replace-ArchiveEntryBytes -ArchivePath $casePackage -EntryName 'README.md' -Bytes $case.Bytes
                }
                else {
                    Replace-ArchiveEntryBytes -ArchivePath $caseSymbols -EntryName '[Content_Types].xml' -Bytes $case.Bytes
                }
                $caseOutput = @(& pwsh -NoProfile -File $PSCommandPath -PackagePath $casePackage -SymbolPackagePath $caseSymbols -RepositoryRoot $root 2>&1)
                $caseExit = $LASTEXITCODE
                if ($caseExit -ne 0 -or ($caseOutput -join "`n") -notmatch 'PACKAGE_INSPECTION=PASS') { throw "Package strict-decoder self-test rejected clean $archiveCase fixture $($case.Name): $($caseOutput -join ' | ')" }
                Write-Output "PACKAGE_TEXT_FIXTURE=$archiveCase-$($case.Name) EXPECTED=ACCEPT exit=$caseExit"
            }
        }
        $decoderCases = @(
            @{ Name = 'utf8-no-bom'; Bytes = $utf8.GetBytes('Paperclip') },
            @{ Name = 'utf8-bom'; Bytes = [byte[]]($utf8Bom.GetPreamble() + $utf8Bom.GetBytes('Paperclip')) },
            @{ Name = 'utf16le-no-bom'; Bytes = $utf16le.GetBytes('Paperclip') },
            @{ Name = 'utf16le-bom'; Bytes = [byte[]](([Text.Encoding]::Unicode.GetPreamble()) + $utf16le.GetBytes('Paperclip')) },
            @{ Name = 'utf16be-no-bom'; Bytes = $utf16be.GetBytes('Paperclip') },
            @{ Name = 'utf16be-bom'; Bytes = [byte[]](([Text.Encoding]::BigEndianUnicode.GetPreamble()) + $utf16be.GetBytes('Paperclip')) },
            @{ Name = 'utf32le-no-bom'; Bytes = $utf32le.GetBytes('Paperclip') },
            @{ Name = 'utf32le-bom'; Bytes = [byte[]](([Text.UTF32Encoding]::new($false, $true, $true).GetPreamble()) + $utf32le.GetBytes('Paperclip')) },
            @{ Name = 'utf32be-no-bom'; Bytes = $utf32be.GetBytes('Paperclip') },
            @{ Name = 'utf32be-bom'; Bytes = [byte[]](([Text.UTF32Encoding]::new($true, $true, $true).GetPreamble()) + $utf32be.GetBytes('Paperclip')) },
            @{ Name = 'invalid-utf8'; Bytes = [byte[]](0x50, 0x61, 0x70, 0x65, 0x72, 0xc3, 0x28, 0x63, 0x6c, 0x69, 0x70) }
        )
        foreach ($archiveCase in @('nupkg', 'snupkg')) {
            foreach ($case in $decoderCases) {
                $caseRoot = Join-Path $selfTestRoot ("decoder-$archiveCase-$($case.Name)")
                New-Item -ItemType Directory -Path $caseRoot | Out-Null
                $casePackage = Join-Path $caseRoot 'KeelMatrix.CliContract.0.1.0.nupkg'
                $caseSymbols = Join-Path $caseRoot 'KeelMatrix.CliContract.0.1.0.snupkg'
                Copy-Item -LiteralPath $packagePath -Destination $casePackage
                Copy-Item -LiteralPath $symbolPackagePath -Destination $caseSymbols
                if ($archiveCase -eq 'nupkg') {
                    Replace-ArchiveEntryBytes -ArchivePath $casePackage -EntryName 'README.md' -Bytes $case.Bytes
                }
                else {
                    Replace-ArchiveEntryBytes -ArchivePath $caseSymbols -EntryName '[Content_Types].xml' -Bytes $case.Bytes
                }
                $caseOutput = @(& pwsh -NoProfile -File $PSCommandPath -PackagePath $casePackage -SymbolPackagePath $caseSymbols -RepositoryRoot $root 2>&1)
                $caseExit = $LASTEXITCODE
                if ($caseExit -eq 0) { throw "Package strict-decoder self-test accepted $archiveCase fixture $($case.Name)." }
                Write-Output "PACKAGE_TEXT_FIXTURE=$archiveCase-$($case.Name) EXPECTED=REJECT exit=$caseExit"
            }
        }
        Write-Output 'PACKAGE_TEXT_DECODER_REGRESSION=PASS'
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
    Assert-CommitFields -Document $nuspec -Expected $ExpectedCommit -ArchiveLabel 'nupkg'
    $metadata = $nuspec.package.metadata
    $iconVerified = $false
    $allowedMetadata = @('id', 'version', 'authors', 'license', 'licenseUrl', 'icon', 'readme', 'projectUrl', 'description', 'tags', 'packageTypes', 'repository')
    $unexpectedMetadata = @($metadata.ChildNodes | Where-Object { $_.NodeType -eq 'Element' -and $_.LocalName -notin $allowedMetadata })
    if ($unexpectedMetadata.Count -gt 0) { throw "Nuspec contains unexpected metadata fields: $($unexpectedMetadata.LocalName -join ', ')" }
    if ($metadata.id -ne 'KeelMatrix.CliContract') { throw "Unexpected package id: $($metadata.id)" }
    if ($metadata.version -ne '0.1.0') { throw "Unexpected package version: $($metadata.version)" }
    if ($metadata.authors -ne 'KeelMatrix') { throw 'Package authors metadata is incorrect.' }
    if ($metadata.description -ne 'Detects breaking command-line interface changes from deterministic OpenCLI contracts.') { throw 'Package description metadata is incorrect.' }
    if ($metadata.tags -ne 'dotnet-tool cli command-line compatibility contract opencli schema breaking-change ci semver') { throw 'Package tags metadata is incorrect.' }
    if ($metadata.license.GetAttribute('type') -ne 'expression' -or $metadata.license.InnerText -ne 'MIT') { throw 'Package license metadata is not MIT.' }
    if ($metadata.readme -ne 'README.md') { throw 'Package README metadata is not README.md.' }
    $iconPath = Join-Path $root 'icon.png'
    if ($metadata.icon -ne 'icon.png') {
        if (-not $AllowMissingIcon -or (Test-Path -LiteralPath $iconPath)) { throw 'Package icon metadata is not icon.png.' }
        Write-Output 'ICON_GATE=UNVERIFIED package icon metadata and package-root icon are absent.'
    }
    if ($metadata.projectUrl -ne 'https://github.com/KeelMatrix/CliContract') { throw 'Project URL metadata is incorrect.' }
    if ($metadata.repository.type -ne 'git' -or $metadata.repository.url -ne 'https://github.com/KeelMatrix/CliContract' -or $metadata.repository.commit -ne $ExpectedCommit) { throw "Repository metadata must point to frozen candidate $ExpectedCommit." }
    $packageTypes = @($metadata.packageTypes.packageType | ForEach-Object { $_.name })
    if ($packageTypes.Count -ne 1 -or $packageTypes[0] -ne 'DotnetTool') { throw 'Package type metadata is not the expected DotnetTool contract.' }
    $dependencyNodes = @($metadata.dependencies.group | ForEach-Object { $_.dependency } | Where-Object { $null -ne $_ })
    if ($dependencyNodes.Count -gt 0) { throw "Unexpected nuspec dependencies: $(@($dependencyNodes | ForEach-Object { $_.id }) -join ', ')" }
    $settingsPath = Join-Path $temp 'tools/net8.0/any/DotnetToolSettings.xml'
    [xml]$toolSettings = Get-Content -Raw -LiteralPath $settingsPath
    $toolCommand = $toolSettings.DotNetCliTool.Commands.Command
    if ($toolSettings.DotNetCliTool.Version -ne '1' -or $toolCommand.Name -ne 'clicontract' -or $toolCommand.EntryPoint -ne 'KeelMatrix.CliContract.dll' -or $toolCommand.Runner -ne 'dotnet') { throw 'DotnetToolSettings.xml does not describe the expected net8.0 tool identity.' }
    $unexpectedTfms = @($entries | Where-Object { $_ -match '^tools/([^/]+)/' -and $_ -notmatch '^tools/net8\.0/any/' })
    if ($unexpectedTfms.Count -gt 0) { throw "Unexpected tool target framework entries: $($unexpectedTfms -join ', ')" }
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
    $required = @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES', 'KeelMatrix.CliContract.nuspec') + $requiredToolPayload
    foreach ($entry in $required) { if ($entries -notcontains $entry) { throw "Required package entry is missing: $entry" } }
    if ($entries -notcontains 'LICENSE') { throw 'The package must contain LICENSE.' }
    $allowedEntries = @('_rels/.rels', '[Content_Types].xml', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES', 'icon.png', $nuspecName) + $requiredToolPayload + $metadataEntries
    $unexpected = @($entries | Where-Object {
        $_ -notin $allowedEntries
    })
    if ($unexpected.Count -gt 0) { throw "Unexpected package entries: $($unexpected -join ', ')" }
    $noticePath = Join-Path $temp 'THIRD-PARTY-NOTICES'
    $notice = Get-Content -Raw -LiteralPath $noticePath
    $yamlNotice = @'
Copyright (c) 2008, 2009, 2010, 2011, 2012, 2013, 2014 Antoine Aubry and contributors
Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to
do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@
    if ($notice -notmatch 'YamlDotNet\s+v16\.3\.0' -or $notice -notmatch [regex]::Escape($yamlNotice.Trim())) { throw 'THIRD-PARTY-NOTICES does not contain the exact YamlDotNet v16.3.0 notice.' }
    $runtimeAssemblies = @($entries | Where-Object { $_ -match '^tools/net8\.0/any/[^/]+\.dll$' } | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
    foreach ($assembly in $runtimeAssemblies) {
        if ($assembly -notlike 'KeelMatrix.*' -and $notice -notmatch [regex]::Escape($assembly)) { throw "Bundled third-party assembly $assembly has no matching redistribution notice." }
        Write-Output "BUNDLED_ASSEMBLY=$assembly NOTICE=PASS"
    }
    $sensitive = @($entries | Where-Object {
        $entry = $_
        @($sensitivePatterns | Where-Object { $entry -match $_ }).Count -gt 0 -or $entry -match '(?i)(^|/)AGENTS\.md$|.*test.*'
    })
    if ($sensitive.Count -gt 0) { throw "Sensitive or test package entries found: $($sensitive -join ', ')" }
    $pdbEntries = @($entries | Where-Object { $_ -match '\.pdb$' })
    $expectedPdbEntries = @($requiredToolPayload | Where-Object { $_ -match '\.pdb$' })
    if ((@($pdbEntries | Sort-Object) -join '|') -ne (@($expectedPdbEntries | Sort-Object) -join '|')) { throw "Unexpected or missing package symbol entries: $($pdbEntries -join ', ')" }
    Assert-ArchiveTextSurface -ArchivePath $packagePath -ArchiveName 'nupkg'
    if ($hasSymbolPackage) { Assert-ArchiveTextSurface -ArchivePath $symbolPackagePath -ArchiveName 'snupkg' }
    $nupkgProvenance = Assert-ArchiveProvenance -ArchivePath $packagePath -ArchiveLabel 'nupkg' -Expected $ExpectedCommit
    if ($hasSymbolPackage) {
        $snupkgProvenance = Assert-ArchiveProvenance -ArchivePath $symbolPackagePath -ArchiveLabel 'snupkg' -Expected $ExpectedCommit
        $nupkgMetadata = $nupkgProvenance.Nuspec.Document.package.metadata
        $snupkgMetadata = $snupkgProvenance.Nuspec.Document.package.metadata
        if ($nupkgMetadata.id -ne $snupkgMetadata.id -or $nupkgMetadata.version -ne $snupkgMetadata.version -or $nupkgMetadata.repository.commit -ne $snupkgMetadata.repository.commit) {
            throw 'nupkg and snupkg nuspec identity/version/commit do not match.'
        }
        $nupkgPdbNames = @($nupkgProvenance.Pdbs | ForEach-Object FullName | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
        $snupkgPdbNames = @($snupkgProvenance.Pdbs | ForEach-Object FullName | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
        if (($nupkgPdbNames -join '|') -ne ($snupkgPdbNames -join '|')) { throw 'nupkg and snupkg PDB assembly sets do not match.' }
        $nupkgArchive = [IO.Compression.ZipFile]::OpenRead($packagePath)
        $snupkgArchive = [IO.Compression.ZipFile]::OpenRead($symbolPackagePath)
        try {
            foreach ($name in @($nupkgProvenance.Pdbs | ForEach-Object FullName)) {
                $symbolName = $snupkgProvenance.Pdbs | Where-Object { [IO.Path]::GetFileName($_.FullName) -eq [IO.Path]::GetFileName($name) } | Select-Object -First 1 -ExpandProperty FullName
                $left = Get-ArchiveEntryBytes -Entry $nupkgArchive.GetEntry($name)
                $right = if ($null -eq $symbolName) { [byte[]]::new(0) } else { Get-ArchiveEntryBytes -Entry $snupkgArchive.GetEntry($symbolName) }
                if ($null -eq $symbolName -or $left.Length -ne $right.Length) { throw "Symbol PDB does not correspond to $name." }
                for ($index = 0; $index -lt $left.Length; $index++) { if ($left[$index] -ne $right[$index]) { throw "Symbol PDB does not correspond to $name." } }
            }
        }
        finally { $nupkgArchive.Dispose(); $snupkgArchive.Dispose() }
        Write-Output 'PACKAGE_PROVENANCE=PASS source_link=pdb_paths=pair=PASS'
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
