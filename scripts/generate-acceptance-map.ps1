param(
    [Parameter(Mandatory = $true)]
    [string] $ChecklistPath,

    [Parameter(Mandatory = $true)]
    [string] $EvidencePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $CandidateSha,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
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

function Escape-Cell([string] $value) {
    return $value.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ').Trim()
}

$checklistRows = @(
    [regex]::Matches((Get-Content -Raw -LiteralPath $ChecklistPath), '(?m)^\s*\* \[ \] (.+)$') |
        ForEach-Object { $_.Groups[1].Value.Trim() }
)

if ($checklistRows.Count -eq 0) {
    throw 'The acceptance checklist did not contain checkbox criteria.'
}

$evidenceEntries = @(Get-Content -Raw -LiteralPath $EvidencePath | ConvertFrom-Json)
$evidenceByCriterion = @{}
foreach ($entry in $evidenceEntries) {
    if ($null -eq $entry.criterion -or $null -eq $entry.status -or $null -eq $entry.evidence) {
        throw 'Each evidence entry requires criterion, status, and evidence properties.'
    }

    $criterion = [string]$entry.criterion
    if ($evidenceByCriterion.ContainsKey($criterion)) {
        throw "Duplicate evidence entry for criterion: $criterion"
    }

    $evidenceByCriterion[$criterion] = $entry
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# First-release acceptance map')
$lines.Add('')
$lines.Add('Generated from the current read-only acceptance checklist by `scripts/generate-acceptance-map.ps1`; row numbers and criterion text are not hand-maintained.')
$lines.Add('')
$lines.Add('| # | Criterion | Status | Candidate-SHA evidence or disposition |')
$lines.Add('|---:|---|:---:|---|')

for ($index = 0; $index -lt $checklistRows.Count; $index++) {
    $criterion = $checklistRows[$index]
    if (-not $evidenceByCriterion.ContainsKey($criterion)) {
        throw "Evidence is missing for checklist criterion $($index + 1)."
    }

    $entry = $evidenceByCriterion[$criterion]
    $status = [string]$entry.status
    if ($status -notin @('MET', 'UNMET', 'N/A')) {
        throw "Unsupported acceptance status '$status' for criterion $($index + 1)."
    }

    $hash = Get-CriterionHash $criterion
    $detail = ([string]$entry.evidence).Trim()
    if ([string]::IsNullOrWhiteSpace($detail)) {
        throw "Evidence is empty for checklist criterion $($index + 1)."
    }

    if ($status -eq 'MET') {
        $evidence = "candidate_sha=$($CandidateSha.ToLowerInvariant()); criterion_sha256=$hash; proof=$(Escape-Cell $detail)"
    }
    elseif ($status -eq 'UNMET') {
        $evidence = "criterion_sha256=$hash; UNMET: $(Escape-Cell $detail)"
    }
    else {
        $evidence = "criterion_sha256=$hash; N/A $(Escape-Cell $detail)"
    }

    $lines.Add(("| {0} | {1} | {2} | {3} |" -f ($index + 1), (Escape-Cell $criterion), $status, $evidence))
}

$unknownEvidence = @($evidenceByCriterion.Keys | Where-Object { $_ -notin $checklistRows })
if ($unknownEvidence.Count -gt 0) {
    throw "Evidence contains criteria not present in the current checklist: $($unknownEvidence -join ' | ')"
}

$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$parent = Split-Path -Parent $fullOutputPath
if ($parent -and -not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
[IO.File]::WriteAllText($fullOutputPath, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "ACCEPTANCE_MAP_GENERATED=PASS checklist_rows=$($checklistRows.Count) output=$fullOutputPath candidate_sha=$($CandidateSha.ToLowerInvariant())"
