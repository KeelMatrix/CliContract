param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,
    [string] $SymbolPackagePath,
    [Parameter(Mandatory = $true)]
    [string] $PackageId,
    [Parameter(Mandatory = $true)]
    [string] $PackageVersion
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Bytes {
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

function Get-Sha256Hex {
    param([byte[]] $Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return (($algorithm.ComputeHash($Bytes) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $algorithm.Dispose() }
}

function Normalize-Archive {
    param(
        [string] $Path,
        [ValidateSet('nupkg', 'snupkg')]
        [string] $Kind
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Package archive is missing: $Path" }

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.FullName)) { throw "Package archive contains an empty entry name: $Path" }
            if ($entries.ContainsKey($entry.FullName)) { throw "Package archive contains duplicate entry: $($entry.FullName)" }
            $entries[$entry.FullName] = Get-Bytes -Entry $entry
        }
    }
    finally { $archive.Dispose() }

    $coreProperties = @($entries.Keys | Where-Object { $_ -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$' })
    if ($coreProperties.Count -ne 1) { throw "Package archive must contain exactly one core-properties entry: $Path" }
    $oldCoreProperties = $coreProperties[0]
    $coreName = Get-Sha256Hex -Bytes ([Text.UTF8Encoding]::new($false).GetBytes("$PackageId|$PackageVersion|$Kind|core-properties"))
    $newCoreProperties = "package/services/metadata/core-properties/$($coreName.Substring(0, 32)).psmdcp"

    $relsName = '_rels/.rels'
    if (-not $entries.ContainsKey($relsName)) { throw "Package archive is missing root relationships: $Path" }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $rels = $utf8.GetString($entries[$relsName])
    $coreRelationship = [regex]::Match($rels, '<Relationship\b(?=[^>]*Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties")(?=[^>]*Target="[^"]+")(?=[^>]*Id="[^"]+")[^>]*/>')
    if (-not $coreRelationship.Success) { throw "Package archive is missing the core-properties relationship: $Path" }
    $relationshipText = $coreRelationship.Value
    $oldTarget = [regex]::Match($relationshipText, 'Target="([^"]+)"')
    $oldId = [regex]::Match($relationshipText, 'Id="([^"]+)"')
    if (-not $oldTarget.Success -or -not $oldId.Success) { throw "Package archive core-properties relationship is malformed: $Path" }
    $relationshipKey = Get-Sha256Hex -Bytes ([Text.UTF8Encoding]::new($false).GetBytes("$PackageId|$PackageVersion|$Kind|relationship"))
    $newId = 'R' + $relationshipKey.Substring(0, 16).ToUpperInvariant()
    $rels = $rels.Replace($oldTarget.Groups[1].Value, '/' + $newCoreProperties)
    $rels = $rels.Replace($oldId.Groups[1].Value, $newId)
    $entries[$relsName] = [Text.UTF8Encoding]::new($false).GetBytes($rels)

    $coreBytes = $entries[$oldCoreProperties]
    $entries.Remove($oldCoreProperties)
    $entries[$newCoreProperties] = $coreBytes

    $temporaryPath = "$Path.normalized"
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    $file = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $output = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $timestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            foreach ($name in @($entries.Keys | Sort-Object)) {
                $entry = $output.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $timestamp
                $stream = $entry.Open()
                try {
                    $bytes = $entries[$name]
                    $stream.Write($bytes, 0, $bytes.Length)
                }
                finally { $stream.Dispose() }
            }
        }
        finally { $output.Dispose() }
    }
    finally { $file.Dispose() }

    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

$archivePaths = @(@{ Path = $PackagePath; Kind = 'nupkg' })
if (-not [string]::IsNullOrWhiteSpace($SymbolPackagePath)) {
    $archivePaths += @{ Path = $SymbolPackagePath; Kind = 'snupkg' }
}

foreach ($archive in $archivePaths) {
    Normalize-Archive -Path $archive.Path -Kind $archive.Kind
}
