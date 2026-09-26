param(
    [string] $RootPath = (Split-Path -Parent $PSScriptRoot),
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RootPath).Path

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
        if ($_.Name -ne 'first-release-acceptance-review.md') {
            $_.FullName.Substring($root.Length + 1).Replace('\', '/')
        }
    })
}

function Get-ForbiddenPatterns {
    $partA = 'pr' + 'obe'
    $partB = 'ph' + 'ase'
    $partC = 'evi' + 'dence'
    $partD = 'orche' + 'stration'
    $partE = 'co' + 'dex'
    $partF = 'Paper' + 'clip'
    $partG = 'fron' + 'tier'
    $partH = 'ag' + 'ent'
    $partI = 'mo' + 'del'
    $partJ = 'INTER' + 'NAL'
    $partK = 'anal' + 'ysis'
    $partL = 'er' + 'ror'
    $partM = 'KE' + 'E-'

    $pattern = '(?i)\b(' + $partA + '|' + $partB + '[ -]?0|' + $partC + '|' + $partD + '|' + $partE + '|' + $partF + '|' + $partG + '|' + $partH + '|' + $partI + '|' + $partJ + '[_ -]?' + $partL + '|' + $partJ + '\s+' + $partK + '\s+' + $partL + '|' + $partM + '\d+)\b'
    return $pattern
}

function Invoke-SurfaceScan {
    param([string] $ScanRoot)

    $patterns = @(Get-ForbiddenPatterns)
    $missing = @($surfaceFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ScanRoot $_)) })
    if ($missing.Count -gt 0) {
        throw "User-facing surface inventory contains missing file(s): $($missing -join ', ')"
    }

    $hits = @(
        foreach ($relativePath in $surfaceFiles) {
            $path = Join-Path $ScanRoot $relativePath
            $content = Get-Content -Raw -LiteralPath $path
            foreach ($pattern in $patterns) {
                if ($content -match $pattern) {
                    [pscustomobject]@{ Path = $relativePath; Pattern = $pattern }
                }
            }
        }
    )

    Write-Output "SURFACE_INVENTORY_COUNT=$($surfaceFiles.Count)"
    $surfaceFiles | ForEach-Object { Write-Output "SURFACE_FILE=$($_)" }
    if ($hits.Count -gt 0) {
        $hits | ForEach-Object { Write-Output "SURFACE_WORDING_HIT path=$($_.Path) pattern=$($_.Pattern)" }
        throw 'Forbidden internal or implementation wording found in the shipped/user-facing surface.'
    }

    Write-Output 'SURFACE_WORDING_SCAN=PASS'
}

if ($SelfTest) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-surface-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $temp | Out-Null
        foreach ($relativePath in $surfaceFiles) {
            $source = Join-Path $root $relativePath
            $target = Join-Path $temp $relativePath
            $parent = Split-Path -Parent $target
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            Copy-Item -LiteralPath $source -Destination $target
        }
        if (Test-Path -LiteralPath $docsPath) {
            Copy-Item -LiteralPath $docsPath -Destination (Join-Path $temp 'docs') -Recurse
        }

        $marker = 'pr' + 'obe'
        [IO.File]::AppendAllText((Join-Path $temp 'README.md'), "`n$marker`n")
        $childOutput = @(& pwsh -NoProfile -File $PSCommandPath -RootPath $temp 2>&1)
        $childExit = $LASTEXITCODE
        if ($childExit -eq 0) {
            throw 'Surface wording gate accepted an injected forbidden term.'
        }
        Write-Output "SURFACE_NON_VACUITY_CHILD_EXIT=$childExit"
        Write-Output 'SURFACE_NON_VACUITY=PASS'
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
    }
}

Invoke-SurfaceScan -ScanRoot $root
