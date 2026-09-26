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
    [string] $CandidateSha
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

if ($PSCmdlet.ParameterSetName -eq 'Path') {
    $MapText = Get-Content -Raw -LiteralPath $MapPath
}
elseif ($PSCmdlet.ParameterSetName -eq 'Stdin') {
    $MapText = [Console]::In.ReadToEnd()
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
$naWithoutJustification = @($naRows | Where-Object { $_.Evidence -notmatch '(?i)(^|[;\s])N/A\s+[^;|]+' })
$unmetWithoutJustification = @($unmetRows | Where-Object { $_.Evidence -notmatch '(?i)(^|[;\s])UNMET\s*:\s*[^;|]+' })

if ($invalidStatuses.Count -gt 0) {
    throw 'Acceptance map contains a status other than MET, UNMET, or N/A.'
}

if ($missingCandidateEvidence.Count -gt 0) {
    throw "MET rows without exact candidate-SHA evidence and criterion-specific proof=: $($missingCandidateEvidence.Number -join ',')"
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

Write-Output ("ACCEPTANCE_MAP_LINT=PASS checklist_rows={0} map_rows={1} met_rows={2} unmet_rows={3} na_rows={4} missing=0 duplicate_numbers=0 criterion_text_mismatches=0 criterion_hash_mismatches=0 missing_candidate_evidence=0 met_without_candidate_sha=0 na_without_justification=0 unmet_without_justification=0 candidate_sha={5}" -f `
    $checklistRows.Count, $mapRows.Count, $metRows.Count, $unmetRows.Count, $naRows.Count, $CandidateSha.ToLowerInvariant())
