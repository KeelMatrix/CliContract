$ErrorActionPreference = 'Stop'

function Get-AcceptanceCriterionHash([string] $criterion) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($criterion))).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Escape-AcceptanceCell([string] $value) {
    return $value.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ').Trim()
}

function Find-AcceptanceGitHubRunIds([string] $proof) {
    $pattern = '(?ix)(?:github\s+actions?\s+run(?:\s+id)?|github\s+run(?:\s+id)?|actions?\s+run(?:\s+id)?|ci\s+run(?:\s+id)?|run_id)\s*[:=#]?\s*(?<id>\d{6,})\b|gh\s+run\s+view\s+(?<ghid>\d{6,})\b'
    $ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($match in [regex]::Matches($proof, $pattern)) {
        $id = if ($match.Groups['id'].Success) { $match.Groups['id'].Value } else { $match.Groups['ghid'].Value }
        if ($id) { [void]$ids.Add($id) }
    }
    return @($ids | Sort-Object { [long]$_ })
}

function Remove-AcceptanceGeneratedRunMetadata([string] $proof) {
    return [regex]::Replace($proof, '(?i)\bgithub_run_(?:id|head_sha|status|conclusion|event)=[^;|]+;?\s*', '').Trim()
}

function Get-AcceptanceEvidenceKind([string] $proof, [int] $runCount) {
    if ($runCount -gt 0 -or $proof -match '(?is)(?:^|[;\s]|proof=)command=[^;|]+;\s*output=[^;|]+' -or $proof -match '(?is)(?:^|[;\s]|proof=)checker=[^;|]+;\s*checker_output=[^;|]+') { return 'reproducible' }
    if ($proof -match '(?is)(?:^|[;\s]|proof=)judgement=[^;|]+') { return 'judgement' }
    return 'reproducible'
}

function Get-AcceptanceGitHubApplicationPath {
    try {
        $application = @(Get-Command -Name gh -CommandType Application -ErrorAction Stop | Select-Object -First 1)
    }
    catch {
        throw "Unable to resolve the GitHub CLI application: $($_.Exception.Message)"
    }
    if ($application.Count -ne 1) { throw 'Unable to resolve exactly one GitHub CLI application.' }
    $path = [string]$application[0].Path
    if ([string]::IsNullOrWhiteSpace($path)) { $path = [string]$application[0].Source }
    if ([string]::IsNullOrWhiteSpace($path)) { throw 'The GitHub CLI application did not expose a full path.' }
    return [IO.Path]::GetFullPath($path)
}

function Get-AcceptanceGitHubRunMetadata([string] $runId, [string] $repository, [scriptblock] $RunMetadataResolver) {
    if ($null -ne $RunMetadataResolver) { return & $RunMetadataResolver $runId }
    $ghPath = Get-AcceptanceGitHubApplicationPath
    try {
        $raw = @(& $ghPath run view $runId --repo $repository --json headSha,status,conclusion,event 2>&1)
        $exitCode = $LASTEXITCODE
    }
    catch { throw "Unable to validate GitHub Actions run $runId with gh run view: $($_.Exception.Message)" }
    if ($exitCode -ne 0) { throw "Unable to validate GitHub Actions run $runId with gh run view. Output: $($raw -join ' ')" }
    try { return (($raw | ForEach-Object { $_.ToString() }) -join "`n" | ConvertFrom-Json) }
    catch { throw "GitHub Actions run $runId returned invalid JSON metadata: $($_.Exception.Message)" }
}

function Get-AcceptanceRunField([object] $metadata, [string] $name, [string] $runId) {
    $value = $metadata.$name
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) { throw "GitHub Actions run $runId metadata did not contain $name." }
    return ([string]$value).Trim()
}

function Invoke-AcceptanceMapGenerationCore {
    param(
        [Parameter(Mandatory = $true)] [string] $ChecklistPath,
        [Parameter(Mandatory = $true)] [string] $EvidencePath,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $CandidateSha,
        [Parameter(Mandatory = $true)] [string] $OutputPath,
        [string] $Repository = 'KeelMatrix/CliContract',
        [scriptblock] $RunMetadataResolver
    )

    $checklistRows = @([regex]::Matches((Get-Content -Raw -LiteralPath $ChecklistPath), '(?m)^\s*\* \[ \] (.+)$') | ForEach-Object { $_.Groups[1].Value.Trim() })
    if ($checklistRows.Count -eq 0) { throw 'The acceptance checklist did not contain checkbox criteria.' }

    $evidenceEntries = @(Get-Content -Raw -LiteralPath $EvidencePath | ConvertFrom-Json)
    $evidenceByCriterion = @{}
    foreach ($entry in $evidenceEntries) {
        if (($null -eq $entry.criterion -and $null -eq $entry.criterion_number) -or $null -eq $entry.status -or $null -eq $entry.evidence) { throw 'Each evidence entry requires criterion or criterion_number, status, and evidence properties.' }
        if ($null -ne $entry.criterion_number) {
            $criterionNumber = 0
            if (-not [int]::TryParse([string]$entry.criterion_number, [ref]$criterionNumber) -or $criterionNumber -lt 1 -or $criterionNumber -gt $checklistRows.Count) {
                throw "Evidence criterion_number is outside the checklist: $($entry.criterion_number)"
            }
            $criterion = $checklistRows[$criterionNumber - 1]
        }
        else {
            $criterion = [string]$entry.criterion
        }
        if ($evidenceByCriterion.ContainsKey($criterion)) { throw "Duplicate evidence entry for criterion: $criterion" }
        $evidenceByCriterion[$criterion] = $entry
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('# First-release acceptance map')
    $lines.Add('')
    $lines.Add('Generated from the current read-only acceptance checklist by `scripts/generate-acceptance-map.ps1`; candidate-bound runs, commands, and checkers are mechanically revalidated, while `judgement` rows carry criterion-specific artifact declarations and remain reviewer attestation; row numbers and criterion text are not hand-maintained.')
    $lines.Add('')
    $lines.Add('| # | Criterion | Status | Candidate-SHA evidence or disposition |')
    $lines.Add('|---:|---|:---:|---|')

    for ($index = 0; $index -lt $checklistRows.Count; $index++) {
        $criterion = $checklistRows[$index]
        if (-not $evidenceByCriterion.ContainsKey($criterion)) { throw "Evidence is missing for checklist criterion $($index + 1)." }
        $entry = $evidenceByCriterion[$criterion]
        $status = [string]$entry.status
        if ($status -notin @('MET', 'UNMET', 'N/A')) { throw "Unsupported acceptance status '$status' for criterion $($index + 1)." }
        $hash = Get-AcceptanceCriterionHash $criterion
        $detail = ([string]$entry.evidence).Trim()
        if ([string]::IsNullOrWhiteSpace($detail)) { throw "Evidence is empty for checklist criterion $($index + 1)." }
        $runIds = @(Find-AcceptanceGitHubRunIds $detail)
        $detail = Remove-AcceptanceGeneratedRunMetadata $detail
        foreach ($runId in $runIds) {
            $metadata = Get-AcceptanceGitHubRunMetadata $runId $Repository $RunMetadataResolver
            $headSha = Get-AcceptanceRunField $metadata 'headSha' $runId
            if ($headSha -notmatch '^[0-9a-fA-F]{40}$') { throw "GitHub Actions run $runId returned an invalid headSha: $headSha" }
            if ($headSha -ine $CandidateSha) { throw "GitHub Actions run $runId is not candidate-bound: headSha=$headSha candidate_sha=$($CandidateSha.ToLowerInvariant())" }
            $runStatus = Get-AcceptanceRunField $metadata 'status' $runId
            $conclusion = Get-AcceptanceRunField $metadata 'conclusion' $runId
            $event = Get-AcceptanceRunField $metadata 'event' $runId
            $detail = "$detail; github_run_id=$runId; github_run_head_sha=$($headSha.ToLowerInvariant()); github_run_status=$runStatus; github_run_conclusion=$conclusion; github_run_event=$event"
        }
        if ($status -eq 'MET') {
            $kind = Get-AcceptanceEvidenceKind $detail $runIds.Count
            $evidence = "candidate_sha=$($CandidateSha.ToLowerInvariant()); proof_kind=$kind; criterion_sha256=$hash; proof=$(Escape-AcceptanceCell $detail)"
        }
        elseif ($status -eq 'UNMET') { $evidence = "criterion_sha256=$hash; UNMET: $(Escape-AcceptanceCell $detail)" }
        else { $evidence = "criterion_sha256=$hash; N/A $(Escape-AcceptanceCell $detail)" }
        $lines.Add(("| {0} | {1} | {2} | {3} |" -f ($index + 1), (Escape-AcceptanceCell $criterion), $status, $evidence))
    }

    $unknownEvidence = @($evidenceByCriterion.Keys | Where-Object { $_ -notin $checklistRows })
    if ($unknownEvidence.Count -gt 0) { throw "Evidence contains criteria not present in the current checklist: $($unknownEvidence -join ' | ')" }
    $fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $fullOutputPath
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($fullOutputPath, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output "ACCEPTANCE_MAP_GENERATED=PASS checklist_rows=$($checklistRows.Count) output=$fullOutputPath candidate_sha=$($CandidateSha.ToLowerInvariant())"
}

function Get-AcceptanceTokenValues([string] $proof, [string] $tokenName) {
    $escapedName = [regex]::Escape($tokenName)
    return @([regex]::Matches($proof, "(?i)(?:^|[;\s])$escapedName=(?<value>[^;|]+)") | ForEach-Object { $_.Groups['value'].Value.Trim() })
}

function Test-AcceptanceRepositoryPath([string] $candidatePath, [string] $RepositoryRoot) {
    $candidatePath = $candidatePath.Trim().Trim('`', '"', "'")
    if ([string]::IsNullOrWhiteSpace($candidatePath) -or $candidatePath.Contains('*') -or [IO.Path]::IsPathRooted($candidatePath)) { return $false }
    try {
        $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $full = [IO.Path]::GetFullPath((Join-Path $root $candidatePath))
        $relative = [IO.Path]::GetRelativePath($root, $full)
        if ($relative -eq '..' -or $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)") -or [IO.Path]::IsPathRooted($relative)) { return $false }
        return Test-Path -LiteralPath $full -PathType Leaf
    }
    catch { return $false }
}

function Get-AcceptanceRepositoryFilePath([string] $candidatePath, [string] $RepositoryRoot) {
    $candidatePath = $candidatePath.Trim().Trim('`', '"', "'")
    if (-not (Test-AcceptanceRepositoryPath $candidatePath $RepositoryRoot)) { return $null }
    return [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetFullPath($RepositoryRoot)) $candidatePath))
}

function Test-AcceptancePlaceholder([string] $value) {
    $normalized = ([string]$value).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($normalized)) { return $true }
    if ($normalized -match '^(?:generic evidence|placeholder|arbitrary(?: proof)?|tbd|todo|fixme|lorem ipsum|proof goes here|n/?a)$') { return $true }
    if ($normalized -match '^(?:candidate_sha=)?[0-9a-f]{40}$') { return $true }
    return $false
}

function Get-AcceptanceRunEvidenceRecords([string] $proof) {
    $pattern = '(?is)(?:^|[;\s])github_run_id=(?<id>\d{6,});\s*github_run_head_sha=(?<head>[^;|]+);\s*github_run_status=(?<status>[^;|]+);\s*github_run_conclusion=(?<conclusion>[^;|]+);\s*github_run_event=(?<event>[^;|]+)'
    return @([regex]::Matches($proof, $pattern) | ForEach-Object { [pscustomobject]@{ RunId = $_.Groups['id'].Value.Trim(); HeadSha = $_.Groups['head'].Value.Trim(); Status = $_.Groups['status'].Value.Trim(); Conclusion = $_.Groups['conclusion'].Value.Trim(); Event = $_.Groups['event'].Value.Trim() } })
}

function Test-AcceptanceRunEvidence([string] $proof, [string] $candidateSha, [int] $rowNumber, [string] $repository, [scriptblock] $RunMetadataResolver) {
    $runIds = @(Find-AcceptanceGitHubRunIds $proof)
    if ($runIds.Count -eq 0) { return [pscustomobject]@{ Valid = $true; Reason = $null } }
    $records = @(Get-AcceptanceRunEvidenceRecords $proof)
    if ($records.Count -ne $runIds.Count) { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber names GitHub Actions run(s) without complete generator-derived metadata" } }
    foreach ($runId in $runIds) {
        $matchingRecords = @($records | Where-Object RunId -eq $runId)
        if ($matchingRecords.Count -ne 1) { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber does not contain exactly one metadata record for run $runId" } }
        try {
            $actual = Get-AcceptanceGitHubRunMetadata $runId $repository $RunMetadataResolver
            $actualHead = Get-AcceptanceRunField $actual 'headSha' $runId
            $actualStatus = Get-AcceptanceRunField $actual 'status' $runId
            $actualConclusion = Get-AcceptanceRunField $actual 'conclusion' $runId
            $actualEvent = Get-AcceptanceRunField $actual 'event' $runId
        }
        catch { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber could not independently validate run ${runId}: $($_.Exception.Message)" } }
        if ($actualHead -notmatch '^[0-9a-fA-F]{40}$' -or $actualHead -ine $candidateSha) { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber names GitHub Actions run $runId whose resolved head SHA is not the candidate" } }
        $record = $matchingRecords[0]
        if ($record.HeadSha -notmatch '^[0-9a-fA-F]{40}$' -or $record.HeadSha -ine $actualHead) { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber contains forged or stale head SHA metadata for run $runId" } }
        if ($record.Status -ine $actualStatus -or $record.Conclusion -ine $actualConclusion -or $record.Event -ine $actualEvent) { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber contains GitHub Actions metadata that does not match run $runId" } }
        if ($actualStatus -ine 'completed' -or $actualConclusion -ine 'success') { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber names a GitHub Actions run that is not completed/success" } }
    }
    return [pscustomobject]@{ Valid = $true; Reason = $null }
}

function Test-AcceptanceCommandAnchor([string] $proof) {
    return @([regex]::Matches($proof, '(?is)(?:^|[;\s]|proof=)command=(?<command>[^;|]+);\s*output=(?<output>[^;|]+)') | Where-Object { -not (Test-AcceptancePlaceholder $_.Groups['command'].Value) -and -not (Test-AcceptancePlaceholder $_.Groups['output'].Value) }).Count -gt 0
}

function Test-AcceptanceJudgementAnchor([string] $proof, [string] $RepositoryRoot) {
    $judgements = @([regex]::Matches($proof, '(?is)(?:^|[;\s]|proof=)judgement=(?<value>[^;|]+)'))
    foreach ($judgement in $judgements) {
        $value = $judgement.Groups['value'].Value.Trim()
        $artifactMatch = [regex]::Match($value, '(?i)(?:^|,)\s*artifacts?=(?<paths>[^,]+(?:,[^,]+)*)$')
        if (-not $artifactMatch.Success) { continue }
        $paths = @($artifactMatch.Groups['paths'].Value.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($paths.Count -eq 0 -or @($paths | Where-Object { -not (Test-AcceptanceRepositoryPath $_ $RepositoryRoot) }).Count -gt 0) { continue }
        $rationales = @(Get-AcceptanceTokenValues $proof 'rationale' | Where-Object { -not (Test-AcceptancePlaceholder $_) })
        if ($rationales.Count -gt 0 -and -not (Test-AcceptancePlaceholder $value)) { return $true }
    }
    return $false
}

function Get-AcceptanceCheckerMatches([string] $proof) { return @([regex]::Matches($proof, '(?is)(?:^|[;\s]|proof=)checker=(?<command>[^;|]+);\s*checker_output=(?<output>[^;|]+)')) }
function Get-AcceptanceCheckerCommand([string] $command) { return [regex]::Match($command.Trim(), '^(?<path>\S+\.ps1)$', [Text.RegularExpressions.RegexOptions]::IgnoreCase) }

function Test-AcceptanceCheckerEvidence([string] $proof, [int] $rowNumber, [string] $RepositoryRoot) {
    $matches = @(Get-AcceptanceCheckerMatches $proof)
    if ($matches.Count -eq 0) { return [pscustomobject]@{ Valid = $true; Reason = $null } }
    foreach ($match in $matches) {
        $command = $match.Groups['command'].Value.Trim(); $expectedOutput = $match.Groups['output'].Value.Trim()
        if (Test-AcceptancePlaceholder $command -or Test-AcceptancePlaceholder $expectedOutput) { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber contains a placeholder repository checker invocation" } }
        $commandMatch = Get-AcceptanceCheckerCommand $command
        if (-not $commandMatch.Success) { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber repository checker must name one repository-owned .ps1 path" } }
        $checkerPath = $commandMatch.Groups['path'].Value.Replace('\', '/'); if ($checkerPath.StartsWith('./', [StringComparison]::Ordinal)) { $checkerPath = $checkerPath.Substring(2) }
        if (-not $checkerPath.StartsWith('scripts/', [StringComparison]::OrdinalIgnoreCase)) { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber repository checker must be under scripts/" } }
        $fullPath = Get-AcceptanceRepositoryFilePath $checkerPath $RepositoryRoot
        if ($null -eq $fullPath) { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber repository checker path does not exist: $checkerPath" } }
        try { $actualOutput = @(& $fullPath 2>&1); $actualExit = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE } }
        catch { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber repository checker could not be rerun: $($_.Exception.Message)" } }
        $actualText = ($actualOutput | ForEach-Object { $_.ToString() }) -join "`n"
        if ($actualExit -ne 0 -or $actualText.IndexOf($expectedOutput, [StringComparison]::Ordinal) -lt 0) { return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber repository checker result did not contain the captured output (exit=$actualExit expected=[$expectedOutput] actual=[$actualText])" } }
    }
    return [pscustomobject]@{ Valid = $true; Reason = $null }
}

function Get-AcceptanceAnchorKind([string] $proof, [string] $RepositoryRoot) {
    if (@(Find-AcceptanceGitHubRunIds $proof).Count -gt 0) { return 'reproducible' }
    if (Test-AcceptanceCommandAnchor $proof) { return 'reproducible' }
    if (@(Get-AcceptanceCheckerMatches $proof).Count -gt 0) { return 'reproducible' }
    if (Test-AcceptanceJudgementAnchor $proof $RepositoryRoot) { return 'judgement' }
    return $null
}

function Invoke-AcceptanceMapLintCore {
    param(
        [Parameter(Mandatory = $true)] [string] $ChecklistPath,
        [Parameter(Mandatory = $true, ParameterSetName = 'Text')] [string] $MapText,
        [Parameter(Mandatory = $true, ParameterSetName = 'Path')] [string] $MapPath,
        [Parameter(Mandatory = $true, ParameterSetName = 'Stdin')] [switch] $MapFromStdin,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $CandidateSha,
        [string] $RepositoryRoot = (Get-Location).Path,
        [string] $Repository = 'KeelMatrix/CliContract',
        [scriptblock] $RunMetadataResolver
    )

    if ($PSCmdlet.ParameterSetName -eq 'Path') { $MapText = Get-Content -Raw -LiteralPath $MapPath }
    elseif ($PSCmdlet.ParameterSetName -eq 'Stdin') { $MapText = [Console]::In.ReadToEnd() }
    if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) { throw "Repository root was not found: $RepositoryRoot" }

    $checklistRows = @([regex]::Matches((Get-Content -Raw -LiteralPath $ChecklistPath), '(?m)^\s*\* \[ \] (.+)$') | ForEach-Object { $_.Groups[1].Value.Trim() })
    $mapRows = @([regex]::Matches($MapText, '(?m)^\|\s*(?<number>\d+)\s*\|(?<criterion>.*?)\|\s*(?<status>MET|UNMET|N/A)\s*\|\s*(?<evidence>.*)\|\s*$') | ForEach-Object { [pscustomobject]@{ Number = [int]$_.Groups['number'].Value; Criterion = $_.Groups['criterion'].Value.Trim(); Status = $_.Groups['status'].Value; Evidence = $_.Groups['evidence'].Value.Trim() } })
    if ($checklistRows.Count -eq 0) { throw 'The acceptance checklist did not contain checkbox criteria.' }
    if ($mapRows.Count -ne $checklistRows.Count) { throw "Acceptance map row count $($mapRows.Count) does not match checklist row count $($checklistRows.Count)." }
    for ($index = 0; $index -lt $checklistRows.Count; $index++) {
        $row = $mapRows[$index]; $expectedNumber = $index + 1
        if ($row.Number -ne $expectedNumber -or $row.Criterion -cne $checklistRows[$index]) { throw "Acceptance map criterion mismatch at position $expectedNumber. expected_number=$expectedNumber actual_number=$($row.Number) expected_criterion=[$($checklistRows[$index])] actual_criterion=[$($row.Criterion)]" }
    }
    $numbers = @($mapRows | Select-Object -ExpandProperty Number)
    $duplicates = @($numbers | Group-Object | Where-Object Count -gt 1 | Select-Object -ExpandProperty Name)
    $expectedNumbers = @(1..$checklistRows.Count)
    $missingNumbers = @(Compare-Object -ReferenceObject $expectedNumbers -DifferenceObject $numbers -PassThru | Where-Object { $_ -in $expectedNumbers })
    $unexpectedNumbers = @(Compare-Object -ReferenceObject $expectedNumbers -DifferenceObject $numbers -PassThru | Where-Object { $_ -notin $expectedNumbers })
    if ($duplicates.Count -gt 0 -or $missingNumbers.Count -gt 0 -or $unexpectedNumbers.Count -gt 0) { throw "Acceptance map numbering is not one unique sequential row per checklist criterion. duplicate_numbers=$($duplicates -join ',') missing_numbers=$($missingNumbers -join ',') unexpected_numbers=$($unexpectedNumbers -join ',')" }

    $invalidStatuses = @($mapRows | Where-Object { $_.Status -notin @('MET', 'UNMET', 'N/A') })
    $metRows = @($mapRows | Where-Object Status -eq 'MET'); $unmetRows = @($mapRows | Where-Object Status -eq 'UNMET'); $naRows = @($mapRows | Where-Object Status -eq 'N/A')
    $missingCandidateEvidence = @($metRows | Where-Object { $_.Evidence -notmatch "(?i)(^|[;\s])candidate_sha=$([regex]::Escape($CandidateSha.ToLowerInvariant()))([;\s]|$)" -or $_.Evidence -notmatch '(?i)(^|[;\s])proof=\s*\S' })
    $criterionHashMismatches = @(for ($index = 0; $index -lt $mapRows.Count; $index++) { $row = $mapRows[$index]; $expectedHash = Get-AcceptanceCriterionHash $checklistRows[$index]; if ($row.Evidence -notmatch "(?i)(^|[;\s])criterion_sha256=$expectedHash([;\s]|$)") { $row.Number } })
    $missingCriterionSpecificAnchor = @($metRows | Where-Object { $null -eq (Get-AcceptanceAnchorKind $_.Evidence $RepositoryRoot) })
    $runEvidenceFailures = @(foreach ($row in $mapRows) { $result = Test-AcceptanceRunEvidence $row.Evidence $CandidateSha $row.Number $Repository $RunMetadataResolver; if (-not $result.Valid) { $result.Reason } })
    $checkerEvidenceFailures = @(foreach ($row in $metRows) { $result = Test-AcceptanceCheckerEvidence $row.Evidence $row.Number $RepositoryRoot; if (-not $result.Valid) { $result.Reason } })
    $evidenceKindMismatches = @(foreach ($row in $metRows) { $actualKind = Get-AcceptanceAnchorKind $row.Evidence $RepositoryRoot; $declaredKinds = @(Get-AcceptanceTokenValues $row.Evidence 'proof_kind'); $expectedKind = if ($actualKind -eq 'judgement') { 'judgement' } else { 'reproducible' }; if ($declaredKinds.Count -ne 1 -or $declaredKinds[0] -cne $expectedKind) { $row.Number } })
    $naWithoutJustification = @($naRows | Where-Object { $_.Evidence -notmatch '(?i)(^|[;\s])N/A\s+[^;|]+' })
    $unmetWithoutJustification = @($unmetRows | Where-Object { $_.Evidence -notmatch '(?i)(^|[;\s])UNMET\s*:\s*[^;|]+' })

    if ($invalidStatuses.Count -gt 0) { throw 'Acceptance map contains a status other than MET, UNMET, or N/A.' }
    if ($missingCandidateEvidence.Count -gt 0) { throw "MET rows without exact candidate-SHA evidence and criterion-specific proof=: $($missingCandidateEvidence.Number -join ',')" }
    if ($missingCriterionSpecificAnchor.Count -gt 0) { throw "MET rows without a valid reproducible or explicit judgement anchor: $($missingCriterionSpecificAnchor.Number -join ',')" }
    if ($runEvidenceFailures.Count -gt 0) { throw "Invalid independently validated GitHub Actions evidence: $($runEvidenceFailures -join ' | ')" }
    if ($checkerEvidenceFailures.Count -gt 0) { throw "Invalid repository checker evidence: $($checkerEvidenceFailures -join ' | ')" }
    if ($evidenceKindMismatches.Count -gt 0) { throw "Evidence-kind marker does not match the proof anchor: $($evidenceKindMismatches -join ',')" }
    if ($naWithoutJustification.Count -gt 0) { throw "N/A rows without an applicability justification: $($naWithoutJustification.Number -join ',')" }
    if ($unmetWithoutJustification.Count -gt 0) { throw "UNMET rows without an explanation: $($unmetWithoutJustification.Number -join ',')" }
    if ($criterionHashMismatches.Count -gt 0) { throw "Acceptance map rows without the exact checklist criterion hash: $($criterionHashMismatches -join ',')" }
    Write-Output ("ACCEPTANCE_MAP_LINT=PASS checklist_rows={0} map_rows={1} met_rows={2} unmet_rows={3} na_rows={4} missing=0 duplicate_numbers=0 criterion_text_mismatches=0 criterion_hash_mismatches=0 missing_candidate_evidence=0 met_without_candidate_sha=0 met_without_anchor=0 invalid_run_evidence=0 checker_evidence_failures=0 evidence_kind_mismatches=0 na_without_justification=0 unmet_without_justification=0 candidate_sha={5}" -f $checklistRows.Count, $mapRows.Count, $metRows.Count, $unmetRows.Count, $naRows.Count, $CandidateSha.ToLowerInvariant())
}
