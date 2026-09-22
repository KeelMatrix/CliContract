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
                if ($indent -le $runIndent) {
                    $runIndent = $null
                }
                else {
                    $expression = [regex]::Match($line, '\$\{\{.*?\}\}')
                    if ($expression.Success) {
                        [pscustomobject] @{
                            Path       = $Path
                            Line       = $index + 1
                            Expression = $expression.Value
                        }
                    }
                    continue
                }
            }
            else {
                continue
            }
        }

        $run = [regex]::Match($line, '^(?<indent>\s*)(?:-\s+)?run:\s*(?<value>.*)$')
        if (-not $run.Success) { continue }

        $value = $run.Groups['value'].Value
        $expression = [regex]::Match($value, '\$\{\{.*?\}\}')
        if ($expression.Success) {
            [pscustomobject] @{
                Path       = $Path
                Line       = $index + 1
                Expression = $expression.Value
            }
        }

        if ($value -match '^\s*[|>]\s*[-+]?\s*$') {
            $runIndent = $run.Groups['indent'].Value.Length
        }
    }
}

function Assert-WorkflowScannerSelfTest {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-workflow-' + [Guid]::NewGuid().ToString('N'))
    $workflowPath = Join-Path $selfTestRoot '.github/workflows/self-test.yml'
    try {
        New-Item -ItemType Directory -Path (Split-Path -Parent $workflowPath) -Force | Out-Null
        @'
name: Workflow scanner self-test

concurrency:
  group: ${{ github.ref }}

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: |
          Write-Output '${{ github.ref_name }}'
'@ | Set-Content -LiteralPath $workflowPath -Encoding utf8NoBOM

        $violations = @(Get-RunExpressionViolations -Path $workflowPath)
        if ($violations.Count -ne 1 -or $violations[0].Line -ne 11) {
            throw 'Workflow scanner self-test did not identify the run-block expression.'
        }
        Write-Output 'WORKFLOW_SCANNER_SELF_TEST=PASS'
    }
    finally {
        if (Test-Path -LiteralPath $selfTestRoot) {
            Remove-Item -LiteralPath $selfTestRoot -Recurse -Force
        }
    }
}

function Assert-HostileTagRejected {
    $tagScript = Join-Path $RepositoryRoot 'scripts/verify-release-tag.ps1'
    if (-not (Test-Path -LiteralPath $tagScript)) {
        throw "Release tag validator is missing: $tagScript"
    }

    $hostile = "v1.0.0';Write-Output('REGRESSION_MARKER');#"
    $oldTag = $env:TAG_NAME
    try {
        $env:TAG_NAME = $hostile
        $output = @(& pwsh -NoProfile -File $tagScript -Tag $env:TAG_NAME 2>&1 | ForEach-Object { $_.ToString() })
        $exitCode = $LASTEXITCODE
    }
    finally {
        $env:TAG_NAME = $oldTag
    }

    if ($exitCode -eq 0) { throw 'Hostile release tag was accepted.' }
    $combinedOutput = $output -join "`n"
    if ($combinedOutput -notmatch 'Unsupported release tag') {
        throw 'Hostile release tag did not produce the expected validation failure.'
    }
    foreach ($marker in @('REGRESSION_MARKER', 'VALIDATION_REACHED')) {
        if ($output | Where-Object { $_ -match ("^\s*" + [regex]::Escape($marker) + "\s*$") }) {
            throw "Hostile release tag executed code marker: $marker"
        }
    }
    Write-Output 'HOSTILE_TAG_SELF_TEST=PASS rejected_without_execution'
}

$workflowDirectory = Join-Path $RepositoryRoot '.github/workflows'
if (-not (Test-Path -LiteralPath $workflowDirectory)) {
    throw "Workflow directory is missing: $workflowDirectory"
}

$workflowFiles = @(Get-ChildItem -LiteralPath $workflowDirectory -Recurse -File | Where-Object { $_.Extension -in @('.yml', '.yaml') })
if ($workflowFiles.Count -eq 0) { throw 'No workflow files were found.' }

$violations = @(
    foreach ($workflow in $workflowFiles) {
        Get-RunExpressionViolations -Path $workflow.FullName
    }
)
if ($violations.Count -gt 0) {
    $details = $violations | ForEach-Object { "$($_.Path):$($_.Line) contains $($_.Expression) inside run" }
    throw "Workflow run blocks must not interpolate expressions into script source:`n$($details -join "`n")"
}

Write-Output "WORKFLOW_SOURCE_BOUNDARIES=PASS files=$($workflowFiles.Count)"
Assert-HostileTagRejected
if ($SelfTest) { Assert-WorkflowScannerSelfTest }
