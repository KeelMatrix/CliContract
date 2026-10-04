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
    $releaseHeadings = [regex]::Matches($Text, "(?m)^## \[$escapedVersion\][^\r\n]*$")
    if ($releaseHeadings.Count -ne 1) { throw "CHANGELOG.md must contain exactly one release entry for version $ExpectedVersion." }
    $heading = [regex]::Match($releaseHeadings[0].Value, "^## \[$escapedVersion\]\s+-\s+(\d{4}-\d{2}-\d{2})\s*$")
    if (-not $heading.Success) { throw "CHANGELOG.md does not contain a dated release entry for version $ExpectedVersion." }
    $remaining = $Text.Substring($releaseHeadings[0].Index + $releaseHeadings[0].Length)
    $nextHeading = [regex]::Match($remaining, '(?m)^##\s+')
    $sectionLength = if ($nextHeading.Success) { $nextHeading.Index } else { $remaining.Length }
    [pscustomobject]@{
        Date = $heading.Groups[1].Value
        Index = $releaseHeadings[0].Index
        Body = $remaining.Substring(0, $sectionLength)
    }
}

function Assert-ReleaseRejected {
    param([scriptblock] $Action, [string] $FailureMessage)

    try {
        & $Action
        throw $FailureMessage
    }
    catch {
        if ($_.Exception.Message -eq $FailureMessage) { throw }
    }
}

function Test-ReleaseContract {
    param([string] $RepositoryRoot, [string] $ExpectedVersion, [string] $ExpectedTag)

    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Release version must use MAJOR.MINOR.PATCH: $ExpectedVersion" }
    if ($ExpectedTag -ne "v$ExpectedVersion") { throw "Release tag $ExpectedTag does not match version $ExpectedVersion." }

    $projectPath = Join-Path $RepositoryRoot 'src/KeelMatrix.CliContract.Tool/KeelMatrix.CliContract.Tool.csproj'
    $programPath = Join-Path $RepositoryRoot 'src/KeelMatrix.CliContract.Tool/Program.cs'
    $readmePath = Join-Path $RepositoryRoot 'README.md'
    $packageReadmePath = Join-Path $RepositoryRoot 'src/KeelMatrix.CliContract.Tool/README.md'
    $changelogPath = Join-Path $RepositoryRoot 'CHANGELOG.md'
    foreach ($path in @($projectPath, $programPath, $readmePath, $packageReadmePath, $changelogPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release file is missing: $path" }
    }

    [xml] $project = Get-Content -Raw -LiteralPath $projectPath
    $packageId = $project.SelectSingleNode('/Project/PropertyGroup/PackageId')
    $version = $project.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $packageId -or $packageId.InnerText -ne 'KeelMatrix.CliContract') { throw 'The shipping project package id is incorrect.' }
    if ($null -eq $version -or $version.InnerText -ne $ExpectedVersion) { throw "The shipping project version does not match $ExpectedVersion." }
    $programVersion = [regex]::Match((Get-Content -Raw -LiteralPath $programPath), '(?m)^\s*private const string Version = "(\d+\.\d+\.\d+)";\s*$')
    if (-not $programVersion.Success -or $programVersion.Groups[1].Value -ne $ExpectedVersion) { throw "The CLI-reported version does not match $ExpectedVersion." }

    foreach ($path in @($readmePath, $packageReadmePath)) {
        if ((Get-Content -Raw -LiteralPath $path) -notmatch 'dotnet tool install --global KeelMatrix\.CliContract') {
            throw "The install command is missing from $path."
        }
    }

    $changelog = Get-Content -Raw -LiteralPath $changelogPath
    if ($changelog -notmatch '(?m)^# Changelog\s*$') { throw 'CHANGELOG.md is missing its Changelog title.' }
    $unreleasedHeadings = [regex]::Matches($changelog, '(?m)^## \[Unreleased\]\s*$')
    if ($unreleasedHeadings.Count -ne 1) { throw 'CHANGELOG.md must contain exactly one separate Unreleased section.' }
    $release = Get-ReleaseSection -Text $changelog -ExpectedVersion $ExpectedVersion
    if ($unreleasedHeadings[0].Index -ge $release.Index) { throw 'The Unreleased section must remain separate and precede released versions.' }
    if ($release.Body -match '(?im)\b(planned|unreleased|not yet published|tbd)\b') { throw "The $ExpectedVersion changelog entry is not final." }
    $sectionHeadings = [regex]::Matches($release.Body, '(?m)^###\s+(.+?)\s*$')
    if ($ExpectedVersion -eq '0.1.0') {
        if ($sectionHeadings.Count -ne 1 -or $sectionHeadings[0].Groups[1].Value -cne 'Added') {
            throw 'The first release changelog entry must contain only one Added section.'
        }
        if ($release.Body -notmatch '(?m)^\s*-\s+\S') { throw 'The first release Added section must contain at least one consumer-facing entry.' }
    }
    elseif ($release.Body -notmatch '(?im)^###\s+(Added|Highlights|Features)\s*$') {
        throw "The $ExpectedVersion changelog entry has no finalized feature section."
    }

    $internalReleaseTerms = '(?im)\b(remediation|front' + 'ier review|rejection|regression fix)\b'
    if ($release.Body -match $internalReleaseTerms) {
        throw "The $ExpectedVersion changelog entry contains internal remediation wording."
    }
    if ($ExpectedVersion -eq '0.1.0') {
        $firstReleaseTransitionTerms = '(?im)\b(now|no\s+longer|previously|formerly|used\s+to|fix(?:es|ed)?|corrected|resolved|addressed|this\s+removes|this\s+fixes|changed\s+from)\b'
        if ($release.Body -match $firstReleaseTransitionTerms) {
            throw 'The first release changelog entry contains pre-release remediation or transition wording.'
        }
    }
    $parsedReleaseDate = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($release.Date, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$parsedReleaseDate)) {
        throw 'The changelog release date is invalid.'
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
internal static class Program
{
    private const string Version = "0.1.0";
}
'@ | Set-Content -LiteralPath (Join-Path $selfTestRoot 'src/KeelMatrix.CliContract.Tool/Program.cs') -Encoding utf8NoBOM
        @'
# KeelMatrix.CliContract

dotnet tool install --global KeelMatrix.CliContract
'@ | Set-Content -LiteralPath (Join-Path $selfTestRoot 'README.md') -Encoding utf8NoBOM
        Copy-Item -LiteralPath (Join-Path $selfTestRoot 'README.md') -Destination (Join-Path $selfTestRoot 'src/KeelMatrix.CliContract.Tool/README.md')
        @'
# Changelog

## [Unreleased]

## [0.1.0] - 2026-10-04

### Added

- Detect incompatible OpenCLI contract changes.
'@ | Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Encoding utf8NoBOM
        Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0' | Out-Null
        Assert-ReleaseRejected -FailureMessage 'A package version mismatch was accepted.' -Action {
            Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.1' -ExpectedTag 'v0.1.1'
        }
        Assert-ReleaseRejected -FailureMessage 'A tag version mismatch was accepted.' -Action {
            Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.1'
        }

        $validChangelog = Get-Content -Raw -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md')
        foreach ($invalidCategory in @('Changed', 'Fixed', 'Highlights', 'Features')) {
            $invalid = $validChangelog.Replace('### Added', "### $invalidCategory")
            Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $invalid -Encoding utf8NoBOM
            Assert-ReleaseRejected -FailureMessage "The first release accepted the $invalidCategory category." -Action {
                Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'
            }
        }

        $mixedCategories = $validChangelog.Replace('- Detect incompatible OpenCLI contract changes.', "- Detect incompatible OpenCLI contract changes.`n`n### Fixed`n`n- Correct a published defect.")
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $mixedCategories -Encoding utf8NoBOM
        Assert-ReleaseRejected -FailureMessage 'A mixed-category first release was accepted.' -Action {
            Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'
        }

        $emptyAdded = $validChangelog.Replace('- Detect incompatible OpenCLI contract changes.', 'No release entry.')
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $emptyAdded -Encoding utf8NoBOM
        Assert-ReleaseRejected -FailureMessage 'An empty Added section was accepted.' -Action {
            Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'
        }

        foreach ($marker in @('now supports', 'no longer supports', 'previously supported', 'formerly supported', 'used to support', 'fixed behavior', 'fixes behavior', 'corrected behavior', 'resolved behavior', 'addressed behavior', 'this removes behavior', 'this fixes behavior', 'changed from behavior')) {
            $invalid = $validChangelog.Replace('- Detect incompatible OpenCLI contract changes.', "- $marker.")
            Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $invalid -Encoding utf8NoBOM
            Assert-ReleaseRejected -FailureMessage "The first release accepted remediation wording '$marker'." -Action {
                Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'
            }
        }

        $invalidDate = $validChangelog.Replace('2026-10-04', '2026-02-30')
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $invalidDate -Encoding utf8NoBOM
        Assert-ReleaseRejected -FailureMessage 'An invalid calendar date was accepted.' -Action {
            Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'
        }

        $duplicate = $validChangelog + "`n## [0.1.0] - 2026-10-04`n`n### Added`n`n- Duplicate release entry."
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $duplicate -Encoding utf8NoBOM
        Assert-ReleaseRejected -FailureMessage 'A duplicate release version was accepted.' -Action {
            Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'
        }

        $programPath = Join-Path $selfTestRoot 'src/KeelMatrix.CliContract.Tool/Program.cs'
        $program = Get-Content -Raw -LiteralPath $programPath
        Set-Content -LiteralPath $programPath -Value $program.Replace('0.1.0', '0.1.1') -Encoding utf8NoBOM
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $validChangelog -Encoding utf8NoBOM
        Assert-ReleaseRejected -FailureMessage 'A mismatched CLI-reported version was accepted.' -Action {
            Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'
        }
        Set-Content -LiteralPath $programPath -Value $program -Encoding utf8NoBOM

        $planned = $validChangelog -replace '\[0\.1\.0\]', '[Unreleased]'
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'CHANGELOG.md') -Value $planned -Encoding utf8NoBOM
        Assert-ReleaseRejected -FailureMessage 'An unreleased changelog was accepted.' -Action {
            Test-ReleaseContract -RepositoryRoot $selfTestRoot -ExpectedVersion '0.1.0' -ExpectedTag 'v0.1.0'
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
