$ErrorActionPreference = 'Stop'
$scriptDirectory = $PSScriptRoot
$repositoryRoot = Split-Path -Parent $scriptDirectory
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-acceptance-map-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

$originalPath = $env:PATH
$fakeGhDirectory = Join-Path $temp 'fake-gh'
New-Item -ItemType Directory -Path $fakeGhDirectory | Out-Null

try {
    $checklist = Join-Path $temp 'checklist.md'
    $evidence = Join-Path $temp 'evidence.json'
    $staleEvidence = Join-Path $temp 'stale-evidence.json'
    $runMetadata = Join-Path $temp 'run-metadata.json'
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
    [IO.File]::WriteAllText((Join-Path $fakeGhDirectory 'gh.cmd'), @"
@echo off
if "%~3"=="36196565020" (
  echo {"headSha":"$stale","status":"completed","conclusion":"success","event":"push"}
  exit /b 0
)
echo {"headSha":"$candidate","status":"completed","conclusion":"success","event":"push"}
exit /b 0
"@, [Text.UTF8Encoding]::new($false))
    $env:PATH = "$fakeGhDirectory;$originalPath"

    [IO.File]::WriteAllText($checklist, "* [ ] First criterion`n* [ ] Second criterion`n* [ ] Third criterion`n* [ ] Fourth criterion`n* [ ] Fifth criterion`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($runMetadata, (@(
        @{ run_id = '123456789'; headSha = $candidate; status = 'completed'; conclusion = 'success'; event = 'push' }
    ) | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
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

    & pwsh -NoProfile -File $generator -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath $map
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance map self-test generation failed.' }
    & pwsh -NoProfile -File $generator -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath $mapAgain
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance map deterministic regeneration failed.' }
    if (-not ([Linq.Enumerable]::SequenceEqual([IO.File]::ReadAllBytes($map), [IO.File]::ReadAllBytes($mapAgain)))) {
        throw 'Acceptance map generator was not byte-identical for the same inputs.'
    }

    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $map -CandidateSha $candidate -RepositoryRoot $repositoryRoot
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance map self-test lint failed.' }

    $metadataOverrideOutput = @(& pwsh -NoProfile -File $generator -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath (Join-Path $temp 'metadata-override-map.md') -RunMetadataPath $runMetadata 2>&1)
    $metadataOverrideExit = $LASTEXITCODE
    if ($metadataOverrideExit -eq 0 -or ($metadataOverrideOutput -join "`n") -notmatch 'RunMetadataPath|parameter cannot be found|named parameter') {
        throw 'Acceptance map generator self-test still exposed a run metadata override.'
    }

    $tampered = Get-Content -Raw -LiteralPath $map
    $tampered = $tampered.Replace("github_run_head_sha=$candidate", "github_run_head_sha=$stale")
    [IO.File]::WriteAllText($staleRunMap, $tampered, [Text.UTF8Encoding]::new($false))
    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $staleRunMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted forged embedded run metadata.' }

    $forgedRun = (Get-Content -Raw -LiteralPath $map).Replace('github_run_id=123456789', 'github_run_id=36196565020')
    [IO.File]::WriteAllText($forgedRunMap, $forgedRun, [Text.UTF8Encoding]::new($false))
    $forgedRunOutput = @(& pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $forgedRunMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot 2>&1)
    if ($LASTEXITCODE -eq 0 -or ($forgedRunOutput -join "`n") -notmatch 'resolved head SHA is not the candidate|independently validate') {
        throw 'Acceptance map lint self-test accepted a stale run id paired with a forged candidate head SHA.'
    }

    $staleGenerationOutput = @(& pwsh -NoProfile -File $generator -ChecklistPath $checklist -EvidencePath $staleEvidence -CandidateSha $candidate -OutputPath (Join-Path $temp 'stale-generated-map.md') 2>&1)
    if ($LASTEXITCODE -eq 0 -or ($staleGenerationOutput -join "`n") -notmatch 'not candidate-bound') {
        throw 'Acceptance map generator self-test accepted a stale run id end-to-end.'
    }

    $pathOnly = (Get-Content -Raw -LiteralPath $map).Replace('repo_path=README.md; command=pwsh -NoProfile -File ./scripts/test-acceptance-map.ps1; output=ACCEPTANCE_MAP_SELF_TEST=PASS', 'repo_path=LICENSE')
    [IO.File]::WriteAllText($pathOnlyMap, $pathOnly, [Text.UTF8Encoding]::new($false))
    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $pathOnlyMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted an existing but irrelevant path anchor.' }

    $judgementWithoutArtifact = (Get-Content -Raw -LiteralPath $map).Replace('judgement=artifacts=README.md', 'judgement=reviewer judgment without an artifact reference')
    [IO.File]::WriteAllText($judgementWithoutArtifactMap, $judgementWithoutArtifact, [Text.UTF8Encoding]::new($false))
    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $judgementWithoutArtifactMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted a judgement without a criterion-specific artifact.' }

    $generic = $pathOnly.Replace('repo_path=LICENSE', 'generic evidence')
    [IO.File]::WriteAllText($genericMap, $generic, [Text.UTF8Encoding]::new($false))
    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $genericMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted generic proof text.' }

    $textDrift = (Get-Content -Raw -LiteralPath $map).Replace('Second criterion', 'Tampered criterion')
    [IO.File]::WriteAllText($tamperedTextMap, $textDrift, [Text.UTF8Encoding]::new($false))
    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $tamperedTextMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted criterion-text drift.' }

    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $map -CandidateSha $stale -RepositoryRoot $repositoryRoot 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted candidate-SHA drift.' }

    Write-Output 'ACCEPTANCE_MAP_SELF_TEST=PASS generated=3 linted=2 rejected_metadata_override=1 rejected_forged_embedded_run=1 rejected_forged_run_id=1 rejected_stale_run_end_to_end=1 rejected_path_only=1 rejected_judgement_without_artifact=1 rejected_generic=1 rejected_text_drift=1 rejected_candidate_sha=1 deterministic=1 checker=1 judgement_marker=1 fixture_golden=1'
}
finally {
    $env:PATH = $originalPath
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
