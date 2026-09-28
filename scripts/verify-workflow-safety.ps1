param(
    [string] $RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

function Get-LeadingWhitespaceLength {
    param([string] $Line)
    return ([regex]::Match($Line, '^\s*').Value.Length)
}

function Get-RunExpressionViolations {
    param([Parameter(Mandatory = $true)] [string] $Path)
    $lines = @(Get-Content -LiteralPath $Path)
    $runIndent = $null
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = [string] $lines[$index]
        if ($null -ne $runIndent) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $indent = Get-LeadingWhitespaceLength -Line $line
                if ($indent -le $runIndent) { $runIndent = $null }
                else {
                    $expression = [regex]::Match($line, '\$\{\{.*?\}\}')
                    if ($expression.Success) { [pscustomobject]@{ Path = $Path; Line = $index + 1; Expression = $expression.Value } }
                    continue
                }
            }
            else { continue }
        }
        $run = [regex]::Match($line, '^(?<indent>\s*)(?:-\s+)?run:\s*(?<value>.*)$')
        if (-not $run.Success) { continue }
        $expression = [regex]::Match($run.Groups['value'].Value, '\$\{\{.*?\}\}')
        if ($expression.Success) { [pscustomobject]@{ Path = $Path; Line = $index + 1; Expression = $expression.Value } }
        if ($run.Groups['value'].Value -match '^\s*[|>]\s*[-+]?\s*$') { $runIndent = $run.Groups['indent'].Value.Length }
    }
}

function Assert-ReleaseWorkflow {
    param([Parameter(Mandatory = $true)] [string] $Path)
    $text = Get-Content -Raw -LiteralPath $Path
    if ($text -notmatch '(?ms)^on:\s*\r?\n\s+push:\s*\r?\n\s+tags:\s*\r?\n\s+-\s+[''\"]v\*\.\*\.\*[''\"]') { throw 'Release workflow must trigger only from v*.*.* tags.' }
    if ($text -match '(?m)^\s+branches:|(?m)^\s+workflow_dispatch:') { throw 'Release workflow has an unapproved trigger.' }
    if ($text -notmatch 'NuGet/login@v1') { throw 'Release workflow must use NuGet/login@v1.' }
    if ($text -notmatch '(?m)^\s+user:\s+dmitriyzen\s*$') { throw 'Release workflow NuGet username must be exactly dmitriyzen.' }
    if ($text -match '(?i)secrets\.[^\s}]*nuget|NUGET_API_KEY\s*:\s*\$\{\{\s*secrets') { throw 'Release workflow must not use a long-lived NuGet secret.' }
    $allowedCredential = 'steps.nuget-login.outputs.NUGET_API_KEY'
    foreach ($match in [regex]::Matches($text, '(?i)NUGET_API_KEY')) {
        $contextStart = [Math]::Max(0, $match.Index - 80)
        $context = $text.Substring($contextStart, [Math]::Min(160, $text.Length - $contextStart))
        if ($context -notmatch [regex]::Escape($allowedCredential)) { throw 'NuGet login output is referenced outside the OIDC credential handoff.' }
    }
    if ($text -match '(?im)^\s*(?:if:\s*always\(\)|continue-on-error:\s*true|.*\|\|\s*true)') { throw 'Release workflow contains a fail-open or failure-ordering bypass.' }
    if ($text -notmatch '(?m)^\s+id-token:\s+write\s*$') { throw 'Publishing job must request id-token: write.' }
    if ($text -notmatch '(?m)^\s+needs:\s+prepare\s*$') { throw 'Publishing job must depend on the validated prepare job.' }
    $publishIndex = $text.IndexOf('name: Publish validated package pair', [StringComparison]::Ordinal)
    $releaseIndex = $text.IndexOf('name: Create GitHub Release', [StringComparison]::Ordinal)
    $publishAfterRelease = if ($releaseIndex -ge 0) { $text.IndexOf('name: Publish validated package pair', $releaseIndex, [StringComparison]::Ordinal) } else { -1 }
    if ($publishIndex -lt 0 -or $releaseIndex -lt 0 -or $releaseIndex -le $publishIndex -or $publishAfterRelease -ge 0) { throw 'GitHub Release creation must follow package publication.' }
    $prepareIndex = $text.IndexOf('name: Build and validate exact release pair', [StringComparison]::Ordinal)
    if ($prepareIndex -lt 0 -or $prepareIndex -ge $publishIndex) { throw 'Artifact validation must precede publication.' }
    if ($text -notmatch 'actions/upload-artifact@v4' -or $text -notmatch 'actions/download-artifact@v4') { throw 'The validated artifact pair must cross the job boundary explicitly.' }
}

function Assert-WorkflowSet {
    param([string] $Root)
    $workflowDirectory = Join-Path $Root '.github/workflows'
    if (-not (Test-Path -LiteralPath $workflowDirectory -PathType Container)) { throw "Workflow directory is missing: $workflowDirectory" }
    $workflowFiles = @(Get-ChildItem -LiteralPath $workflowDirectory -Recurse -File | Where-Object { $_.Extension -in @('.yml', '.yaml') })
    if ($workflowFiles.Count -eq 0) { throw 'No workflow files were found.' }
    $violations = @($workflowFiles | ForEach-Object { Get-RunExpressionViolations -Path $_.FullName })
    if ($violations.Count -gt 0) {
        $details = $violations | ForEach-Object { "$($_.Path):$($_.Line) contains $($_.Expression) inside run" }
        throw "Workflow run blocks must not interpolate expressions into script source:`n$($details -join "`n")"
    }
    foreach ($workflow in $workflowFiles) {
        $text = Get-Content -Raw -LiteralPath $workflow.FullName
        if ($text -notmatch '(?m)^permissions:\s*$') { throw "Workflow does not declare explicit permissions: $($workflow.Name)" }
        if ($text -notmatch '(?m)^\s+timeout-minutes:\s+\d+') { throw "Workflow does not declare a job timeout: $($workflow.Name)" }
        foreach ($action in [regex]::Matches($text, '(?m)^\s+uses:\s+([^\s]+)$')) {
            if ($action.Groups[1].Value -notmatch '@v\d+(?:\.\d+){0,2}$') { throw "Workflow action is not explicitly versioned: $($action.Groups[1].Value)" }
        }
    }
    Assert-ReleaseWorkflow -Path (Join-Path $workflowDirectory 'release.yml')
    Write-Output "WORKFLOW_SOURCE_BOUNDARIES=PASS files=$($workflowFiles.Count)"
}

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-workflow-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path (Join-Path $selfTestRoot '.github/workflows') -Force | Out-Null
        $releasePath = Join-Path $selfTestRoot '.github/workflows/release.yml'
        Copy-Item -LiteralPath (Join-Path $RepositoryRoot '.github/workflows/release.yml') -Destination $releasePath
        foreach ($case in @(
            @{ Name = 'wrong-username'; Old = 'user: dmitriyzen'; New = 'user: someone-else' },
            @{ Name = 'long-lived-key'; Old = 'RELEASE_NUGET_CREDENTIAL: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}'; New = 'RELEASE_NUGET_CREDENTIAL: ${{ secrets.NUGET_API_KEY }}' },
            @{ Name = 'missing-login'; Old = 'uses: NuGet/login@v1'; New = 'uses: actions/checkout@v4.2.2' },
            @{ Name = 'skipped-dependency'; Old = 'needs: prepare'; New = 'needs: other-job' },
            @{ Name = 'release-before-publish'; Old = 'name: Create GitHub Release'; New = "name: Create GitHub Release`n      - name: Publish validated package pair" }
        )) {
            $casePath = Join-Path $selfTestRoot ".github/workflows/$($case.Name).yml"
            $content = (Get-Content -Raw -LiteralPath $releasePath).Replace($case.Old, $case.New)
            Set-Content -LiteralPath $casePath -Value $content -Encoding utf8NoBOM
            try { Assert-ReleaseWorkflow -Path $casePath; throw "Workflow self-test accepted $($case.Name)." }
            catch { if ($_.Exception.Message -eq "Workflow self-test accepted $($case.Name).") { throw } }
            Write-Output "WORKFLOW_NEGATIVE_SELF_TEST=$($case.Name) PASS"
        }
    }
    finally { if (Test-Path -LiteralPath $selfTestRoot) { Remove-Item -LiteralPath $selfTestRoot -Recurse -Force } }
    Write-Output 'WORKFLOW_SELF_TEST=PASS'
    exit 0
}

Assert-WorkflowSet -Root (Resolve-Path -LiteralPath $RepositoryRoot).Path
