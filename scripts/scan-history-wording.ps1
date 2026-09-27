param(
    [string] $RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

function Invoke-GitBytes {
    param(
        [Parameter(Mandatory = $true)] [string] $WorkingDirectory,
        [Parameter(Mandatory = $true)] [string[]] $Arguments
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdout = [IO.MemoryStream]::new()
    try {
        if (-not $process.Start()) { throw 'Could not start git.' }
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        [void] $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw ("git {0} failed with exit code {1}: {2}" -f ($Arguments -join ' '), $process.ExitCode, $stderr.Trim())
        }
        return ,$stdout.ToArray()
    }
    finally {
        $stdout.Dispose()
        $process.Dispose()
    }
}

function ConvertFrom-Utf8Strict {
    param([byte[]] $Bytes)

    try {
        return [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    }
    catch {
        throw 'Git returned a record containing invalid UTF-8.'
    }
}

function Get-NulRecords {
    param([byte[]] $Bytes)

    $records = [Collections.Generic.List[byte[]]]::new()
    $start = 0
    for ($index = 0; $index -lt $Bytes.Length; $index++) {
        if ($Bytes[$index] -ne 0) { continue }
        if ($index -eq $start) { throw 'Git returned an empty history record.' }
        $record = [byte[]]::new($index - $start)
        [Buffer]::BlockCopy($Bytes, $start, $record, 0, $record.Length)
        $records.Add($record)
        $start = $index + 1
    }
    if ($start -ne $Bytes.Length) { throw 'Git returned an unterminated history record.' }
    return ,$records
}

function Get-ByteFieldIndex {
    param(
        [byte[]] $Bytes,
        [int] $StartIndex = 0
    )

    for ($index = $StartIndex; $index -lt $Bytes.Length; $index++) {
        if ($Bytes[$index] -eq 0x1f) { return $index }
    }
    return -1
}

function ConvertTo-ByteSlice {
    param(
        [byte[]] $Bytes,
        [int] $StartIndex,
        [int] $Length
    )

    $slice = [byte[]]::new($Length)
    if ($Length -gt 0) { [Buffer]::BlockCopy($Bytes, $StartIndex, $slice, 0, $Length) }
    return ,$slice
}

function Assert-CommitHash {
    param([string] $Commit)
    if ($Commit -notmatch '^[0-9a-f]{40}$') { throw "Git returned an invalid commit id: $Commit" }
}

function Assert-AllowedMessageBytes {
    param(
        [byte[]] $MessageBytes,
        [string] $Commit
    )

    for ($index = 0; $index -lt $MessageBytes.Length; $index++) {
        $value = $MessageBytes[$index]
        if (($value -lt 0x20 -and $value -notin @(0x09, 0x0a)) -or $value -eq 0x7f) {
            throw "Commit $Commit contains a disallowed control byte at message offset $index."
        }
    }
}

function ConvertFrom-MessageRecord {
    param([byte[]] $Record)

    $separator = Get-ByteFieldIndex -Bytes $Record
    if ($separator -lt 1) { throw 'Git returned a history record without a commit/message separator.' }
    $commit = ConvertFrom-Utf8Strict (ConvertTo-ByteSlice -Bytes $Record -StartIndex 0 -Length $separator)
    Assert-CommitHash -Commit $commit
    $messageBytes = ConvertTo-ByteSlice -Bytes $Record -StartIndex ($separator + 1) -Length ($Record.Length - $separator - 1)
    Assert-AllowedMessageBytes -MessageBytes $messageBytes -Commit $commit
    [pscustomobject]@{
        Commit = $commit
        Message = ConvertFrom-Utf8Strict $messageBytes
    }
}

function ConvertFrom-IdentityRecord {
    param([byte[]] $Record)

    $text = ConvertFrom-Utf8Strict $Record
    $fields = $text.Split([char] 0x1f)
    if ($fields.Count -ne 3) { throw 'Git returned an identity record with an unexpected field count.' }
    Assert-CommitHash -Commit $fields[0]
    [pscustomobject]@{
        Commit = $fields[0]
        Author = $fields[1]
        Committer = $fields[2]
    }
}

function Get-ExpectedCommitCount {
    param([string] $ScanRoot)

    $bytes = Invoke-GitBytes -WorkingDirectory $ScanRoot -Arguments @('rev-list', '--count', 'HEAD')
    $text = (ConvertFrom-Utf8Strict $bytes).Trim()
    [int] $count = 0
    if (-not [int]::TryParse($text, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref] $count) -or $count -le 0) {
        throw 'Unable to determine a nonzero reachable commit count.'
    }
    return $count
}

function Get-HistoryRecords {
    param(
        [string] $ScanRoot,
        [string] $Format,
        [switch] $Messages
    )

    $bytes = Invoke-GitBytes -WorkingDirectory $ScanRoot -Arguments @('log', '--encoding=UTF-8', ('--format=' + $Format), '-z', '--no-decorate', 'HEAD')
    $recordSet = Get-NulRecords -Bytes $bytes
    $records = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $recordSet.Count; $index++) {
        if ($Messages) { $records.Add((ConvertFrom-MessageRecord -Record $recordSet[$index])) }
        else { $records.Add((ConvertFrom-IdentityRecord -Record $recordSet[$index])) }
    }
    if ($Messages) {
        return ,$records.ToArray()
    }
    return ,$records.ToArray()
}

function ConvertTo-ScanText {
    param([string] $Text)

    $normalized = $Text.Normalize([Text.NormalizationForm]::FormKC)
    $normalized = [regex]::Replace($normalized, '[\u00ad\u034f\u061c\u180e\u200b-\u200f\u202a-\u202e\u2060\u2066-\u2069\ufeff]', '')
    $map = @{
        ([char] 0x0430) = 'a'; ([char] 0x0410) = 'A'; ([char] 0x0435) = 'e'; ([char] 0x0415) = 'E'
        ([char] 0x043e) = 'o'; ([char] 0x041e) = 'O'; ([char] 0x0440) = 'p'; ([char] 0x0420) = 'P'
        ([char] 0x0441) = 'c'; ([char] 0x0421) = 'C'; ([char] 0x0445) = 'x'; ([char] 0x0425) = 'X'
        ([char] 0x0456) = 'i'; ([char] 0x0406) = 'I'; ([char] 0x0458) = 'j'; ([char] 0x0408) = 'J'
        ([char] 0x03b1) = 'a'; ([char] 0x0391) = 'A'; ([char] 0x03bf) = 'o'; ([char] 0x039f) = 'O'
        ([char] 0x03c1) = 'p'; ([char] 0x03a1) = 'P'; ([char] 0x03b5) = 'e'; ([char] 0x0395) = 'E'
    }
    $builder = [Text.StringBuilder]::new($normalized.Length)
    foreach ($character in $normalized.ToCharArray()) {
        if ($map.ContainsKey($character)) { [void] $builder.Append($map[$character]) }
        else { [void] $builder.Append($character) }
    }
    return $builder.ToString()
}

function New-WrappedLiteral {
    param([string] $Value)

    $lineWrap = '(?:[\r\n\u2028\u2029][\s\p{Z}]*)?'
    return (($Value.ToCharArray() | ForEach-Object { [regex]::Escape([string] $_) }) -join $lineWrap)
}

function New-SeparatedLiteral {
    param([string] $Value)

    $separator = '[\s\p{Z}\p{Pd}_]*'
    return (($Value.ToCharArray() | ForEach-Object { [regex]::Escape([string] $_) }) -join $separator)
}

function New-WrappedTokenPattern {
    param([string] $Value)
    return '(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value $Value) + '(?![\p{L}\p{N}_])'
}

function Get-ForbiddenPatterns {
    $separator = '[\s\p{Z}\p{Pd}_]*'
    $patterns = [Collections.Generic.List[object]]::new()
    foreach ($value in @('probe', 'evidence', 'orchestration', 'Codex', 'Paperclip', 'frontier', 'agent', 'model', 'prompt')) {
        $patterns.Add([pscustomobject]@{ Name = $value; Pattern = '(?i)' + (New-WrappedTokenPattern -Value $value) })
    }
    $patterns.Add([pscustomobject]@{ Name = 'phase-0'; Pattern = '(?i)(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value 'phase') + '[\s\p{Z}\p{Pd}_]*0(?![\p{L}\p{N}_])' })
    $patterns.Add([pscustomobject]@{ Name = 'internal-error'; Pattern = '(?i)(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value 'INTERNAL') + '[\s\p{Z}\p{Pd}_]*' + (New-WrappedLiteral -Value 'ERROR') + '(?![\p{L}\p{N}_])' })
    $patterns.Add([pscustomobject]@{ Name = 'internal analysis error'; Pattern = '(?i)(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value 'INTERNAL') + '[\s\p{Z}]+analysis[\s\p{Z}]+' + (New-WrappedLiteral -Value 'error') + '(?![\p{L}\p{N}_])' })
    $patterns.Add([pscustomobject]@{ Name = 'issue identifier'; Pattern = '(?i)(?<![\p{L}\p{N}_])K' + (New-WrappedLiteral -Value 'E') + (New-WrappedLiteral -Value 'E') + '[\s\p{Z}\p{Pd}_]*-?[\s\p{Z}]*\d(?:[\s\p{Z}]*\d)*(?![\p{L}\p{N}_])' })
    $attributionForms = @('coauthoredby', 'signedoffby', 'generatedby', 'generatedwith', 'assistedby', 'codevelopedby', 'reviewedby', 'reviewedwith')
    $attribution = '(?i)(?<![\p{L}\p{N}_])(?:' + (($attributionForms | ForEach-Object { New-SeparatedLiteral -Value $_ }) -join '|') + ')(?![\p{L}\p{N}_])'
    $patterns.Add([pscustomobject]@{ Name = 'attribution keyword'; Pattern = $attribution })
    foreach ($label in @('Claude', 'ChatGPT', 'OpenAI', 'Copilot', 'Cursor', 'Cline', 'DeepSeek', 'Gemini', 'GPT')) {
        $patterns.Add([pscustomobject]@{ Name = 'prohibited label: ' + $label; Pattern = '(?i)' + (New-WrappedTokenPattern -Value $label) })
    }
    return $patterns.ToArray()
}

function Get-AllowedIdentityViolations {
    param([object[]] $Records)

    $allowedAuthor = @('KeelMatrix <keelmatrix@gmail.com>')
    $allowedCommitter = @('KeelMatrix <keelmatrix@gmail.com>', 'GitHub <noreply@github.com>')
    foreach ($record in $Records) {
        if ($record.Author -notin $allowedAuthor -or $record.Committer -notin $allowedCommitter) {
            "$($record.Commit)`u{1f}$($record.Author)`u{1f}$($record.Committer)"
        }
    }
}

function Invoke-HistoryScan {
    param([string] $ScanRoot)

    $expectedCount = Get-ExpectedCommitCount -ScanRoot $ScanRoot
    Write-Output "REACHABLE_EXPECTED_COUNT=$expectedCount"
    try {
        $messageRecords = Get-HistoryRecords -ScanRoot $ScanRoot -Format '%H%x1f%B' -Messages
    }
    catch {
        Write-Output 'REACHABLE_COMMIT_COUNT=0'
        Write-Output 'REACHABLE_IDENTITY_COUNT=0'
        throw
    }
    try {
        $identityRecords = Get-HistoryRecords -ScanRoot $ScanRoot -Format '%H%x1f%an <%ae>%x1f%cn <%ce>'
    }
    catch {
        Write-Output "REACHABLE_COMMIT_COUNT=$($messageRecords.Count)"
        Write-Output 'REACHABLE_IDENTITY_COUNT=0'
        throw
    }

    Write-Output "REACHABLE_COMMIT_COUNT=$($messageRecords.Count)"
    Write-Output "REACHABLE_IDENTITY_COUNT=$($identityRecords.Count)"
    if ($messageRecords.Count -ne $expectedCount -or $identityRecords.Count -ne $expectedCount) {
        throw 'Reachable history record accounting is incomplete.'
    }

    $identityHits = @(Get-AllowedIdentityViolations -Records $identityRecords)
    $patterns = @(Get-ForbiddenPatterns)
    $hits = @(
        foreach ($record in $messageRecords) {
            $message = ConvertTo-ScanText -Text $record.Message
            foreach ($pattern in $patterns) {
                if ($message -match $pattern.Pattern) {
                    [pscustomobject]@{ Commit = $record.Commit; Name = $pattern.Name; Message = $record.Message.TrimEnd() }
                }
            }
        }
    )

    if ($identityHits.Count -gt 0) {
        $identityHits | ForEach-Object { Write-Output "HISTORY_IDENTITY_HIT=$_" }
        throw 'Disallowed commit author or committer identity found in reachable history.'
    }
    if ($hits.Count -gt 0) {
        $hits | ForEach-Object { Write-Output "HISTORY_WORDING_HIT=commit=$($_.Commit) pattern=$($_.Name) message=$($_.Message)" }
        throw 'Prohibited process wording or attribution found in a reachable commit message.'
    }

    Write-Output 'HISTORY_WORDING_SCAN=PASS'
}

function New-SelfTestRepository {
    param(
        [string] $Path,
        [string] $UserName = 'KeelMatrix',
        [string] $UserEmail = 'keelmatrix@gmail.com'
    )

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    & git -C $Path init --quiet
    & git -C $Path config user.name $UserName
    & git -C $Path config user.email $UserEmail
    [IO.File]::WriteAllText((Join-Path $Path 'history.txt'), 'data')
    & git -C $Path add history.txt
}

function Invoke-ChildHistoryScan {
    param([string] $Path)
    $output = @(& pwsh -NoProfile -File $PSCommandPath -RepositoryRoot $Path 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
    }
}

function Assert-HistoryChildReject {
    param(
        [string] $Name,
        [object] $Result,
        [bool] $RequireCompleteAccounting
    )
    if ($Result.ExitCode -eq 0 -or $Result.Text -notmatch 'REACHABLE_EXPECTED_COUNT=1') {
        throw "History wording self-test accepted or did not account for fixture $Name."
    }
    if ($RequireCompleteAccounting -and ($Result.Text -notmatch 'REACHABLE_COMMIT_COUNT=1' -or $Result.Text -notmatch 'REACHABLE_IDENTITY_COUNT=1')) {
        throw "History wording self-test was vacuous for fixture $Name."
    }
    Write-Output "HISTORY_WORDING_FIXTURE=$Name EXPECTED=REJECT exit=$($Result.ExitCode) accounting=$($RequireCompleteAccounting)"
}

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-history-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-SelfTestRepository -Path $selfTestRoot
        $env:GIT_COMMITTER_NAME = 'GitHub'
        $env:GIT_COMMITTER_EMAIL = 'noreply@github.com'
        & git -C $selfTestRoot commit --quiet -m 'Initial history'
        Remove-Item Env:GIT_COMMITTER_NAME
        Remove-Item Env:GIT_COMMITTER_EMAIL

        $permitted = Invoke-ChildHistoryScan -Path $selfTestRoot
        if ($permitted.ExitCode -ne 0 -or $permitted.Text -notmatch 'HISTORY_WORDING_SCAN=PASS' -or $permitted.Text -notmatch 'REACHABLE_EXPECTED_COUNT=1' -or $permitted.Text -notmatch 'REACHABLE_COMMIT_COUNT=1' -or $permitted.Text -notmatch 'REACHABLE_IDENTITY_COUNT=1') {
            throw 'History wording self-test rejected a permitted GitHub committer or failed complete-record accounting.'
        }
        Write-Output 'HISTORY_WORDING_GITHUB_COMMITTER_SELF_TEST=PASS reachable=1 records=complete'

        $framingCases = @(
            @{ Name = 'nul-like-content'; Bytes = [byte[]](0x41, 0x00, 0x42, 0x00) },
            @{ Name = 'truncated-record'; Bytes = [byte[]](0x41) },
            @{ Name = 'repeated-delimiter'; Bytes = [byte[]](0x41, 0x00, 0x00) }
        )
        foreach ($case in $framingCases) {
            $rejected = $false
            try {
                $parsed = Get-NulRecords -Bytes $case.Bytes
                if ($parsed.Count -ne 1) { throw 'framing did not produce exactly one complete record' }
            }
            catch { $rejected = $true }
            if (-not $rejected) { throw "History framing self-test accepted fixture $($case.Name)." }
            Write-Output "HISTORY_FRAMING_FIXTURE=$($case.Name) EXPECTED=REJECT"
        }

        $messageCases = @(
            @{ Name = 'record-separator'; Message = "clean$([char] 0x1e)Paperclip hidden" },
            @{ Name = 'field-separator'; Message = "clean$([char] 0x1f)Paperclip hidden" },
            @{ Name = 'repeated-separators'; Message = "clean$([char] 0x1e)$([char] 0x1e)Paperclip hidden" },
            @{ Name = 'line-wrapped-attribution'; Message = "Co-Authored-`nBy: Vendor <vendor@example.com>" },
            @{ Name = 'internal-split-co-prefix'; Message = "C`no-Authored-By: Vendor <vendor@example.com>" },
            @{ Name = 'internal-split-authored-suffix'; Message = "Co-Authored-B`r`ny: Vendor <vendor@example.com>" },
            @{ Name = 'internal-split-generated-suffix'; Message = "Generated-b`u{2028}y: Vendor <vendor@example.com>" },
            @{ Name = 'spaced-attribution'; Message = 'Co - Authored - By: Vendor <vendor@example.com>' },
            @{ Name = 'underscored-attribution'; Message = 'Co_Authored_By: Vendor <vendor@example.com>' },
            @{ Name = 'signed-off-by'; Message = "clean`nSigned-off-by: Vendor <vendor@example.com>" },
            @{ Name = 'generated-by'; Message = 'Generated-by: Vendor' },
            @{ Name = 'generated-with'; Message = 'Generated with Claude Opus' },
            @{ Name = 'assisted-by'; Message = 'Assisted-by: Vendor' },
            @{ Name = 'co-developed-by'; Message = 'Co-developed-by: Vendor' },
            @{ Name = 'reviewed-by'; Message = 'Reviewed-by: Claude Opus' },
            @{ Name = 'reviewed-with'; Message = 'Reviewed with Claude Opus' }
        )
        foreach ($case in $messageCases) {
            $caseRoot = Join-Path $selfTestRoot $case.Name
            New-SelfTestRepository -Path $caseRoot
            if ($case.Name -in @('signed-off-by')) {
                & git -C $caseRoot commit --quiet -m clean -m $case.Message
            }
            else {
                & git -C $caseRoot commit --quiet -m $case.Message
            }
            $result = Invoke-ChildHistoryScan -Path $caseRoot
            Assert-HistoryChildReject -Name $case.Name -Result $result -RequireCompleteAccounting ($case.Name -notin @('record-separator', 'field-separator', 'repeated-separators'))
        }

        $spoofRoot = Join-Path $selfTestRoot 'dependabot-spoof'
        New-SelfTestRepository -Path $spoofRoot -UserName 'Dependabot' -UserEmail 'attacker@example.com'
        & git -C $spoofRoot commit --quiet -m clean
        $spoof = Invoke-ChildHistoryScan -Path $spoofRoot
        if ($spoof.Text -notmatch 'HISTORY_IDENTITY_HIT') { throw 'History wording self-test accepted a spoofed bot identity.' }
        Assert-HistoryChildReject -Name 'dependabot-spoof' -Result $spoof -RequireCompleteAccounting $true
        Write-Output 'HISTORY_WORDING_REGRESSION_FIXTURES=PASS'
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
