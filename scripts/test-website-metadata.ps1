$ErrorActionPreference = 'Stop'
$scratchRoot = [IO.Path]::GetFullPath($(if ($env:PAPERCLIP_RUN_SCRATCH_DIR) { $env:PAPERCLIP_RUN_SCRATCH_DIR } else { [IO.Path]::GetTempPath() }))
$temp = Join-Path $scratchRoot ('clicontract-website-' + [Guid]::NewGuid().ToString('N'))
$validJson = '{"schemaVersion":1,"packages":{"KeelMatrix.CliContract":{"visibility":"public-product","role":"primary"}}}'

function Assert-Rejected([string] $Name, [scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Website metadata negative control accepted $Name." }
    Write-Output "WEBSITE_NEGATIVE_CONTROL=$Name PASS"
}

function Write-TestPackage($Package) {
    $path = Join-Path $temp 'classification.nupkg'
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry('classification.nuspec')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try {
            $id = [Security.SecurityElement]::Escape($Package.PackageId)
            $tags = [Security.SecurityElement]::Escape($Package.PackageTags)
            $projectUrl = [Security.SecurityElement]::Escape($Package.PackageProjectUrl)
            $sourceUrl = [Security.SecurityElement]::Escape($Package.RepositoryUrl)
            $type = [Security.SecurityElement]::Escape($Package.RepositoryType)
            $writer.Write("<package><metadata><id>$id</id><tags>$tags</tags><projectUrl>$projectUrl</projectUrl><repository type=`"$type`" url=`"$sourceUrl`" /></metadata></package>")
        }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
    return $path
}

try {
    New-Item -ItemType Directory -Path $temp | Out-Null
    [IO.File]::WriteAllText((Join-Path $temp 'Directory.Build.props'), @'
<Project><PropertyGroup>
<PackageId>KeelMatrix.CliContract</PackageId>
<PackageTags>cli;keelmatrix-public-product;keelmatrix-primary</PackageTags>
<PackageProjectUrl>https://github.com/KeelMatrix/CliContract</PackageProjectUrl>
<RepositoryUrl>https://github.com/KeelMatrix/CliContract</RepositoryUrl>
<RepositoryType>git</RepositoryType>
</PropertyGroup></Project>
'@)
    [IO.File]::WriteAllText((Join-Path $temp 'Tool.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $temp 'Core.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup></Project>')
    $packages = @(Get-PackageProjects $temp)
    if ($packages.Count -ne 1) { throw 'MSBuild package discovery must exclude non-packable projects.' }
    $seed = $packages[0]
    Assert-Inventory $packages (Read-Classification $validJson)
    Assert-PackedClassification (Write-TestPackage $seed) (Read-Classification $validJson)
    Assert-Rejected 'no-packable-projects' { Assert-Inventory @() (Read-Classification $validJson) }
    Assert-Rejected 'duplicate-package-id' { Assert-Inventory @($seed, $seed) (Read-Classification $validJson) }

    foreach ($case in @(
        @{ Name = 'unsupported-schema'; Json = $validJson.Replace('"schemaVersion":1', '"schemaVersion":2') },
        @{ Name = 'string-schema'; Json = $validJson.Replace('"schemaVersion":1', '"schemaVersion":"1"') },
        @{ Name = 'missing-schema'; Json = $validJson.Replace('"schemaVersion":1,', '') },
        @{ Name = 'unknown-field'; Json = $validJson.Replace('"role":"primary"', '"role":"primary","version":"0.1.0"') },
        @{ Name = 'duplicate-package'; Json = $validJson.Replace('"KeelMatrix.CliContract":', '"KeelMatrix.CliContract":{},"KeelMatrix.CliContract":') },
        @{ Name = 'duplicate-classification'; Json = $validJson.Replace('"role":"primary"', '"role":"component","role":"primary"') },
        @{ Name = 'invalid-visibility'; Json = $validJson.Replace('public-product', 'public') },
        @{ Name = 'invalid-role'; Json = $validJson.Replace('primary', 'Primary') },
        @{ Name = 'null-packages'; Json = '{"schemaVersion":1,"packages":null}' },
        @{ Name = 'trailing-comma'; Json = $validJson.Replace('"primary"}', '"primary",}') }
    )) {
        Assert-Rejected $case.Name { Read-Classification $case.Json }
    }

    foreach ($case in @(
        @{ Name = 'missing-entry'; Json = '{"schemaVersion":1,"packages":{}}' },
        @{ Name = 'stale-entry'; Json = $validJson.Replace('"packages":{', '"packages":{"Old.Package":{"visibility":"public-product","role":"component"},') },
        @{ Name = 'wrong-id-case'; Json = $validJson.Replace('KeelMatrix.CliContract', 'keelmatrix.clicontract') },
        @{ Name = 'manifest-source-disagreement'; Json = $validJson.Replace('public-product', 'internal-package') }
    )) {
        Assert-Rejected $case.Name { Assert-Inventory $packages (Read-Classification $case.Json) }
    }

    foreach ($case in @(
        @{ Name = 'missing-visibility'; Key = 'PackageTags'; Value = 'cli;keelmatrix-primary' },
        @{ Name = 'missing-role'; Key = 'PackageTags'; Value = 'cli;keelmatrix-public-product' },
        @{ Name = 'both-visibilities'; Key = 'PackageTags'; Value = "$($seed.PackageTags);keelmatrix-internal-package" },
        @{ Name = 'both-roles'; Key = 'PackageTags'; Value = "$($seed.PackageTags);keelmatrix-component" },
        @{ Name = 'duplicate-sentinel'; Key = 'PackageTags'; Value = "$($seed.PackageTags);keelmatrix-primary" },
        @{ Name = 'sentinel-case'; Key = 'PackageTags'; Value = $seed.PackageTags.Replace('keelmatrix-primary', 'KeelMatrix-Primary') },
        @{ Name = 'source-disagreement'; Key = 'PackageTags'; Value = $seed.PackageTags.Replace('public-product', 'internal-package') },
        @{ Name = 'role-disagreement'; Key = 'PackageTags'; Value = $seed.PackageTags.Replace('primary', 'component') },
        @{ Name = 'unknown-package-id'; Key = 'PackageId'; Value = 'Old.Package' },
        @{ Name = 'package-id-case'; Key = 'PackageId'; Value = 'keelmatrix.clicontract' },
        @{ Name = 'wrong-project-url'; Key = 'PackageProjectUrl'; Value = 'https://keelmatrix.com' },
        @{ Name = 'wrong-repository-url'; Key = 'RepositoryUrl'; Value = 'https://github.com/KeelMatrix/Other' },
        @{ Name = 'wrong-repository-type'; Key = 'RepositoryType'; Value = 'svn' }
    )) {
        $changed = $seed.Clone()
        $changed[$case.Key] = $case.Value
        Assert-Rejected "source-$($case.Name)" { Assert-Inventory @($changed) (Read-Classification $validJson) }
        Assert-Rejected "packed-$($case.Name)" { Assert-PackedClassification (Write-TestPackage $changed) (Read-Classification $validJson) }
    }
    $second = $seed.Clone()
    $second.PackageId = 'KeelMatrix.CliContract.Component'
    $twoPrimaries = $validJson.Replace('"packages":{', '"packages":{"KeelMatrix.CliContract.Component":{"visibility":"public-product","role":"primary"},')
    Assert-Rejected 'multiple-primaries' { Assert-Inventory @($seed, $second) (Read-Classification $twoPrimaries) }
    $second.PackageTags = $seed.PackageTags.Replace('primary', 'component')
    Assert-Inventory @($seed, $second) (Read-Classification ($twoPrimaries.Replace('"role":"primary"},', '"role":"component"},')))
    $component = $seed.Clone()
    $component.PackageTags = $seed.PackageTags.Replace('primary', 'component')
    Assert-Rejected 'no-primary' { Assert-Inventory @($component) (Read-Classification $validJson.Replace('primary', 'component')) }
    $internal = $seed.Clone()
    $internal.PackageTags = $seed.PackageTags.Replace('public-product', 'internal-package')
    Assert-Inventory @($internal) (Read-Classification $validJson.Replace('public-product', 'internal-package'))
    $internal.PackageId = $second.PackageId
    $hiddenPrimary = $twoPrimaries.Replace('"visibility":"public-product","role":"primary"},', '"visibility":"internal-package","role":"primary"},').Replace('"role":"primary"}}}', '"role":"component"}}}')
    Assert-Rejected 'internal-primary-with-public-component' { Assert-Inventory @($component, $internal) (Read-Classification $hiddenPrimary) }
    Write-Output 'WEBSITE_METADATA_SELF_TEST=PASS'
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath($temp)
    $scratchBoundary = $scratchRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTemp.StartsWith($scratchBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup escaped the scratch directory.' }
    if (Test-Path -LiteralPath $resolvedTemp) { Remove-Item -LiteralPath $resolvedTemp -Recurse -Force }
}
