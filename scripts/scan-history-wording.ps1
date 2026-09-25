param(
    [string] $RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SelfTest
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

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-history-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $selfTestRoot | Out-Null
        & git -C $selfTestRoot init --quiet
        & git -C $selfTestRoot config user.name 'KeelMatrix'
        & git -C $selfTestRoot config user.email 'keelmatrix@gmail.com'
        [IO.File]::WriteAllText((Join-Path $selfTestRoot 'history.txt'), 'old')
        & git -C $selfTestRoot add history.txt
        & git -C $selfTestRoot commit --quiet -m (('pr' + 'obe') + ' historical wording')
        [IO.File]::WriteAllText((Join-Path $selfTestRoot 'history.txt'), 'new')
        & git -C $selfTestRoot add history.txt
        & git -C $selfTestRoot commit --quiet -m 'Keep history clean'

        $selfTestOutput = @(& pwsh -NoProfile -File $PSCommandPath -RepositoryRoot $selfTestRoot 2>&1)
        $selfTestExit = $LASTEXITCODE
        if ($selfTestExit -eq 0 -or ($selfTestOutput -join "`n") -notmatch 'HISTORY_WORDING_HIT=') {
            throw 'History wording self-test failed to detect a forbidden earlier commit.'
        }

        Write-Output "HISTORY_WORDING_SELF_TEST=PASS child_exit=$selfTestExit"
    }
    finally {
        if (Test-Path -LiteralPath $selfTestRoot) { Remove-Item -LiteralPath $selfTestRoot -Recurse -Force }
    }
}

$shallowState = @(git -C $RepositoryRoot rev-parse --is-shallow-repository 2>$null)
if ($LASTEXITCODE -ne 0 -or $shallowState.Count -eq 0) { throw 'Unable to determine whether repository history is complete.' }
if ($shallowState[0].Trim() -eq 'true') { throw 'Reachable history wording scan requires a non-shallow repository.' }

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
