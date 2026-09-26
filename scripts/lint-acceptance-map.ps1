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
    [regex]::Matches($MapText, '(?m)^\|\s*(?<number>\d+)\s*\|(?<criterion>.*?)\|\s*(?<status>MET|N/A)\s*\|\s*(?<evidence>.*?)\|\s*$') |
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

$numbers = @($mapRows | Select-Object -ExpandProperty Number)
$duplicates = @($numbers | Group-Object | Where-Object Count -gt 1 | Select-Object -ExpandProperty Name)
$expectedNumbers = @(1..$checklistRows.Count)
$missingNumbers = @(Compare-Object -ReferenceObject $expectedNumbers -DifferenceObject $numbers -PassThru | Where-Object { $_ -in $expectedNumbers })
$unexpectedNumbers = @(Compare-Object -ReferenceObject $expectedNumbers -DifferenceObject $numbers -PassThru | Where-Object { $_ -notin $expectedNumbers })

if ($duplicates.Count -gt 0 -or $missingNumbers.Count -gt 0 -or $unexpectedNumbers.Count -gt 0) {
    throw "Acceptance map numbering is not one unique sequential row per checklist criterion. duplicate_numbers=$($duplicates -join ',') missing_numbers=$($missingNumbers -join ',') unexpected_numbers=$($unexpectedNumbers -join ',')"
}

$pathPattern = '(?i)(^|[\s;])(?:\.github/|docs/|scripts/|src/|tests/|fixtures/|(?:README|CHANGELOG|PRIVACY|SECURITY|LICENSE|CONTRIBUTING|MANIFEST|COMPATIBILITY-RULES)\.md|Directory\.[A-Za-z0-9._-]+|global\.json|NuGet\.config|\.gitignore|\.gitattributes)'
$invalidStatuses = @($mapRows | Where-Object { $_.Status -notin @('MET', 'N/A') })
$metRows = @($mapRows | Where-Object Status -eq 'MET')
$naRows = @($mapRows | Where-Object Status -eq 'N/A')
$missingCandidateEvidence = @(
    $metRows | Where-Object {
        $_.Evidence -notmatch [regex]::Escape($CandidateSha) -and
        $_.Evidence -notmatch $pathPattern
    }
)
$naWithoutJustification = @($naRows | Where-Object { $_.Evidence -notmatch '(?i)^N/A\s+' })

if ($invalidStatuses.Count -gt 0) {
    throw 'Acceptance map contains a status other than MET or N/A.'
}

if ($missingCandidateEvidence.Count -gt 0) {
    throw "MET rows without the candidate SHA or a precise repository file/test path: $($missingCandidateEvidence.Number -join ',')"
}

if ($naWithoutJustification.Count -gt 0) {
    throw "N/A rows without an applicability justification: $($naWithoutJustification.Number -join ',')"
}

$missingCandidateSha = @($metRows | Where-Object { $_.Evidence -notmatch [regex]::Escape($CandidateSha) })
Write-Output ("ACCEPTANCE_MAP_LINT=PASS checklist_rows={0} map_rows={1} met_rows={2} na_rows={3} missing=0 duplicate_numbers=0 missing_candidate_evidence=0 met_without_candidate_sha={4} na_without_justification=0 candidate_sha={5}" -f `
    $checklistRows.Count, $mapRows.Count, $metRows.Count, $naRows.Count, $missingCandidateSha.Count, $CandidateSha.ToLowerInvariant())
