$ErrorActionPreference = 'Stop'
$scriptDirectory = $PSScriptRoot
$repositoryRoot = Split-Path -Parent $scriptDirectory
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-acceptance-map-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $checklist = Join-Path $temp 'checklist.md'
    $evidence = Join-Path $temp 'evidence.json'
    $staleEvidence = Join-Path $temp 'stale-evidence.json'
    $runMetadata = Join-Path $repositoryRoot 'fixtures/acceptance-map/run-metadata.json'
    $map = Join-Path $temp 'map.md'
    $mapAgain = Join-Path $temp 'map-again.md'
    $fixtureMap = Join-Path $temp 'fixture-map.md'
    $staleRunMap = Join-Path $temp 'stale-run-map.md'
    $forgedRunMap = Join-Path $temp 'forged-run-map.md'
    $pathOnlyMap = Join-Path $temp 'path-only-map.md'
    $judgementWithoutArtifactMap = Join-Path $temp 'judgement-without-artifact-map.md'
    $genericMap = Join-Path $temp 'generic-map.md'
    $tamperedTextMap = Join-Path $temp 'tampered-text-map.md'

    $candidate = '0123456789abcdef0123456789abcdef01234567'
    $stale = 'fedcba9876543210fedcba9876543210fedcba98'

    [IO.File]::WriteAllText($checklist, "* [ ] First criterion`n* [ ] Second criterion`n* [ ] Third criterion`n* [ ] Fourth criterion`n* [ ] Fifth criterion`n", [Text.UTF8Encoding]::new($false))
    $evidenceEntries = @(
        @{ criterion = 'First criterion'; status = 'MET'; evidence = 'repo_path=README.md; command=pwsh -NoProfile -File ./scripts/test-acceptance-map.ps1; output=ACCEPTANCE_MAP_SELF_TEST=PASS' },
        @{ criterion = 'Second criterion'; status = 'MET'; evidence = 'GitHub Actions run 123456789; criterion_value=completed candidate validation' },
        @{ criterion = 'Third criterion'; status = 'MET'; evidence = 'judgement=artifacts=README.md; rationale=reviewer verified the criterion-specific installation contract in README.md' },
        @{ criterion = 'Fourth criterion'; status = 'MET'; evidence = 'checker=./scripts/check-no-execution.ps1; checker_output=no process-start' },
        @{ criterion = 'Fifth criterion'; status = 'N/A'; evidence = 'only applies to multi-package repositories' }
    )
    [IO.File]::WriteAllText($evidence, ($evidenceEntries | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $staleEntries = @($evidenceEntries | ForEach-Object {
        if ($_.criterion -eq 'Second criterion') {
            @{ criterion = $_.criterion; status = $_.status; evidence = 'GitHub Actions run 36196565020; criterion_value=stale run regression' }
        }
        else { $_ }
    })
    [IO.File]::WriteAllText($staleEvidence, ($staleEntries | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

    $generator = Join-Path $scriptDirectory 'generate-acceptance-map.ps1'
    $linter = Join-Path $scriptDirectory 'lint-acceptance-map.ps1'
    $fixtureCandidate = '1111111111111111111111111111111111111111'
    & pwsh -NoProfile -File $generator -ChecklistPath (Join-Path $repositoryRoot 'fixtures/acceptance-map/checklist.md') -EvidencePath (Join-Path $repositoryRoot 'fixtures/acceptance-map/evidence.json') -CandidateSha $fixtureCandidate -OutputPath $fixtureMap
    if ($LASTEXITCODE -ne 0 -or -not ([Linq.Enumerable]::SequenceEqual([IO.File]::ReadAllBytes($fixtureMap), [IO.File]::ReadAllBytes((Join-Path $repositoryRoot 'fixtures/acceptance-map/golden-map.md'))))) {
        throw 'Acceptance map fixture did not reproduce its checked-in golden map.'
    }
    & pwsh -NoProfile -File $linter -ChecklistPath (Join-Path $repositoryRoot 'fixtures/acceptance-map/checklist.md') -MapPath $fixtureMap -CandidateSha $fixtureCandidate -RepositoryRoot $repositoryRoot
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance map fixture lint failed.' }

    $runMetadataById = @{}
    foreach ($metadata in @(Get-Content -Raw -LiteralPath $runMetadata | ConvertFrom-Json)) {
        $runMetadataById[[string]$metadata.run_id] = [pscustomobject]@{
            headSha = [string]$metadata.headSha
            status = [string]$metadata.status
            conclusion = [string]$metadata.conclusion
            event = [string]$metadata.event
        }
    }
    $runMetadataResolver = {
        param([string] $runId)
        if (-not $runMetadataById.ContainsKey($runId)) {
            throw "Test fixture has no metadata for GitHub Actions run $runId."
        }
        return $runMetadataById[$runId]
    }

    function Assert-ExpectedRejection {
        param(
            [Parameter(Mandatory = $true)]
            [scriptblock] $Action,

            [Parameter(Mandatory = $true)]
            [string] $ExpectedPattern,

            [Parameter(Mandatory = $true)]
            [string] $FailureMessage
        )

        $output = @()
        $completed = $true
        try {
            $output = @(& $Action 2>&1)
        }
        catch {
            $completed = $false
            $output += $_
        }

        $text = $output -join "`n"
        if ($completed -or $text -notmatch $ExpectedPattern) {
            throw "$FailureMessage Output: $text"
        }
    }

    # Dot-sourcing the production scripts must not expose either implementation or
    # its test-only metadata resolver seam.
    . $generator -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath $map
    . $linter -ChecklistPath $checklist -MapPath $map -CandidateSha $candidate -RepositoryRoot $repositoryRoot

    if (Get-Command Invoke-AcceptanceMapGeneration -ErrorAction SilentlyContinue) {
        throw 'Dot-sourcing the production generator exposed a callable implementation.'
    }
    if (Get-Command Invoke-AcceptanceMapLint -ErrorAction SilentlyContinue) {
        throw 'Dot-sourcing the production linter exposed a callable implementation.'
    }

    . (Join-Path $scriptDirectory 'private\acceptance-map-internals.ps1')

    Invoke-AcceptanceMapGenerationCore -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath $map -RunMetadataResolver $runMetadataResolver
    Invoke-AcceptanceMapGenerationCore -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath $mapAgain -RunMetadataResolver $runMetadataResolver
    if (-not (Test-Path -LiteralPath $map) -or -not (Test-Path -LiteralPath $mapAgain)) { throw 'Acceptance map self-test did not generate both maps.' }
    if (-not ([Linq.Enumerable]::SequenceEqual([IO.File]::ReadAllBytes($map), [IO.File]::ReadAllBytes($mapAgain)))) {
        throw 'Acceptance map generator was not byte-identical for the same inputs.'
    }

    Invoke-AcceptanceMapLintCore -ChecklistPath $checklist -MapPath $map -CandidateSha $candidate -RepositoryRoot $repositoryRoot -RunMetadataResolver $runMetadataResolver

    $metadataOverrideOutput = @(& pwsh -NoProfile -File $generator -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath (Join-Path $temp 'metadata-override-map.md') -RunMetadataPath $runMetadata 2>&1)
    $metadataOverrideExit = $LASTEXITCODE
    if ($metadataOverrideExit -eq 0 -or ($metadataOverrideOutput -join "`n") -notmatch 'RunMetadataPath|parameter cannot be found|named parameter') {
        throw 'Acceptance map generator self-test still exposed a run metadata override.'
    }
    $metadataResolverOverrideOutput = @(& pwsh -NoProfile -File $generator -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath (Join-Path $temp 'resolver-override-map.md') -RunMetadataResolver $runMetadata 2>&1)
    $metadataResolverOverrideExit = $LASTEXITCODE
    if ($metadataResolverOverrideExit -eq 0 -or ($metadataResolverOverrideOutput -join "`n") -notmatch 'RunMetadataResolver|parameter cannot be found|named parameter') {
        throw 'Acceptance map generator self-test exposed its internal run metadata resolver.'
    }

    $tampered = Get-Content -Raw -LiteralPath $map
    $tampered = $tampered.Replace("github_run_head_sha=$candidate", "github_run_head_sha=$stale")
    [IO.File]::WriteAllText($staleRunMap, $tampered, [Text.UTF8Encoding]::new($false))
    Assert-ExpectedRejection `
        -Action { Invoke-AcceptanceMapLintCore -ChecklistPath $checklist -MapPath $staleRunMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot -RunMetadataResolver $runMetadataResolver } `
        -ExpectedPattern 'forged or stale head SHA metadata' `
        -FailureMessage 'Acceptance map lint self-test accepted forged embedded run metadata.'

    $forgedRun = (Get-Content -Raw -LiteralPath $map).Replace('github_run_id=123456789', 'github_run_id=36196565020')
    [IO.File]::WriteAllText($forgedRunMap, $forgedRun, [Text.UTF8Encoding]::new($false))
    Assert-ExpectedRejection `
        -Action { Invoke-AcceptanceMapLintCore -ChecklistPath $checklist -MapPath $forgedRunMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot -RunMetadataResolver $runMetadataResolver } `
        -ExpectedPattern 'resolved head SHA is not the candidate|independently validate' `
        -FailureMessage 'Acceptance map lint self-test accepted a stale run id paired with a forged candidate head SHA.'

    Assert-ExpectedRejection `
        -Action { Invoke-AcceptanceMapGenerationCore -ChecklistPath $checklist -EvidencePath $staleEvidence -CandidateSha $candidate -OutputPath (Join-Path $temp 'stale-generated-map.md') -RunMetadataResolver $runMetadataResolver } `
        -ExpectedPattern 'not candidate-bound' `
        -FailureMessage 'Acceptance map generator self-test accepted a stale run id end-to-end.'

    $pathOnly = (Get-Content -Raw -LiteralPath $map).Replace('repo_path=README.md; command=pwsh -NoProfile -File ./scripts/test-acceptance-map.ps1; output=ACCEPTANCE_MAP_SELF_TEST=PASS', 'repo_path=LICENSE')
    [IO.File]::WriteAllText($pathOnlyMap, $pathOnly, [Text.UTF8Encoding]::new($false))
    Assert-ExpectedRejection `
        -Action { Invoke-AcceptanceMapLintCore -ChecklistPath $checklist -MapPath $pathOnlyMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot -RunMetadataResolver $runMetadataResolver } `
        -ExpectedPattern 'valid reproducible or explicit judgement anchor' `
        -FailureMessage 'Acceptance map lint self-test accepted an existing but irrelevant path anchor.'

    $judgementWithoutArtifact = (Get-Content -Raw -LiteralPath $map).Replace('judgement=artifacts=README.md', 'judgement=reviewer judgment without an artifact reference')
    [IO.File]::WriteAllText($judgementWithoutArtifactMap, $judgementWithoutArtifact, [Text.UTF8Encoding]::new($false))
    Assert-ExpectedRejection `
        -Action { Invoke-AcceptanceMapLintCore -ChecklistPath $checklist -MapPath $judgementWithoutArtifactMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot -RunMetadataResolver $runMetadataResolver } `
        -ExpectedPattern 'valid reproducible or explicit judgement anchor' `
        -FailureMessage 'Acceptance map lint self-test accepted a judgement without a criterion-specific artifact.'

    $generic = $pathOnly.Replace('repo_path=LICENSE', 'generic evidence')
    [IO.File]::WriteAllText($genericMap, $generic, [Text.UTF8Encoding]::new($false))
    Assert-ExpectedRejection `
        -Action { Invoke-AcceptanceMapLintCore -ChecklistPath $checklist -MapPath $genericMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot -RunMetadataResolver $runMetadataResolver } `
        -ExpectedPattern 'valid reproducible or explicit judgement anchor' `
        -FailureMessage 'Acceptance map lint self-test accepted generic proof text.'

    $textDrift = (Get-Content -Raw -LiteralPath $map).Replace('Second criterion', 'Tampered criterion')
    [IO.File]::WriteAllText($tamperedTextMap, $textDrift, [Text.UTF8Encoding]::new($false))
    Assert-ExpectedRejection `
        -Action { Invoke-AcceptanceMapLintCore -ChecklistPath $checklist -MapPath $tamperedTextMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot -RunMetadataResolver $runMetadataResolver } `
        -ExpectedPattern 'criterion mismatch' `
        -FailureMessage 'Acceptance map lint self-test accepted criterion-text drift.'

    Assert-ExpectedRejection `
        -Action { Invoke-AcceptanceMapLintCore -ChecklistPath $checklist -MapPath $map -CandidateSha $stale -RepositoryRoot $repositoryRoot -RunMetadataResolver $runMetadataResolver } `
        -ExpectedPattern 'exact candidate-SHA' `
        -FailureMessage 'Acceptance map lint self-test accepted candidate-SHA drift.'

    $module = Import-Module (Join-Path $scriptDirectory 'private\acceptance-map.psm1') -Force -PassThru
    $exportedNames = @(Get-Command -Module $module.Name | Select-Object -ExpandProperty Name)
    if ($exportedNames -contains 'Invoke-AcceptanceMapGenerationCore' -or $exportedNames -contains 'Invoke-AcceptanceMapLintCore') {
        throw 'The private acceptance-map module exported its test-only implementation seam.'
    }
    Remove-Module $module.Name -Force

    $fakeGhDirectory = Join-Path $temp 'fake-gh'
    New-Item -ItemType Directory -Path $fakeGhDirectory | Out-Null
    if ($PSVersionTable.Platform -eq 'Win32NT') {
        $fakeGhPath = Join-Path $fakeGhDirectory 'gh.cmd'
        $fakeGhBody = "@echo off`r`necho {`"headSha`":`"$candidate`",`"status`":`"completed`",`"conclusion`":`"success`",`"event`":`"push`"}`r`n"
        [IO.File]::WriteAllText($fakeGhPath, $fakeGhBody, [Text.Encoding]::ASCII)
    }
    else {
        $fakeGhPath = Join-Path $fakeGhDirectory 'gh'
        $fakeGhBody = @"
#!/bin/sh
printf '%s\n' '{"headSha":"$candidate","status":"completed","conclusion":"success","event":"push"}'
"@
        [IO.File]::WriteAllText($fakeGhPath, $fakeGhBody, [Text.Encoding]::ASCII)
        [IO.File]::SetUnixFileMode($fakeGhPath, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
    }
    $originalPath = $env:PATH
    $env:PATH = "$fakeGhDirectory$([IO.Path]::PathSeparator)$originalPath"
    function gh { throw 'caller-defined gh function was invoked' }
    try {
        Invoke-AcceptanceMapGenerationCore -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath (Join-Path $temp 'application-resolved-map.md')
        Invoke-AcceptanceMapLintCore -ChecklistPath $checklist -MapPath (Join-Path $temp 'application-resolved-map.md') -CandidateSha $candidate -RepositoryRoot $repositoryRoot
    }
    finally {
        $env:PATH = $originalPath
        Remove-Item Function:gh -ErrorAction SilentlyContinue
    }

    Write-Output 'ACCEPTANCE_MAP_SELF_TEST=PASS generated=4 linted=3 rejected_metadata_override=1 rejected_internal_resolver=1 rejected_forged_embedded_run=1 rejected_forged_run_id=1 rejected_stale_run_end_to_end=1 rejected_path_only=1 rejected_judgement_without_artifact=1 rejected_generic=1 rejected_text_drift=1 rejected_candidate_sha=1 rejected_function_gh=1 private_exports=1 deterministic=1 checker=1 judgement_marker=1 fixture_golden=1'
    exit 0
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
