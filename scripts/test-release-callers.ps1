param([string] $ScratchDirectory = [IO.Path]::GetTempPath())

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = Split-Path -Parent $PSScriptRoot
$scratchBoundary = [IO.Path]::GetFullPath($ScratchDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$temp = Join-Path $scratchBoundary ('clicontract-release-' + [Guid]::NewGuid().ToString('N'))
$commit = '0123456789012345678901234567890123456789'
$workflow = Get-Content -Raw -LiteralPath (Join-Path $root '.github/workflows/release.yml')
$caseCount = 0

function Get-StepRun([string] $Name) {
    $pattern = '(?ms)^      - name: ' + [regex]::Escape($Name) + '\r?\n(?<step>.*?)(?=^      - name:|^  [a-z]+:|\z)'
    $step = [regex]::Match($workflow, $pattern).Groups['step'].Value
    $run = [regex]::Match($step, '(?ms)^        run: (?<inline>[^\r\n]+)\r?\n?(?<body>.*)\z')
    if (-not $run.Success) { throw "Missing workflow run block: $Name" }
    if ($run.Groups['inline'].Value -eq '|') {
        return (($run.Groups['body'].Value -split "`n" | ForEach-Object { $_ -replace '^          ', '' }) -join "`n").TrimEnd()
    }
    return $run.Groups['inline'].Value
}

function Write-Fixture([string] $Path, [string] $Text) {
    $target = Join-Path $temp $Path
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.File]::WriteAllText($target, $Text)
}

function Invoke-Case([string] $Name, [string] $Body, [string] $State, [bool] $Success, [string] $ExpectedLog = '', [string] $ExpectedError = '') {
    $initial = switch ($State) {
        'unset' { 'Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue' }
        'zero' { '$global:LASTEXITCODE = 0' }
        'nonzero' { '$global:LASTEXITCODE = 19' }
        default { throw "Unknown native status: $State" }
    }
    $logPath = Join-Path $temp 'calls.txt'
    [IO.File]::WriteAllText($logPath, '')
    Write-Fixture 'case.ps1' (@'
$ErrorActionPreference = 'Stop'
. ./build/Invoke-NestedPwsh.ps1
function dotnet {
    [IO.File]::AppendAllText((Join-Path $PWD 'calls.txt'), "push:$([IO.Path]::GetExtension($args[2]))`n")
    if ($args -cnotcontains '--force-english-output' -or $args -cnotcontains 'https://api.nuget.org/v3/index.json') { throw 'Missing explicit v3 source or invariant receipts.' }
    $global:LASTEXITCODE = if ($env:FAIL_PUSH -eq [IO.Path]::GetExtension($args[2])) { 23 } else { 0 }
    if ($env:NO_RECEIPT -ne [IO.Path]::GetExtension($args[2])) {
        '  PUT https://local.invalid/upload/'
        '  Created https://local.invalid/upload/ 1ms'
        'Your package was pushed.'
    }
}
'@ + "`n$initial`n$Body`n" + @'
Write-Output 'ISOLATED_CASE_COMPLETE'
if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }
'@)
    $started = [Diagnostics.Stopwatch]::StartNew()
    Push-Location $temp
    try {
        $output = @(Invoke-NestedPwsh -NoProfile -File ./case.ps1 2>&1)
        $code = $LASTEXITCODE
    }
    finally { Pop-Location }
    $calls = [IO.File]::ReadAllText($logPath).Replace("`r", '').TrimEnd()
    if (($Success -and ($code -ne 0 -or $output -cnotcontains 'ISOLATED_CASE_COMPLETE')) -or
        (-not $Success -and ($code -eq 0 -or ($output -join "`n") -match 'RELEASE_PUBLICATION=PASS|ISOLATED_CASE_COMPLETE')) -or $calls -cne $ExpectedLog -or
        ($ExpectedError -and ($output -join "`n") -notlike "*$ExpectedError*")) {
        throw "Release case $Name failed: exit=$code calls=[$calls] output=$($output -join "`n")"
    }
    $script:caseCount++
    Write-Output "RELEASE_CALLER_CASE=PASS name=$Name state=$State exit=$code duration_ms=$($started.ElapsedMilliseconds)"
}

try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    foreach ($path in @('build/Invoke-NestedPwsh.ps1', 'scripts/verify-release-tag.ps1', 'scripts/publish-release.ps1', 'scripts/local-gate.ps1')) {
        Write-Fixture $path (Get-Content -Raw -LiteralPath (Join-Path $root $path))
    }
    $tagRun = Get-StepRun 'Validate release tag'
    $prepareRun = Get-StepRun 'Build and validate exact release pair'
    $publishRun = Get-StepRun 'Publish validated package pair'
    $outputPath = Join-Path $temp 'github-output.txt'
    $setup = @'
$env:RELEASE_TAG = 'v0.1.0'
$env:RELEASE_VERSION = '0.1.0'
$env:RELEASE_COMMIT = '0123456789012345678901234567890123456789'
$env:RELEASE_NUGET_CREDENTIAL = 'isolated-test-value'
$env:FAIL_PUSH = ''
$env:NO_RECEIPT = ''
$env:FAIL_STAGE = ''
$env:FAIL_MODE = ''
'@
    $versionSetup = '$env:TAG = ''v0.1.0''; $env:GITHUB_OUTPUT = Join-Path $PWD ''github-output.txt''' + "`n"
    $outputAssertion = @'
if (-not (Test-Path -LiteralPath $env:GITHUB_OUTPUT -PathType Leaf)) { throw 'Missing version output.' }
$bindings = @(Get-Content -LiteralPath $env:GITHUB_OUTPUT)
if ($bindings.Count -ne 1 -or $bindings[0] -cne 'version=0.1.0') { throw 'Invalid version output.' }
'@
    foreach ($state in @('unset', 'zero', 'nonzero')) {
        [IO.File]::WriteAllText($outputPath, '')
        Invoke-Case "version-success-$state" ($versionSetup + $tagRun + "`n" + $outputAssertion) $state $true
        if ([IO.File]::ReadAllText($outputPath).Trim() -cne 'version=0.1.0') { throw 'Successful caller omitted the version output.' }
        $alternate = $versionSetup + '$env:TAG = ''v12.34.567''; $env:GITHUB_OUTPUT = Join-Path $PWD ''new [output].txt''' + "`n" + $tagRun
        $alternatePath = Join-Path $temp 'new [output].txt'
        if (Test-Path -LiteralPath $alternatePath) { Remove-Item -LiteralPath $alternatePath }
        Invoke-Case "alternate-version-new-file-$state" $alternate $state $true
        if ([IO.File]::ReadAllText($alternatePath).Trim() -cne 'version=12.34.567') { throw 'Alternate tag version was not written literally.' }
        foreach ($tag in @('0.1.0', 'v1.2', 'v1.2.3-beta', 'v1.2.3+meta', 'v1.2.3.4', "v1.2.3`n")) {
            [IO.File]::WriteAllText($outputPath, '')
            $tagLiteral = "'" + $tag.Replace("'", "''") + "'"
            Invoke-Case "invalid-tag-$tag" ($versionSetup + '$env:TAG = ' + $tagLiteral + "`n" + $tagRun) $state $false '' 'Unsupported release tag'
            if ((Get-Item -LiteralPath $outputPath).Length -ne 0) { throw 'Invalid tag wrote a version.' }
        }
        foreach ($binding in @('$null', "''", "' '", '(Join-Path $PWD ''absent/output.txt'')', '$PWD.Path')) {
            Invoke-Case "invalid-output-$binding" ($versionSetup + '$env:GITHUB_OUTPUT = ' + $binding + "`n" + $tagRun) $state $false
        }
    }

    # Expensive validators are replaced only in the disposable fixture. The actual
    # workflow blocks, publication helper and process launcher execute unchanged.
    $phaseStub = @'
param([string] $Version, [string] $Tag, [string] $ExpectedCommit, [string] $ArtifactDirectory, [switch] $RequireFinalizedChangelog)
$ErrorActionPreference = 'Stop'
$stage = [IO.Path]::GetFileNameWithoutExtension($PSCommandPath)
[IO.File]::AppendAllText((Join-Path $PWD 'calls.txt'), "$stage`n")
if ($env:FAIL_STAGE -eq $stage) {
    switch ($env:FAIL_MODE) {
        'throw' { throw 'ISOLATED_PHASE_FAILURE' }
        'error' { Write-Error 'ISOLATED_PHASE_FAILURE' }
        'exit' { exit 23 }
        'native' {
            . ./build/Invoke-NestedPwsh.ps1
            Invoke-NestedPwsh -NoProfile -Command 'exit 23'
            if ($LASTEXITCODE -ne 0) { throw 'ISOLATED_NATIVE_FAILURE' }
        }
    }
}
if ($Version -cne '0.1.0' -or ($Tag -and $Tag -cne "v$Version") -or ($ExpectedCommit -and $ExpectedCommit -cne '0123456789012345678901234567890123456789')) { throw 'Invalid phase identity.' }
if ($stage -eq 'local-gate') {
    if (-not $RequireFinalizedChangelog) { throw 'Finalized changelog gate was omitted.' }
    New-Item -ItemType Directory -Path ./artifacts/packages -Force | Out-Null
    foreach ($extension in @('nupkg', 'snupkg')) { [IO.File]::WriteAllText((Join-Path $PWD "artifacts/packages/KeelMatrix.CliContract.$Version.$extension"), 'isolated artifact') }
}
'@
    foreach ($path in @('scripts/local-gate.ps1', 'scripts/verify-release-contract.ps1', 'scripts/verify-release-artifacts.ps1')) { Write-Fixture $path $phaseStub }
    foreach ($state in @('unset', 'zero', 'nonzero')) {
        [IO.File]::WriteAllText($outputPath, '')
        # This is the step-output -> prepare environment -> job-output -> publish
        # environment handoff. The workflow binding names are guarded separately.
        $bindVersion = '$env:RELEASE_VERSION = (Get-Content -Raw $env:GITHUB_OUTPUT).Trim().Substring(''version=''.Length)' + "`n"
        Invoke-Case "prepare-publish-$state" ($setup + "`n" + $versionSetup + $tagRun + "`n" + $outputAssertion + "`n" + $bindVersion + $prepareRun + "`n" + $publishRun) $state $true "local-gate`nverify-release-contract`nverify-release-artifacts`npush:.nupkg`npush:.snupkg"
        foreach ($failure in @(
            @{ Stage = 'local-gate'; Run = $prepareRun; Calls = 'local-gate' },
            @{ Stage = 'verify-release-contract'; Run = $publishRun; Calls = 'verify-release-contract' },
            @{ Stage = 'verify-release-artifacts'; Run = $publishRun; Calls = "verify-release-contract`nverify-release-artifacts" }
        )) {
            foreach ($mode in @('throw', 'error', 'exit', 'native')) {
                $failSetup = '$env:FAIL_STAGE = ''' + $failure.Stage + "'`n" + '$env:FAIL_MODE = ''' + $mode + "'`n"
                Invoke-Case "$($failure.Stage)-$mode-$state" ($setup + "`n" + $failSetup + $failure.Run) $state $false $failure.Calls
            }
        }
        foreach ($missing in @('$null', "''", "' '")) {
            Invoke-Case "missing-version-$missing-$state" ($setup + "`n" + '$env:RELEASE_VERSION = ' + $missing + "`n" + $publishRun) $state $false
        }
        Invoke-Case "tag-mismatch-$state" ($setup + "`n" + '$env:RELEASE_TAG = ''v0.1.1''' + "`n" + $publishRun) $state $false '' 'does not match version'
        Invoke-Case "commit-mismatch-$state" ($setup + "`n" + '$env:RELEASE_COMMIT = ''short''' + "`n" + $publishRun) $state $false '' '40-character'
        Invoke-Case "missing-credential-$state" ($setup + "`n" + '$env:RELEASE_NUGET_CREDENTIAL = ''''' + "`n" + $publishRun) $state $false "verify-release-contract`nverify-release-artifacts" 'OIDC login output'
        Invoke-Case "package-push-failure-$state" ($setup + "`n" + '$env:FAIL_PUSH = ''.nupkg''' + "`n" + $publishRun) $state $false "verify-release-contract`nverify-release-artifacts`npush:.nupkg" 'NuGet package publication failed'
        Invoke-Case "symbol-push-failure-$state" ($setup + "`n" + '$env:FAIL_PUSH = ''.snupkg''' + "`n" + $publishRun) $state $false "verify-release-contract`nverify-release-artifacts`npush:.nupkg`npush:.snupkg" 'NuGet symbol publication failed'
        Invoke-Case "package-push-no-op-$state" ($setup + "`n" + '$env:NO_RECEIPT = ''.nupkg''' + "`n" + $publishRun) $state $false "verify-release-contract`nverify-release-artifacts`npush:.nupkg" 'no accepted upload receipt'
        Invoke-Case "symbol-push-no-op-$state" ($setup + "`n" + '$env:NO_RECEIPT = ''.snupkg''' + "`n" + $publishRun) $state $false "verify-release-contract`nverify-release-artifacts`npush:.nupkg`npush:.snupkg" 'no accepted upload receipt'
    }

    Write-Fixture 'scripts/verify-release-tag.ps1' @'
param([string] $Tag, [string] $GitHubOutputFile)
switch ($env:FAIL_MODE) {
    'throw' { throw 'ISOLATED_TAG_FAILURE' }
    'error' { Write-Error 'ISOLATED_TAG_FAILURE' -ErrorAction Stop }
    'exit' { exit 23 }
    'native' {
        . ./build/Invoke-NestedPwsh.ps1
        Invoke-NestedPwsh -NoProfile -Command 'exit 23'
        exit $LASTEXITCODE
    }
}
# The no-output control deliberately succeeds without writing a binding.
'@
    foreach ($state in @('unset', 'zero', 'nonzero')) {
        foreach ($mode in @('throw', 'error', 'exit', 'native', 'no-output')) {
            [IO.File]::WriteAllText($outputPath, '')
            $body = $setup + "`n" + $versionSetup + '$env:FAIL_MODE = ''' + $mode + "'`n" + $tagRun + "`n" +
                '$env:RELEASE_VERSION = [IO.File]::ReadAllText($env:GITHUB_OUTPUT)' + "`n" + $prepareRun
            Invoke-Case "tag-$mode-$state" $body $state $false
        }
    }

    # Exercise the real local-gate entry and the real CI run block without build,
    # test or pack effects. The first later validator is an intentional stop.
    Write-Fixture 'scripts/local-gate.ps1' (Get-Content -Raw (Join-Path $root 'scripts/local-gate.ps1'))
    Write-Fixture 'build/Test-NestedPwshLaunch.ps1' @'
param([switch] $SelfTest)
$stage = if ($SelfTest) { 'launch-self' } else { 'launch-scan' }
[IO.File]::AppendAllText((Join-Path $PWD 'calls.txt'), "$stage`n")
if ($env:FAIL_STAGE -eq $stage) { throw 'ISOLATED_LAUNCH_FAILURE' }
'@
    Write-Fixture 'scripts/verify-option-name-oracle.ps1' "throw 'ISOLATED_NEXT_VALIDATOR'"
    $buildWorkflow = Get-Content -Raw (Join-Path $root '.github/workflows/build.yml')
    $ciRun = [regex]::Match($buildWorkflow, '(?m)^        run: (?<run>\./scripts/local-gate\.ps1)\s*$').Groups['run'].Value
    if (-not $ciRun) { throw 'Missing CI local-gate caller.' }
    foreach ($state in @('unset', 'zero', 'nonzero')) {
        Invoke-Case "ci-local-gate-$state" ($setup + "`n" + $ciRun) $state $false "launch-self`nlaunch-scan" 'ISOLATED_NEXT_VALIDATOR'
        foreach ($stage in @('launch-self', 'launch-scan')) {
            $expected = if ($stage -eq 'launch-self') { 'launch-self' } else { "launch-self`nlaunch-scan" }
            Invoke-Case "ci-$stage-failure-$state" ($setup + "`n" + '$env:FAIL_STAGE = ''' + $stage + "'`n" + $ciRun) $state $false $expected 'ISOLATED_LAUNCH_FAILURE'
        }
    }
    foreach ($path in @('scripts/verify-option-name-oracle.ps1', 'scripts/verify-website-metadata.ps1')) { Write-Fixture $path '' }
    $nativeSetup = @'
function git { $global:LASTEXITCODE = 0; '0123456789012345678901234567890123456789' }
function dotnet {
    $stage = [string] $args[0]
    [IO.File]::AppendAllText((Join-Path $PWD 'calls.txt'), "$stage`n")
    $global:LASTEXITCODE = if ($env:FAIL_STAGE -eq $stage) { 23 } else { 0 }
}
'@
    $calls = "launch-self`nlaunch-scan"
    foreach ($stage in @('restore', 'format', 'build', 'test')) {
        $calls += "`n$stage"
        $body = $setup + "`n" + $nativeSetup + "`n" + '$env:FAIL_STAGE = ''' + $stage + "'`n" + $ciRun
        Invoke-Case "ci-native-$stage-failure" $body 'unset' $false $calls 'failed with exit code 23'
    }
    foreach ($name in @('contract-regressions', 'verify-determinism', 'check-no-execution', 'test-no-execution', 'scan-user-facing-surface', 'verify-error-taxonomy', 'validate-sensitive-paths', 'scan-history-wording', 'verify-release-tag', 'verify-release-contract', 'verify-workflow-safety')) {
        Write-Fixture "scripts/$name.ps1" ''
    }
    Write-Fixture 'scripts/test-release-callers.ps1' "throw 'ISOLATED_RELEASE_REGRESSION_FAILURE'"
    Invoke-Case 'ci-release-regression-failure' ($setup + "`n" + $nativeSetup + "`n" + $ciRun) 'unset' $false $calls 'Release caller regression tests failed'
    Write-Fixture 'scripts/test-release-callers.ps1' ''
    Write-Fixture 'scripts/test-release-upload.ps1' "throw 'ISOLATED_UPLOAD_REGRESSION_FAILURE'"
    Invoke-Case 'ci-upload-regression-failure' ($setup + "`n" + $nativeSetup + "`n" + $ciRun) 'unset' $false $calls 'Release upload regression tests failed'
    Write-Output "RELEASE_CALLER_TESTS=PASS cases=$caseCount"
}
finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    if (-not $resolved.StartsWith($scratchBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release test cleanup escaped the scratch directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
