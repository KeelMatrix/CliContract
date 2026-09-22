param(
    [Parameter(Mandatory = $true)] [string] $Tag,
    [string] $OutputEnvironmentFile
)

$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '\Av(?<version>\d+\.\d+\.\d+)\z') {
    throw 'Unsupported release tag. Expected vMAJOR.MINOR.PATCH.'
}

$version = $Matches.version
if (-not [string]::IsNullOrWhiteSpace($OutputEnvironmentFile)) {
    "RELEASE_VERSION=$version" | Out-File -LiteralPath $OutputEnvironmentFile -Encoding utf8 -Append
}

Write-Output "RELEASE_TAG=PASS version=$version"
