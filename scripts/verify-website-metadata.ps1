param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $PackagePath,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$repositoryUrl = 'https://github.com/KeelMatrix/CliContract'
$propertyNames = 'IsPackable,PackageId,PackageTags,PackageProjectUrl,RepositoryUrl,RepositoryType'

function Assert-ObjectProperties($Element, [string[]] $Names) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Manifest objects must be JSON objects.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $seen.Add($property.Name)) { throw "Duplicate manifest property: $($property.Name)" }
        if ($null -ne $Names -and $property.Name -cnotin $Names) { throw "Unknown manifest property: $($property.Name)" }
    }
    foreach ($name in $Names) {
        if (-not $seen.Contains($name)) { throw "Missing manifest property: $name" }
    }
}

function Read-Classification([string] $Text) {
    $document = [System.Text.Json.JsonDocument]::Parse($Text)
    try {
        $root = $document.RootElement
        Assert-ObjectProperties $root @('schemaVersion', 'packages')
        $version = $root.GetProperty('schemaVersion')
        if ($version.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or $version.GetRawText() -cne '1') {
            throw 'Website metadata requires schemaVersion 1.'
        }
        $packages = $root.GetProperty('packages')
        Assert-ObjectProperties $packages $null
        foreach ($package in $packages.EnumerateObject()) {
            if ([string]::IsNullOrWhiteSpace($package.Name)) { throw 'Manifest package IDs must be nonempty.' }
            Assert-ObjectProperties $package.Value @('visibility', 'role')
            if ($package.Value.GetProperty('visibility').GetString() -cnotin @('public-product', 'internal-package')) {
                throw "Invalid visibility for $($package.Name)."
            }
            if ($package.Value.GetProperty('role').GetString() -cnotin @('primary', 'component')) {
                throw "Invalid role for $($package.Name)."
            }
        }
    }
    finally { $document.Dispose() }
    return ($Text | ConvertFrom-Json -AsHashtable).packages
}

function Get-PackageProjects([string] $Root) {
    $projects = @(Get-ChildItem -LiteralPath $Root -Filter '*.csproj' -Recurse -File | Where-Object {
        [IO.Path]::GetRelativePath($Root, $_.FullName) -notmatch '(^|[\\/])(bin|obj|artifacts|\.git)([\\/]|$)'
    })
    foreach ($project in $projects) {
        $output = & dotnet msbuild $project.FullName -nologo "-getProperty:$propertyNames" -p:Configuration=Release
        if ($LASTEXITCODE -ne 0) { throw "Could not evaluate package properties: $($project.FullName)" }
        $properties = ($output -join "`n" | ConvertFrom-Json -AsHashtable).Properties
        if ($properties.IsPackable -eq 'true') { $properties }
    }
}

function Assert-PackageClassification($Package, $Classification) {
    if ($Package.PackageProjectUrl -cne $repositoryUrl -or $Package.RepositoryUrl -cne $repositoryUrl -or
        $Package.RepositoryType -cne 'git') {
        throw "Package source URLs/type must identify $repositoryUrl (git): $($Package.PackageId)"
    }
    $tags = @($Package.PackageTags -split '[;\s]+' | Where-Object { $_.Length -gt 0 })
    $visibilityTags = @($tags | Where-Object { $_ -imatch '^keelmatrix-(public-product|internal-package)$' })
    $roleTags = @($tags | Where-Object { $_ -imatch '^keelmatrix-(primary|component)$' })
    if ($visibilityTags.Count -ne 1 -or $roleTags.Count -ne 1 -or
        $visibilityTags[0] -cne "keelmatrix-$($Classification.visibility)" -or
        $roleTags[0] -cne "keelmatrix-$($Classification.role)") {
        throw "Package sentinels must occur exactly once and agree with the manifest: $($Package.PackageId)"
    }
}

function Assert-Inventory($Packages, $Manifest) {
    if ($Packages.Count -eq 0) { throw 'No packable projects were found.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in $Packages) {
        if ([string]::IsNullOrWhiteSpace($package.PackageId) -or -not $seen.Add($package.PackageId)) {
            throw 'Packable package IDs must be nonempty and unique.'
        }
        if (-not $Manifest.Contains($package.PackageId)) { throw "Missing manifest entry: $($package.PackageId)" }
        Assert-PackageClassification $package $Manifest[$package.PackageId]
    }
    foreach ($id in $Manifest.Keys) {
        if ($id -cnotin @($Packages | ForEach-Object { $_.PackageId })) { throw "Stale manifest entry: $id" }
    }
    $primaries = @($Manifest.Values | Where-Object { $_.role -ceq 'primary' }).Count
    $publicPackages = @($Manifest.Values | Where-Object { $_.visibility -ceq 'public-product' }).Count
    $publicPrimaries = @($Manifest.Values | Where-Object { $_.visibility -ceq 'public-product' -and $_.role -ceq 'primary' }).Count
    if ($primaries -gt 1 -or ($publicPackages -gt 0 -and $publicPrimaries -ne 1)) {
        throw 'The public product family requires exactly one primary package.'
    }
}

function Assert-PackedClassification([string] $Path, $Manifest) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' })
        if ($entries.Count -ne 1) { throw 'Expected exactly one root nuspec.' }
        $stream = $entries[0].Open()
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try {
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
        }
        finally { $reader.Dispose(); $stream.Dispose() }
        $metadata = $document.package.metadata
        $package = @{
            PackageId = [string] $metadata.id
            PackageTags = [string] $metadata.tags
            PackageProjectUrl = [string] $metadata.projectUrl
            RepositoryUrl = [string] $metadata.repository.url
            RepositoryType = [string] $metadata.repository.type
        }
        if ($package.PackageId -cnotin @($Manifest.Keys)) { throw "Packed package is missing from manifest: $($package.PackageId)" }
        Assert-PackageClassification $package $Manifest[$package.PackageId]
        Write-Output "WEBSITE_PACKED_METADATA=PASS id=$($package.PackageId)"
    }
    finally { $archive.Dispose() }
}

if ($SelfTest) {
    & (Join-Path $PSScriptRoot 'test-website-metadata.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Website metadata negative controls failed.' }
}
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$manifest = Read-Classification (Get-Content -Raw -LiteralPath (Join-Path $root 'keelmatrix.website.json'))
$packages = @(Get-PackageProjects $root)
Assert-Inventory $packages $manifest
if (-not [string]::IsNullOrWhiteSpace($PackagePath)) { Assert-PackedClassification $PackagePath $manifest }
foreach ($package in $packages) {
    $classification = $manifest[$package.PackageId]
    Write-Output "WEBSITE_METADATA=PASS id=$($package.PackageId) visibility=$($classification.visibility) role=$($classification.role)"
}
