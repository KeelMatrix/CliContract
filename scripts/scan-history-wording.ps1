param(
    [string] $RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

function Get-ForbiddenPatterns {
    @(
        [pscustomobject]@{ Name = 'probe'; Pattern = '(?i)\bprobe\b' }
        [pscustomobject]@{ Name = 'phase-0'; Pattern = '(?i)\bphase[ -]?0\b' }
        [pscustomobject]@{ Name = 'evidence'; Pattern = '(?i)\bevidence\b' }
        [pscustomobject]@{ Name = 'orchestration'; Pattern = '(?i)\borchestration\b' }
        [pscustomobject]@{ Name = 'Codex'; Pattern = '(?i)\bCodex\b' }
        [pscustomobject]@{ Name = 'Paperclip'; Pattern = '(?i)\bPaperclip\b' }
        [pscustomobject]@{ Name = 'frontier'; Pattern = '(?i)\bfrontier\b' }
        [pscustomobject]@{ Name = 'agent'; Pattern = '(?i)\bagent\b' }
        [pscustomobject]@{ Name = 'model'; Pattern = '(?i)\bmodel\b' }
        [pscustomobject]@{ Name = 'prompt'; Pattern = '(?i)\bprompt\b' }
        [pscustomobject]@{ Name = 'agent-id'; Pattern = '(?i)\bagent[- ]?id\b' }
        [pscustomobject]@{ Name = 'internal-error'; Pattern = '(?i)\bINTERNAL[- ]?ERROR\b' }
        [pscustomobject]@{ Name = 'internal analysis error'; Pattern = '(?i)\bINTERNAL\s+analysis\s+error\b' }
        [pscustomobject]@{ Name = 'issue identifier'; Pattern = '(?i)\bKEE-\d+\b' }
        [pscustomobject]@{ Name = 'co-author attribution trailer'; Pattern = '(?im)\bCo-Authored-By\s*:' }
        [pscustomobject]@{ Name = 'Generated with attribution'; Pattern = '(?i)\bGenerated\s+(?:with|by)\b' }
        [pscustomobject]@{ Name = 'Assisted by attribution'; Pattern = '(?i)\bAssisted\s+by\b' }
    )
}

function Get-AllowedIdentityViolations {
    param([string[]] $Records)

    $allowedAuthor = '^(?:KeelMatrix <keelmatrix@gmail\.com>|Dependabot(?:\[bot\])? <[^>]+>)$'
    $allowedCommitter = '^(?:KeelMatrix <keelmatrix@gmail\.com>|Dependabot(?:\[bot\])? <[^>]+>|GitHub <noreply@github\.com>)$'
    foreach ($record in $Records) {
        $fields = $record -split [char]0x1f
        if ($fields.Count -ne 3 -or $fields[1] -notmatch $allowedAuthor -or $fields[2] -notmatch $allowedCommitter) {
            $record
        }
    }
}

function Invoke-HistoryScan {
    param([string] $ScanRoot)

    $recordSeparator = [char]0x1e
    $fieldSeparator = [char]0x1f
    $messageOutput = (git -C $ScanRoot log --format='%H%x1f%B%x1e' | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read reachable commit messages.' }
    $messageRecords = @($messageOutput -split $recordSeparator | ForEach-Object { $_.Trim([char[]]@("`r", "`n")) } | Where-Object { $_ -ne '' })

    $identityOutput = (git -C $ScanRoot log --format='%H%x1f%an <%ae>%x1f%cn <%ce>%x1e' | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read reachable commit identities.' }
    $identityRecords = @($identityOutput -split $recordSeparator | ForEach-Object { $_.Trim([char[]]@("`r", "`n")) } | Where-Object { $_ -ne '' })
    $identityHits = @(Get-AllowedIdentityViolations -Records $identityRecords)
    $patterns = @(Get-ForbiddenPatterns)
    $hits = @(
        foreach ($record in $messageRecords) {
            $fields = $record -split $fieldSeparator, 2
            $commit = $fields[0]
            $message = if ($fields.Count -eq 2) { $fields[1] } else { '' }
            foreach ($pattern in $patterns) {
                if ($message -match $pattern.Pattern) {
                    [pscustomobject]@{ Commit = $commit; Name = $pattern.Name; Message = $message.TrimEnd() }
                }
            }
        }
    )

    Write-Output "REACHABLE_COMMIT_COUNT=$($messageRecords.Count)"
    Write-Output "REACHABLE_IDENTITY_COUNT=$($identityRecords.Count)"
    if ($identityHits.Count -gt 0) {
        $identityHits | ForEach-Object { Write-Output "HISTORY_IDENTITY_HIT=$_" }
        throw 'Disallowed commit author or committer identity found in reachable history.'
    }
    if ($hits.Count -gt 0) {
        $hits | ForEach-Object { Write-Output "HISTORY_WORDING_HIT=commit=$($_.Commit) pattern=$($_.Name) message=$($_.Message)" }
        throw 'Prohibited process wording found in a reachable commit message.'
    }

    Write-Output 'HISTORY_WORDING_SCAN=PASS'
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
        $env:GIT_COMMITTER_NAME = 'GitHub'
        $env:GIT_COMMITTER_EMAIL = 'noreply@github.com'
        & git -C $selfTestRoot commit --quiet -m 'Initial history'
        Remove-Item Env:GIT_COMMITTER_NAME
        Remove-Item Env:GIT_COMMITTER_EMAIL

        $permittedOutput = @(& pwsh -NoProfile -File $PSCommandPath -RepositoryRoot $selfTestRoot 2>&1)
        $permittedExit = $LASTEXITCODE
        $permittedText = ($permittedOutput | ForEach-Object { $_.ToString() }) -join "`n"
        if ($permittedExit -ne 0 -or $permittedText -notmatch 'HISTORY_WORDING_SCAN=PASS') {
            throw 'History wording self-test rejected a permitted GitHub committer.'
        }
        Write-Output 'HISTORY_WORDING_GITHUB_COMMITTER_SELF_TEST=PASS'

        [IO.File]::WriteAllText((Join-Path $selfTestRoot 'history.txt'), 'new')
        & git -C $selfTestRoot add history.txt
        & git -C $selfTestRoot commit --quiet -m 'Keep history clean' -m 'Co-Authored-By: Paperclip <noreply@paperclip.ing>'

        $trailerOutput = @(& pwsh -NoProfile -File $PSCommandPath -RepositoryRoot $selfTestRoot 2>&1)
        $trailerExit = $LASTEXITCODE
        $trailerText = ($trailerOutput | ForEach-Object { $_.ToString() }) -join "`n"
        if ($trailerExit -eq 0 -or $trailerText -notmatch 'HISTORY_WORDING_HIT' -or $trailerText -notmatch 'Co-Authored-By') {
            throw 'History wording self-test failed to detect a prohibited authorship trailer.'
        }

        Write-Output "HISTORY_WORDING_TRAILER_SELF_TEST=PASS child_exit=$trailerExit"
    }
    finally {
        Remove-Item Env:GIT_COMMITTER_NAME -ErrorAction SilentlyContinue
        Remove-Item Env:GIT_COMMITTER_EMAIL -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $selfTestRoot) { Remove-Item -LiteralPath $selfTestRoot -Recurse -Force }
    }
}

$shallowState = @(git -C $RepositoryRoot rev-parse --is-shallow-repository 2>$null)
if ($LASTEXITCODE -ne 0 -or $shallowState.Count -eq 0) { throw 'Unable to determine whether repository history is complete.' }
if ($shallowState[0].Trim() -eq 'true') { throw 'Reachable history wording scan requires a non-shallow repository.' }

Invoke-HistoryScan -ScanRoot $RepositoryRoot
