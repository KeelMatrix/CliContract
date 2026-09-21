$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# This is the complete pre-package inventory of shipped or user-facing text:
# runtime diagnostics/help, package metadata, package README, root documentation,
# and any documentation file added under docs/.
$surfaceFiles = @(
    'README.md',
    'COMPATIBILITY-RULES.md',
    'MANIFEST.md',
    'PRIVACY.md',
    'SECURITY.md',
    'CONTRIBUTING.md',
    'CHANGELOG.md',
    'src/KeelMatrix.CliContract.Tool/Program.cs',
    'src/KeelMatrix.CliContract.Tool/README.md',
    'src/KeelMatrix.CliContract.Tool/KeelMatrix.CliContract.Tool.csproj',
    'src/KeelMatrix.CliContract.Core/Normalization.cs'
)
$docsPath = Join-Path $root 'docs'
if (Test-Path -LiteralPath $docsPath) {
    $surfaceFiles += @(Get-ChildItem -LiteralPath $docsPath -File -Recurse | ForEach-Object {
        $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    })
}

$patterns = @(
    '(?i)\bprobe\b',
    '(?i)\bphase\s*0\b',
    '(?i)\bevidence\b',
    '(?i)\borchestration\b',
    '(?i)\b(codex|paperclip|frontier)\b',
    '(?i)\b(agent|model)\b',
    '(?i)\binternal[_ -]?error\b',
    '(?i)\binternal\s+analysis\s+error\b',
    '\bKEE-\d+\b'
)

$missing = @($surfaceFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) })
if ($missing.Count -gt 0) {
    throw "User-facing surface inventory contains missing file(s): $($missing -join ', ')"
}

$hits = foreach ($relativePath in $surfaceFiles) {
    $path = Join-Path $root $relativePath
    $content = Get-Content -Raw -LiteralPath $path
    foreach ($pattern in $patterns) {
        if ($content -match $pattern) {
            [pscustomobject]@{ Path = $relativePath; Pattern = $pattern }
        }
    }
}

Write-Output "SURFACE_INVENTORY_COUNT=$($surfaceFiles.Count)"
$surfaceFiles | ForEach-Object { Write-Output "SURFACE_FILE=$($_)" }
if ($hits.Count -gt 0) {
    $hits | ForEach-Object { Write-Output "SURFACE_WORDING_HIT path=$($_.Path) pattern=$($_.Pattern)" }
    throw 'Forbidden internal or implementation wording found in the shipped/user-facing surface.'
}

Write-Output 'SURFACE_WORDING_SCAN=PASS'
