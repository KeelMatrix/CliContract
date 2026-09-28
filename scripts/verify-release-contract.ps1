param(
    [string] $Version,
    [string] $Tag,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

function Get-ReleaseSection {
    param([string] $Text, [string] $ExpectedVersion)

    $escapedVersion = [regex]::Escape($ExpectedVersion)
    $heading = [regex]::Match($Text, "(?m)^## \[$escapedVersion\]\s+-\s+(\d{4}-\d{2}-\d{2})\s*$")
    if (-not $heading.Success) { throw "CHANGELOG.md does not contain a dated release entry for version $ExpectedVersion." }
    $remaining = $Text.Substring($heading.Index + $heading.Length)
    $nextHeading = [regex]::Match($remaining, '(?m)^##\s+')
    $sectionLength = if ($nextHeading.Success) { $nextHeading.Index } else { $remaining.Length }
    [pscustomobject]@{
        Date = $heading.Groups[1].Value
        Body = $remaining.Substring(0, $sectionLength)
    }
}

function Test-ReleaseContract {
    param([string] $RepositoryRoot, [string] $ExpectedVersion, [string] $ExpectedTag)

    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Release version must use MAJOR.MINOR.PATCH: $ExpectedVersion" }
    if ($ExpectedTag -ne "v$ExpectedVersion") { throw "Release tag $ExpectedTag does not match version $ExpectedVersion." }

    $projectPath = Join-Path $RepositoryRoot 'src/KeelMatrix.CliContract.Tool/KeelMatrix.CliContract.Tool.csproj'
    $readmePath = Join-Path $RepositoryRoot 'README.md'
    $packageReadmePath = Join-Path $RepositoryRoot 'src/KeelMatrix.CliContract.Tool/README.md'
    $changelogPath = Join-Path $RepositoryRoot 'CHANGELOG.md'
    foreach ($path in @($projectPath, $readmePath, $packageReadmePath, $changelogPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release file is missing: $path" }
    }

    [xml] $project = Get-Content -Raw -LiteralPath $projectPath
    $packageId = $project.SelectSingleNode('/Project/PropertyGroup/PackageId')
    $version = $project.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $packageId -or $packageId.InnerText -ne 'KeelMatrix.CliContract') { throw 'The shipping project package id is incorrect.' }
    if ($null -eq $version -or $version.InnerText -ne $ExpectedVersion) { throw "The shipping project version does not match $ExpectedVersion." }

    foreach ($path in @($readmePath, $packageReadmePath)) {
        if ((Get-Content -Raw -LiteralPath $path) -notmatch 'dotnet tool install --global KeelMatrix\.CliContract') {
            throw "The install command is missing from $path."
        }
    }

    $release = Get-ReleaseSection -Text (Get-Content -Raw -LiteralPath $changelogPath) -ExpectedVersion $ExpectedVersion
    if ($release.Body -match '(?im)\b(planned|unreleased|not yet published|tbd)\b') { throw "The $ExpectedVersion changelog entry is not final." }
    if ($release.Body -notmatch '(?im)^###\s+(Added|Highlights|Features)\s*$') { throw "The $ExpectedVersion changelog entry has no finalized feature section." }
    $internalReleaseTerms = '(?im)\b(now fixed|previously|formerly|used to|remediation|' + 'front' + 'ier review|rejection|regression fix|corrected|resolved)\b'
    if ($release.Body -match $internalReleaseTerms) {
        throw "The $ExpectedVersion changelog entry contains internal remediation wording."
    }
    if ($release.Date -notmatch '^\d{4}-\d{2}-\d{2}$') { throw 'The changelog release date is invalid.' }

    Write-Output "RELEASE_CONTRACT=PASS version=$ExpectedVersion tag=$ExpectedTag date=$($release.Date)"
}

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-release-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path (Join-Path $selfTestRoot 'src/KeelMatrix.CliContract.Tool') -Force | Out-Null
        @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KeelMatrix.CliContract</PackageId>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $selfTestRoot 'src/KeelMatrix.CliContract.Tool/KeelMatrix.CliContract.Tool.csproj') -Encoding utf8NoBOM
        @'
# KeelMatrix.CliContract

dotnet tool install --global KeelMatrix.CliContract
'@ | Set-Content -LiteralPath (Join-Path $selfTestRoot 'README.md') -Encoding utf8NoBOM
        Copy-Item -LiteralPath (Join-Path $selfTestRoot 'README.md') -Destination (Join-Path $selfTestRoot 'src/KeelMatrix.CliContract.Tool/README.md')
        @'
# Changelog

## [0.1.0] - 2026-09-22

### Added

- Detect incompatible OpenCLI contract changes.
'@ | Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Encoding utf8NoBOM
        Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0' | Out-Null
        try {
            Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.1' -ExpectedTag 'v0.1.1'
            throw 'A version mismatch was accepted.'
        }
        catch {
            if ($_.Exception.Message -eq 'A version mismatch was accepted.') { throw }
        }
        $planned = Get-Content -Raw -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md')
        $planned = $planned -replace '\[0\.1\.0\]', '[Unreleased]'
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $planned -Encoding utf8NoBOM
        try {
            Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'
            throw 'An unreleased changelog was accepted.'
        }
        catch {
            if ($_.Exception.Message -eq 'An unreleased changelog was accepted.') { throw }
        }
        Write-Output 'RELEASE_CONTRACT_SELF_TEST=PASS'
    }
    finally {
        if (Test-Path -LiteralPath $selfTestRoot) { Remove-Item -LiteralPath $selfTestRoot -Recurse -Force }
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Version) -or [string]::IsNullOrWhiteSpace($Tag)) { throw 'Version and tag are required unless -SelfTest is used.' }
Test-ReleaseContract -RepositoryRoot $root -ExpectedVersion $Version -ExpectedTag $Tag
