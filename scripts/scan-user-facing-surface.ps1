param(
    [string] $RootPath = (Split-Path -Parent $PSScriptRoot),
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RootPath).Path

$nonTextExceptions = [ordered]@{
    'icon.png' = 'binary PNG asset; textual wording does not apply'
    'scripts/scan-history-wording.ps1' = 'commit-message guard contains match literals required to detect disallowed metadata and wording'
}

function Get-TrackedTextFiles {
    param([string] $ScanRoot)

    $gitOutput = @(git -C $ScanRoot ls-files --full-name 2>&1)
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0) {
        $detail = ($gitOutput -join ' ').Trim()
        throw "Could not enumerate tracked files with git ls-files: $detail"
    }

    $trackedFiles = @($gitOutput | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($trackedFiles.Count -eq 0) { throw 'The repository has no tracked files to scan.' }

    foreach ($relativePath in $trackedFiles) {
        if ($nonTextExceptions.Contains($relativePath)) {
            continue
        }

        $path = Join-Path $ScanRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Tracked file is missing from the checkout: $relativePath"
        }

        $relativePath
    }
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
    $partN = 'fou' + 'nder'
    $partO = 'acce' + 'ptance'
    $partP = 'ta' + 'sk'
    $partQ = 'orche' + 'stration'

    $pattern = '(?i)\b(' + $partA + '|' + $partB + '[ -]?0|' + $partC + '|' + $partD + '|' + $partE + '|' + $partF + '|' + $partG + '|' + $partH + '|' + $partI + '|' + $partN + '|' + $partO + '|' + $partP + '|' + $partQ + '|' + $partJ + '[_ -]?' + $partL + '|' + $partJ + '\s+' + $partK + '\s+' + $partL + '|' + $partM + '\d+)\b'
    return $pattern
}

function Invoke-SurfaceScan {
    param([string] $ScanRoot)

    $surfaceFiles = @(Get-TrackedTextFiles -ScanRoot $ScanRoot)
    $patterns = @(Get-ForbiddenPatterns)

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
    foreach ($exception in $nonTextExceptions.GetEnumerator()) {
        Write-Output "SURFACE_EXCEPTION=$($exception.Key) reason=$($exception.Value)"
    }
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
        $sourceFiles = @(Get-TrackedTextFiles -ScanRoot $root)
        foreach ($relativePath in $sourceFiles) {
            $source = Join-Path $root $relativePath
            $target = Join-Path $temp $relativePath
            $parent = Split-Path -Parent $target
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            Copy-Item -LiteralPath $source -Destination $target
        }
        & git -C $temp init --quiet
        & git -C $temp config user.name 'KeelMatrix'
        & git -C $temp config user.email 'keelmatrix@gmail.com'
        & git -C $temp add --all
        if ($LASTEXITCODE -ne 0) { throw 'Surface scan self-test could not create the tracked-file fixture.' }

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
