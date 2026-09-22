param(
    [string] $Version,
    [string] $Tag,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path

function Get-ReleaseSection {
    param(
        [string] $Text,
        [string] $ExpectedVersion
    )

    $escapedVersion = [regex]::Escape($ExpectedVersion)
    $heading = [regex]::Match($Text, "(?m)^## \[$escapedVersion\]\s+-\s+(\d{4}-\d{2}-\d{2})\s*$")
    if (-not $heading.Success) {
        throw "CHANGELOG.md does not contain a dated release entry for version $ExpectedVersion."
    }

    $nextHeading = [regex]::Match($Text.Substring($heading.Index + $heading.Length), '(?m)^##\s+')
    $sectionLength = if ($nextHeading.Success) { $nextHeading.Index } else { $Text.Length - ($heading.Index + $heading.Length) }
    [pscustomobject]@{
        Date = $heading.Groups[1].Value
        Body = $Text.Substring($heading.Index + $heading.Length, $sectionLength)
    }
}

function Test-ReleaseContract {
    param(
        [string] $RepositoryRoot,
        [string] $ExpectedVersion,
        [string] $ExpectedTag
    )

    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "Release version must use MAJOR.MINOR.PATCH: $ExpectedVersion"
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedTag)) { $ExpectedTag = "v$ExpectedVersion" }
    if ($ExpectedTag -ne "v$ExpectedVersion") {
        throw "Release tag $ExpectedTag does not match version $ExpectedVersion."
    }

    $projectPath = Join-Path $RepositoryRoot 'src/KeelMatrix.CliContract.Tool/KeelMatrix.CliContract.Tool.csproj'
    $readmePath = Join-Path $RepositoryRoot 'README.md'
    $changelogPath = Join-Path $RepositoryRoot 'CHANGELOG.md'
    foreach ($path in @($projectPath, $readmePath, $changelogPath)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Required release file is missing: $path" }
    }

    [xml] $project = Get-Content -Raw -LiteralPath $projectPath
    $packageIdNode = $project.SelectSingleNode('/Project/PropertyGroup/PackageId')
    $versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $packageIdNode -or $packageIdNode.InnerText -ne 'KeelMatrix.CliContract') {
        throw 'The shipping project package id is not KeelMatrix.CliContract.'
    }
    if ($null -eq $versionNode -or $versionNode.InnerText -ne $ExpectedVersion) {
        throw "The shipping project version does not match $ExpectedVersion."
    }

    $readme = Get-Content -Raw -LiteralPath $readmePath
    if ($readme -notmatch 'dotnet tool install --global KeelMatrix\.CliContract') {
        throw 'README.md does not contain the public tool installation command.'
    }

    $changelog = Get-Content -Raw -LiteralPath $changelogPath
    $release = Get-ReleaseSection -Text $changelog -ExpectedVersion $ExpectedVersion
    if ($release.Body -match '(?im)\b(planned|unreleased|not yet published|tbd)\b') {
        throw "The $ExpectedVersion changelog entry is still marked as not released."
    }
    if ($ExpectedVersion -eq '0.1.0') {
        if ($release.Body -notmatch '(?m)^###\s+Added\s*$') {
            throw 'The first release changelog entry must contain an Added section.'
        }
        $otherSections = @([regex]::Matches($release.Body, '(?m)^###\s+(.+)\s*$') | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -ne 'Added' })
        if ($otherSections.Count -gt 0) {
            throw 'The first release changelog entry may contain only an Added section.'
        }
        if ($release.Body -match '(?i)\b(now|no longer|previously|formerly|used to|fixed|fixes|corrected|resolved|addressed|this removes|this fixes|changed from)\b') {
            throw 'The first release changelog entry contains a transition or remediation phrase.'
        }
    }

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
# KeelMatrix CliContract

dotnet tool install --global KeelMatrix.CliContract
'@ | Set-Content -LiteralPath (Join-Path $selfTestRoot 'README.md') -Encoding utf8NoBOM

        $unreleased = @'
# Changelog

## [Unreleased]

### Added

- Initial tool.
'@
        $final = @'
# Changelog

## [0.1.0] - 2026-09-22

### Added

- Detect incompatible OpenCLI contract changes.
'@
        $mismatch = $final.Replace('[0.1.0]', '[0.1.1]')
        $transition = $final.Replace('Detect incompatible', 'Fixed incompatible')

        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $unreleased -Encoding utf8NoBOM
        try { Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'; throw 'Unreleased entry was accepted.' } catch { if ($_.Exception.Message -eq 'Unreleased entry was accepted.') { throw } }
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $final -Encoding utf8NoBOM
        Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0' | Out-Null
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $mismatch -Encoding utf8NoBOM
        try { Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'; throw 'Version mismatch was accepted.' } catch { if ($_.Exception.Message -eq 'Version mismatch was accepted.') { throw } }
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $transition -Encoding utf8NoBOM
        try { Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'; throw 'Transition wording was accepted.' } catch { if ($_.Exception.Message -eq 'Transition wording was accepted.') { throw } }
        Write-Output 'RELEASE_CONTRACT_SELF_TEST=PASS'
    }
    finally {
        if (Test-Path -LiteralPath $selfTestRoot) { Remove-Item -LiteralPath $selfTestRoot -Recurse -Force }
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Version)) { throw 'Version is required unless -SelfTest is used.' }
Test-ReleaseContract -RepositoryRoot $root -ExpectedVersion $Version -ExpectedTag $Tag
