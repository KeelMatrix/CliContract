param(
    [Parameter(Mandatory = $true)]
    [string] $ChecklistPath,

    [Parameter(Mandatory = $true)]
    [string] $EvidencePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $CandidateSha,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $Repository = 'KeelMatrix/CliContract'
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

function Find-GitHubRunIds([string] $proof) {
    $pattern = '(?ix)(?:github\s+actions?\s+run(?:\s+id)?|github\s+run(?:\s+id)?|actions?\s+run(?:\s+id)?|ci\s+run(?:\s+id)?|run_id)\s*[:=#]?\s*(?<id>\d{6,})\b|gh\s+run\s+view\s+(?<ghid>\d{6,})\b'
    $ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($match in [regex]::Matches($proof, $pattern)) {
        $id = if ($match.Groups['id'].Success) { $match.Groups['id'].Value } else { $match.Groups['ghid'].Value }
        if ($id) { [void]$ids.Add($id) }
    }
    return @($ids | Sort-Object { [long]$_ })
}

function Remove-GeneratedRunMetadata([string] $proof) {
    return [regex]::Replace(
        $proof,
        '(?i)\bgithub_run_(?:id|head_sha|status|conclusion|event)=[^;|]+;?\s*',
        ''
    ).Trim()
}

function Get-EvidenceKind([string] $proof, [int] $runCount) {
    if ($runCount -gt 0 -or
        $proof -match '(?is)(?:^|[;\s]|proof=)command=[^;|]+;\s*output=[^;|]+' -or
        $proof -match '(?is)(?:^|[;\s]|proof=)checker=[^;|]+;\s*checker_output=[^;|]+') {
        return 'reproducible'
    }
    if ($proof -match '(?is)(?:^|[;\s]|proof=)judgement=[^;|]+') {
        return 'judgement'
    }
    return 'reproducible'
}

function Get-GitHubRunMetadata([string] $runId, [string] $repository, [scriptblock] $runMetadataResolver) {
    if ($null -ne $runMetadataResolver) {
        return & $runMetadataResolver $runId
    }

    $raw = @(& gh run view $runId --repo $repository --json headSha,status,conclusion,event 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to validate GitHub Actions run $runId with gh run view. Output: $($raw -join ' ')"
    }

    try {
        $metadata = ($raw | ForEach-Object { $_.ToString() }) -join "`n" | ConvertFrom-Json
    }
    catch {
        throw "GitHub Actions run $runId returned invalid JSON metadata: $($_.Exception.Message)"
    }
    return $metadata
}

function Get-RunField([object] $metadata, [string] $name, [string] $runId) {
    $value = $metadata.$name
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        throw "GitHub Actions run $runId metadata did not contain $name."
    }
    return ([string]$value).Trim()
}

function Invoke-AcceptanceMapGeneration {
param(
    [Parameter(Mandatory = $true)]
    [string] $ChecklistPath,

    [Parameter(Mandatory = $true)]
    [string] $EvidencePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $CandidateSha,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $Repository = 'KeelMatrix/CliContract',

    [scriptblock] $RunMetadataResolver
)

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
$lines.Add('Generated from the current read-only acceptance checklist by `scripts/generate-acceptance-map.ps1`; production named-run evidence is independently resolved, while the self-test uses only internal fixture metadata; row numbers and criterion text are not hand-maintained.')
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

    $runIds = @(Find-GitHubRunIds $detail)
    $detail = Remove-GeneratedRunMetadata $detail
    foreach ($runId in $runIds) {
        $metadata = Get-GitHubRunMetadata $runId $Repository $RunMetadataResolver
        $headSha = Get-RunField $metadata 'headSha' $runId
        if ($headSha -notmatch '^[0-9a-fA-F]{40}$') {
            throw "GitHub Actions run $runId returned an invalid headSha: $headSha"
        }

        if ($headSha -ine $CandidateSha) {
            throw "GitHub Actions run $runId is not candidate-bound: headSha=$headSha candidate_sha=$($CandidateSha.ToLowerInvariant())"
        }

        $runStatus = Get-RunField $metadata 'status' $runId
        $conclusion = Get-RunField $metadata 'conclusion' $runId
        $event = Get-RunField $metadata 'event' $runId
        $detail = "$detail; github_run_id=$runId; github_run_head_sha=$($headSha.ToLowerInvariant()); github_run_status=$runStatus; github_run_conclusion=$conclusion; github_run_event=$event"
    }

    if ($status -eq 'MET') {
        $kind = Get-EvidenceKind $detail $runIds.Count
        $evidence = "candidate_sha=$($CandidateSha.ToLowerInvariant()); proof_kind=$kind; criterion_sha256=$hash; proof=$(Escape-Cell $detail)"
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
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-AcceptanceMapGeneration @PSBoundParameters
}
