param(
    [string] $RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

function Get-ForbiddenPattern {
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

    return '(?i)\b(' + $partA + '|' + $partB + '[ -]?0|' + $partC + '|' + $partD + '|' + $partE + '|' + $partF + '|' + $partG + '|' + $partH + '|' + $partI + '|' + $partH + '[- ]?id|' + $partM + '\d+|' + $partJ + '[_ -]?' + $partL + '|' + $partJ + '\s+' + $partK + '\s+' + $partL + ')' + '\b'
}

$records = @(git -C $RepositoryRoot log --format='%H%x09%s%x09%b')
if ($LASTEXITCODE -ne 0) { throw 'Unable to read reachable commit messages.' }
$pattern = Get-ForbiddenPattern
$hits = @(
    foreach ($record in $records) {
        if ($record -match $pattern) { $record }
    }
)

Write-Output "REACHABLE_COMMIT_COUNT=$($records.Count)"
if ($hits.Count -gt 0) {
    $hits | ForEach-Object { Write-Output "HISTORY_WORDING_HIT=$_" }
    throw 'Prohibited process wording found in a reachable commit message.'
}

Write-Output 'HISTORY_WORDING_SCAN=PASS'
