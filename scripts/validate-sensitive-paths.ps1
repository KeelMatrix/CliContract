param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $PolicyPath = (Join-Path $PSScriptRoot 'sensitive-path-policy.json'),
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$policy = Get-Content -Raw -LiteralPath (Resolve-Path -LiteralPath $PolicyPath) | ConvertFrom-Json
$patterns = @($policy.families | ForEach-Object { $_.regex })

function Find-PolicyMatches([string[]] $paths) {
    foreach ($path in $paths) {
        $normalized = $path.Replace('\', '/')
        foreach ($pattern in $patterns) {
            if ($normalized -match $pattern) {
                [PSCustomObject]@{ Path = $normalized; Pattern = $pattern }
                break
            }
        }
    }
}

if ($SelfTest) {
    $samples = @('.env.local', 'config/appsettings.Development.json', 'logs/tool.log', 'secrets/token.key', 'candidate.nupkg')
    $matches = @(Find-PolicyMatches $samples)
    if ($matches.Count -ne $samples.Count) {
        throw 'Sensitive-path policy self-test did not cover every acceptance-policy sample.'
    }
    Write-Output 'SENSITIVE_PATH_POLICY_SELF_TEST=PASS'
}

$tracked = @(git -C $root ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate tracked files.' }
$matches = @(Find-PolicyMatches $tracked)
if ($matches.Count -gt 0) {
    throw "Tracked sensitive paths are not permitted: $($matches.Path -join ', ')"
}

Write-Output "SENSITIVE_PATH_GATE=PASS tracked_files=$($tracked.Count)"
