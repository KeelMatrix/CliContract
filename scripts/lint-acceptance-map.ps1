param(
    [Parameter(Mandatory = $true)]
    [string] $ChecklistPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Text')]
    [string] $MapText,

    [Parameter(Mandatory = $true, ParameterSetName = 'Path')]
    [string] $MapPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Stdin')]
    [switch] $MapFromStdin,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $CandidateSha,

    [string] $RepositoryRoot = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'

function Get-CriterionHash([string] $criterion) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($criterion))).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Find-GitHubRunIds([string] $proof) {
    $pattern = '(?ix)(?:github\s+actions?\s+run(?:\s+id)?|github\s+run(?:\s+id)?|actions?\s+run(?:\s+id)?|ci\s+run(?:\s+id)?|run_id)\s*[:=#]?\s*(?<id>\d{6,})\b|gh\s+run\s+view\s+(?<ghid>\d{6,})\b'
    $ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($match in [regex]::Matches($proof, $pattern)) {
        $id = if ($match.Groups['id'].Success) { $match.Groups['id'].Value } else { $match.Groups['ghid'].Value }
        if ($id) { [void]$ids.Add($id) }
    }
    return @($ids | Sort-Object { [long]$_ })
}

function Get-TokenValues([string] $proof, [string] $tokenName) {
    $escapedName = [regex]::Escape($tokenName)
    return @(
        [regex]::Matches($proof, "(?i)(?:^|[;\s])$escapedName=(?<value>[^;|]+)") |
            ForEach-Object { $_.Groups['value'].Value.Trim() }
    )
}

function Test-RepositoryPath([string] $candidatePath) {
    $candidatePath = $candidatePath.Trim().Trim('`', '"', "'")
    if ([string]::IsNullOrWhiteSpace($candidatePath) -or $candidatePath.Contains('*') -or [IO.Path]::IsPathRooted($candidatePath)) {
        return $false
    }

    try {
        $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $full = [IO.Path]::GetFullPath((Join-Path $root $candidatePath))
        $relative = [IO.Path]::GetRelativePath($root, $full)
        if ($relative -eq '..' -or $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)") -or [IO.Path]::IsPathRooted($relative)) {
            return $false
        }
        return Test-Path -LiteralPath $full
    }
    catch {
        return $false
    }
}

function Test-Placeholder([string] $value) {
    $normalized = ([string]$value).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($normalized)) { return $true }
    if ($normalized -match '^(?:generic evidence|placeholder|arbitrary(?: proof)?|tbd|todo|fixme|lorem ipsum|proof goes here|n/?a)$') { return $true }
    if ($normalized -match '^(?:candidate_sha=)?[0-9a-f]{40}$') { return $true }
    return $false
}

function Test-CriterionSpecificAnchor([string] $proof, [int] $rowNumber) {
    $pathAnchors = @(
        [regex]::Matches($proof, '(?i)(?:^|[;\s]|proof=)(?:repo_path|path)=(?<value>[^;|]+)') |
            ForEach-Object { $_.Groups['value'].Value.Trim() } |
            Where-Object { Test-RepositoryPath $_ }
    )
    if ($pathAnchors.Count -gt 0) { return $true }

    $commandAnchors = @(
        [regex]::Matches($proof, '(?is)(?:^|[;\s]|proof=)command=(?<command>[^;|]+);\s*output=(?<output>[^;|]+)') |
            Where-Object { -not (Test-Placeholder $_.Groups['command'].Value) -and -not (Test-Placeholder $_.Groups['output'].Value) }
    )
    if ($commandAnchors.Count -gt 0) { return $true }

    $valueAnchors = @(
        [regex]::Matches($proof, '(?i)(?:^|[;\s]|proof=)criterion_value=(?<value>[^;|]+)') |
            ForEach-Object { $_.Groups['value'].Value.Trim() } |
            Where-Object { -not (Test-Placeholder $_) }
    )
    if ($valueAnchors.Count -gt 0) { return $true }

    return $false
}

function Test-RunEvidence([string] $proof, [string] $candidateSha, [int] $rowNumber) {
    $runIds = @(Find-GitHubRunIds $proof)
    if ($runIds.Count -eq 0) {
        return [pscustomobject]@{ Valid = $true; Reason = $null }
    }

    $tokenRunIds = @(Get-TokenValues $proof 'github_run_id')
    $headShas = @(Get-TokenValues $proof 'github_run_head_sha')
    $statuses = @(Get-TokenValues $proof 'github_run_status')
    $conclusions = @(Get-TokenValues $proof 'github_run_conclusion')
    if ($tokenRunIds.Count -lt $runIds.Count -or $headShas.Count -lt $runIds.Count -or $statuses.Count -lt $runIds.Count -or $conclusions.Count -lt $runIds.Count) {
        return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber names GitHub Actions run(s) without complete generator-derived metadata" }
    }
    foreach ($runId in $runIds) {
        if ($runId -notin $tokenRunIds) {
            return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber names run $runId without github_run_id metadata" }
        }
    }
    if (@($headShas | Where-Object { $_ -notmatch '^[0-9a-fA-F]{40}$' -or $_ -ine $candidateSha }).Count -gt 0) {
        return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber names a GitHub Actions run whose head SHA is not the candidate" }
    }
    if (@($statuses | Where-Object { $_ -ine 'completed' }).Count -gt 0 -or @($conclusions | Where-Object { $_ -ine 'success' }).Count -gt 0) {
        return [pscustomobject]@{ Valid = $false; Reason = "row $rowNumber names a GitHub Actions run that is not completed/success" }
    }
    return [pscustomobject]@{ Valid = $true; Reason = $null }
}

if ($PSCmdlet.ParameterSetName -eq 'Path') {
    $MapText = Get-Content -Raw -LiteralPath $MapPath
}
elseif ($PSCmdlet.ParameterSetName -eq 'Stdin') {
    $MapText = [Console]::In.ReadToEnd()
}

if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    throw "Repository root was not found: $RepositoryRoot"
}

$checklistRows = @(
    [regex]::Matches((Get-Content -Raw -LiteralPath $ChecklistPath), '(?m)^\s*\* \[ \] (.+)$') |
        ForEach-Object { $_.Groups[1].Value.Trim() }
)

$mapRows = @(
    [regex]::Matches($MapText, '(?m)^\|\s*(?<number>\d+)\s*\|(?<criterion>.*?)\|\s*(?<status>MET|UNMET|N/A)\s*\|\s*(?<evidence>.*)\|\s*$') |
        ForEach-Object {
            [pscustomobject]@{
                Number = [int]$_.Groups['number'].Value
                Criterion = $_.Groups['criterion'].Value.Trim()
                Status = $_.Groups['status'].Value
                Evidence = $_.Groups['evidence'].Value.Trim()
            }
        }
)

if ($checklistRows.Count -eq 0) {
    throw 'The acceptance checklist did not contain checkbox criteria.'
}

if ($mapRows.Count -ne $checklistRows.Count) {
    throw "Acceptance map row count $($mapRows.Count) does not match checklist row count $($checklistRows.Count)."
}

for ($index = 0; $index -lt $checklistRows.Count; $index++) {
    $row = $mapRows[$index]
    $expectedNumber = $index + 1
    if ($row.Number -ne $expectedNumber -or $row.Criterion -cne $checklistRows[$index]) {
        throw "Acceptance map criterion mismatch at position $expectedNumber. expected_number=$expectedNumber actual_number=$($row.Number) expected_criterion=[$($checklistRows[$index])] actual_criterion=[$($row.Criterion)]"
    }
}

$numbers = @($mapRows | Select-Object -ExpandProperty Number)
$duplicates = @($numbers | Group-Object | Where-Object Count -gt 1 | Select-Object -ExpandProperty Name)
$expectedNumbers = @(1..$checklistRows.Count)
$missingNumbers = @(Compare-Object -ReferenceObject $expectedNumbers -DifferenceObject $numbers -PassThru | Where-Object { $_ -in $expectedNumbers })
$unexpectedNumbers = @(Compare-Object -ReferenceObject $expectedNumbers -DifferenceObject $numbers -PassThru | Where-Object { $_ -notin $expectedNumbers })

if ($duplicates.Count -gt 0 -or $missingNumbers.Count -gt 0 -or $unexpectedNumbers.Count -gt 0) {
    throw "Acceptance map numbering is not one unique sequential row per checklist criterion. duplicate_numbers=$($duplicates -join ',') missing_numbers=$($missingNumbers -join ',') unexpected_numbers=$($unexpectedNumbers -join ',')"
}

$invalidStatuses = @($mapRows | Where-Object { $_.Status -notin @('MET', 'UNMET', 'N/A') })
$metRows = @($mapRows | Where-Object Status -eq 'MET')
$unmetRows = @($mapRows | Where-Object Status -eq 'UNMET')
$naRows = @($mapRows | Where-Object Status -eq 'N/A')
$missingCandidateEvidence = @(
    $metRows | Where-Object {
        $_.Evidence -notmatch "(?i)(^|[;\s])candidate_sha=$([regex]::Escape($CandidateSha.ToLowerInvariant()))([;\s]|$)" -or
        $_.Evidence -notmatch '(?i)(^|[;\s])proof=\s*\S'
    }
)
$criterionHashMismatches = @(
    for ($index = 0; $index -lt $mapRows.Count; $index++) {
        $row = $mapRows[$index]
        $expectedHash = Get-CriterionHash $checklistRows[$index]
        if ($row.Evidence -notmatch "(?i)(^|[;\s])criterion_sha256=$expectedHash([;\s]|$)") {
            $row.Number
        }
    }
)
$missingCriterionSpecificAnchor = @(
    $metRows | Where-Object { -not (Test-CriterionSpecificAnchor $_.Evidence $_.Number) }
)
$runEvidenceFailures = @(
    foreach ($row in $metRows) {
        $result = Test-RunEvidence $row.Evidence $CandidateSha $row.Number
        if (-not $result.Valid) { $result.Reason }
    }
)
$naWithoutJustification = @($naRows | Where-Object { $_.Evidence -notmatch '(?i)(^|[;\s])N/A\s+[^;|]+' })
$unmetWithoutJustification = @($unmetRows | Where-Object { $_.Evidence -notmatch '(?i)(^|[;\s])UNMET\s*:\s*[^;|]+' })

if ($invalidStatuses.Count -gt 0) {
    throw 'Acceptance map contains a status other than MET, UNMET, or N/A.'
}

if ($missingCandidateEvidence.Count -gt 0) {
    throw "MET rows without exact candidate-SHA evidence and criterion-specific proof=: $($missingCandidateEvidence.Number -join ',')"
}

if ($missingCriterionSpecificAnchor.Count -gt 0) {
    throw "MET rows without a machine-checkable criterion-specific anchor: $($missingCriterionSpecificAnchor.Number -join ',')"
}

if ($runEvidenceFailures.Count -gt 0) {
    throw "Invalid candidate-bound GitHub Actions evidence: $($runEvidenceFailures -join ' | ')"
}

if ($naWithoutJustification.Count -gt 0) {
    throw "N/A rows without an applicability justification: $($naWithoutJustification.Number -join ',')"
}

if ($unmetWithoutJustification.Count -gt 0) {
    throw "UNMET rows without an explanation: $($unmetWithoutJustification.Number -join ',')"
}

if ($criterionHashMismatches.Count -gt 0) {
    throw "Acceptance map rows without the exact checklist criterion hash: $($criterionHashMismatches -join ',')"
}

Write-Output ("ACCEPTANCE_MAP_LINT=PASS checklist_rows={0} map_rows={1} met_rows={2} unmet_rows={3} na_rows={4} missing=0 duplicate_numbers=0 criterion_text_mismatches=0 criterion_hash_mismatches=0 missing_candidate_evidence=0 met_without_candidate_sha=0 met_without_anchor=0 invalid_run_evidence=0 na_without_justification=0 unmet_without_justification=0 candidate_sha={5}" -f `
    $checklistRows.Count, $mapRows.Count, $metRows.Count, $unmetRows.Count, $naRows.Count, $CandidateSha.ToLowerInvariant())
