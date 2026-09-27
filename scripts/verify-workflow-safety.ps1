param(
    [string] $RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
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

$workflowDirectory = Join-Path $RepositoryRoot '.github/workflows'
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

$tagScript = Join-Path $RepositoryRoot 'scripts/verify-release-tag.ps1'
$publishScript = Join-Path $RepositoryRoot 'scripts/publish-package.ps1'
if (-not (Test-Path -LiteralPath $tagScript -PathType Leaf)) { throw "Release tag validator is missing: $tagScript" }
if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) { throw "Package publisher is missing: $publishScript" }
if ((Get-Content -Raw -LiteralPath $publishScript) -match 'NUGET_API_KEY') { throw 'The package publisher must not read a long-lived NuGet API-key environment variable.' }

$hostileTag = "v1.0.0';Write-Output('VALIDATION_MARKER');#"
$rawOutput = @(& pwsh -NoProfile -WindowStyle Hidden -File $tagScript -Tag $hostileTag 2>&1)
$tagExitCode = $LASTEXITCODE
$output = @($rawOutput | ForEach-Object { $_.ToString() })
if ($tagExitCode -eq 0 -or ($output -join "`n") -notmatch 'Unsupported release tag') { throw 'The release tag validator accepted an invalid tag.' }
if ($output | Where-Object { $_ -match '^\s*VALIDATION_MARKER\s*$' }) { throw 'The release tag validator executed tag text.' }

Write-Output "WORKFLOW_SOURCE_BOUNDARIES=PASS files=$($workflowFiles.Count)"
Write-Output 'RELEASE_TAG_VALIDATION=PASS hostile_input_rejected'
