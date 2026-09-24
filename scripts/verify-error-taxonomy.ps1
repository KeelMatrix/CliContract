$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$help = Get-Content (Join-Path $root 'src/KeelMatrix.CliContract.Tool/Program.cs') -Raw
$taxonomy = Get-Content (Join-Path $root 'docs/ERROR-TAXONOMY.md') -Raw
$surfaces = @(
    $taxonomy,
    (Get-Content (Join-Path $root 'README.md') -Raw),
    (Get-Content (Join-Path $root 'src/KeelMatrix.CliContract.Tool/README.md') -Raw),
    (Get-Content (Join-Path $root 'COMPATIBILITY-RULES.md') -Raw),
    (Get-Content (Join-Path $root 'SECURITY.md') -Raw),
    (Get-Content (Join-Path $root 'PRIVACY.md') -Raw),
    (Get-Content (Join-Path $root 'docs/OPENCLI-FRESHNESS.md') -Raw)
)

if ($help -notmatch 'Missing source or baseline paths return 2') { throw 'Help text does not state missing source/baseline exit 2.' }
if ($help -notmatch 'Present but unreadable, malformed, unsupported, invalid-UTF-8') { throw 'Help text does not state present source/baseline exit 3.' }
foreach ($surface in $surfaces) {
    if ($surface -notmatch '(?m)(^|[^0-9])2([^0-9]|$)') { throw 'A public surface omits invocation/configuration exit 2.' }
    if ($surface -notmatch '(?m)(^|[^0-9])3([^0-9]|$)') { throw 'A public surface omits input/baseline exit 3.' }
    if ($surface -notmatch '(?m)(^|[^0-9])4([^0-9]|$)') { throw 'A public surface omits unexpected failure exit 4.' }
}

Write-Output 'ERROR_TAXONOMY=PASS'
