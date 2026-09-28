param(
    [string] $Tag,
    [string] $OutputEnvironmentFile,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

function Get-ReleaseVersion([string] $CandidateTag) {
    if ($CandidateTag -notmatch '\Av(?<version>\d+\.\d+\.\d+)\z') {
        throw 'Unsupported release tag. Expected vMAJOR.MINOR.PATCH.'
    }

    return $Matches.version
}

if ($SelfTest) {
    foreach ($valid in @('v0.1.0', 'v12.34.567')) {
        if ((Get-ReleaseVersion $valid) -notmatch '^\d+\.\d+\.\d+$') { throw "Valid tag was rejected: $valid" }
    }
    foreach ($invalid in @('0.1.0', 'v1.2', 'v1.2.3-beta', 'v1.2.3+meta', 'v1.2.3.4')) {
        try { Get-ReleaseVersion $invalid | Out-Null; throw "Malformed tag was accepted: $invalid" }
        catch { if ($_.Exception.Message -like 'Malformed tag was accepted:*') { throw } }
    }
    Write-Output 'RELEASE_TAG_SELF_TEST=PASS'
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Tag)) { throw 'Tag is required unless -SelfTest is used.' }
$version = Get-ReleaseVersion $Tag
if (-not [string]::IsNullOrWhiteSpace($OutputEnvironmentFile)) {
    "RELEASE_VERSION=$version" | Out-File -LiteralPath $OutputEnvironmentFile -Encoding utf8 -Append
}

Write-Output "RELEASE_TAG=PASS version=$version"
