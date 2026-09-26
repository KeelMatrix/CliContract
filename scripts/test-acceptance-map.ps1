$ErrorActionPreference = 'Stop'
$scriptDirectory = $PSScriptRoot
$repositoryRoot = Split-Path -Parent $scriptDirectory
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-acceptance-map-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $checklist = Join-Path $temp 'checklist.md'
    $evidence = Join-Path $temp 'evidence.json'
    $runMetadata = Join-Path $temp 'run-metadata.json'
    $map = Join-Path $temp 'map.md'
    $mapAgain = Join-Path $temp 'map-again.md'
    $staleRunMap = Join-Path $temp 'stale-run-map.md'
    $genericMap = Join-Path $temp 'generic-map.md'
    $tamperedTextMap = Join-Path $temp 'tampered-text-map.md'

    $candidate = '0123456789abcdef0123456789abcdef01234567'
    $stale = 'fedcba9876543210fedcba9876543210fedcba98'
    [IO.File]::WriteAllText($checklist, "* [ ] First criterion`n* [ ] Second criterion`n* [ ] Third criterion`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($runMetadata, (@(
        @{ run_id = '123456789'; headSha = $candidate; status = 'completed'; conclusion = 'success'; event = 'push' }
    ) | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($evidence, (@(
        @{ criterion = 'First criterion'; status = 'MET'; evidence = 'repo_path=README.md' },
        @{ criterion = 'Second criterion'; status = 'MET'; evidence = 'GitHub Actions run 123456789; criterion_value=completed candidate validation' },
        @{ criterion = 'Third criterion'; status = 'N/A'; evidence = 'only applies to multi-package repositories' }
    ) | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

    $generator = Join-Path $scriptDirectory 'generate-acceptance-map.ps1'
    $linter = Join-Path $scriptDirectory 'lint-acceptance-map.ps1'
    & pwsh -NoProfile -File $generator -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath $map -RunMetadataPath $runMetadata
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance map generator self-test generation failed.' }
    & pwsh -NoProfile -File $generator -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha $candidate -OutputPath $mapAgain -RunMetadataPath $runMetadata
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance map generator deterministic regeneration failed.' }
    if (-not ([Linq.Enumerable]::SequenceEqual([IO.File]::ReadAllBytes($map), [IO.File]::ReadAllBytes($mapAgain)))) {
        throw 'Acceptance map generator was not byte-identical for the same inputs.'
    }

    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $map -CandidateSha $candidate -RepositoryRoot $repositoryRoot
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance map generator self-test lint failed.' }

    $tampered = Get-Content -Raw -LiteralPath $map
    $tampered = $tampered.Replace("github_run_head_sha=$candidate", "github_run_head_sha=$stale")
    [IO.File]::WriteAllText($staleRunMap, $tampered, [Text.UTF8Encoding]::new($false))
    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $staleRunMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted a stale run head SHA.' }

    $generic = $tampered.Replace('repo_path=README.md; command=pwsh -NoProfile -File ./scripts/test-acceptance-map.ps1; output=ACCEPTANCE_MAP_SELF_TEST=PASS', 'generic evidence')
    [IO.File]::WriteAllText($genericMap, $generic, [Text.UTF8Encoding]::new($false))
    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $genericMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted generic proof text.' }

    $textDrift = $tampered.Replace('Second criterion', 'Tampered criterion')
    [IO.File]::WriteAllText($tamperedTextMap, $textDrift, [Text.UTF8Encoding]::new($false))
    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $tamperedTextMap -CandidateSha $candidate -RepositoryRoot $repositoryRoot 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted criterion-text drift.' }

    & pwsh -NoProfile -File $linter -ChecklistPath $checklist -MapPath $map -CandidateSha $stale -RepositoryRoot $repositoryRoot 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted candidate-SHA drift.' }

    Write-Output 'ACCEPTANCE_MAP_SELF_TEST=PASS generated=2 linted=1 rejected_stale_run=1 rejected_generic=1 rejected_text_drift=1 rejected_candidate_sha=1 deterministic=1'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
