$ErrorActionPreference = 'Stop'
$scriptDirectory = $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-acceptance-map-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $checklist = Join-Path $temp 'checklist.md'
    $evidence = Join-Path $temp 'evidence.json'
    $map = Join-Path $temp 'map.md'
    [IO.File]::WriteAllText($checklist, "* [ ] First criterion`n* [ ] Second criterion`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($evidence, (@(
        @{ criterion = 'First criterion'; status = 'MET'; evidence = 'scripts/local-gate.ps1 output PASS' },
        @{ criterion = 'Second criterion'; status = 'N/A'; evidence = 'only applies to multi-package repositories' }
    ) | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

    & pwsh -NoProfile -File (Join-Path $scriptDirectory 'generate-acceptance-map.ps1') -ChecklistPath $checklist -EvidencePath $evidence -CandidateSha '0123456789abcdef0123456789abcdef01234567' -OutputPath $map
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance map generator self-test generation failed.' }

    & pwsh -NoProfile -File (Join-Path $scriptDirectory 'lint-acceptance-map.ps1') -ChecklistPath $checklist -MapPath $map -CandidateSha '0123456789abcdef0123456789abcdef01234567'
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance map generator self-test lint failed.' }

    $tampered = Get-Content -Raw -LiteralPath $map
    $tampered = $tampered.Replace('Second criterion', 'Tampered criterion')
    [IO.File]::WriteAllText($map, $tampered, [Text.UTF8Encoding]::new($false))
    & pwsh -NoProfile -File (Join-Path $scriptDirectory 'lint-acceptance-map.ps1') -ChecklistPath $checklist -MapPath $map -CandidateSha '0123456789abcdef0123456789abcdef01234567' 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Acceptance map lint self-test accepted criterion-text drift.' }

    Write-Output 'ACCEPTANCE_MAP_SELF_TEST=PASS generated=1 linted=1 rejected_text_drift=1'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
