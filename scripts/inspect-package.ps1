param(
    [Parameter(Mandatory = $true)] [string] $PackagePath,
    [switch] $AllowMissingIcon
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $temp)
    $entries = [IO.Compression.ZipFile]::OpenRead($packagePath).Entries | ForEach-Object FullName
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
    $required = @('README.md', 'KeelMatrix.CliContract.nuspec', 'tools/net8.0/any/KeelMatrix.CliContract.dll', 'tools/net8.0/any/KeelMatrix.CliContract.Core.dll', 'tools/net8.0/any/KeelMatrix.Telemetry.dll', 'tools/net8.0/any/YamlDotNet.dll')
    foreach ($entry in $required) { if ($entries -notcontains $entry) { throw "Required package entry is missing: $entry" } }
    if ($entries -notcontains 'LICENSE') { throw 'The package must contain LICENSE.' }
    $unexpected = @($entries | Where-Object { $_ -notmatch '^(_rels/[^/]+|\[Content_Types\].xml|package/services/metadata/core-properties/[^/]+.psmdcp|README.md|LICENSE|icon.png|[^/]+.nuspec|tools/net8.0/any/[^/]+)$' })
    if ($unexpected.Count -gt 0) { throw "Unexpected package entries: $($unexpected -join ', ')" }
    $sensitive = @($entries | Where-Object { $_ -match '(^|/)(.env|.env.|appsettings|secrets?|.*.key|.*.pfx|AGENTS.md|.*test.*)' })
    if ($sensitive.Count -gt 0) { throw "Sensitive or test package entries found: $($sensitive -join ', ')" }
    $forbiddenSurfacePatterns = @(
        '(?i)\bprobe\b',
        '(?i)\bphase\s*0\b',
        '(?i)\bevidence\b',
        '(?i)\borchestration\b',
        '(?i)\b(codex|paperclip|frontier)\b',
        '(?i)\b(agent|model)\b',
        '(?i)model[- ]routing',
        '(?i)task[- ]id',
        '(?i)agent[- ]id',
        '(?i)company[- ]internal',
        '(?i)\binternal[_ -]?error\b',
        '(?i)\binternal\s+analysis\s+error\b',
        '\bKEE-\d+\b'
    )
    foreach ($entry in $entries | Where-Object { $_ -notmatch '.(dll|pdb|json)$' }) {
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
