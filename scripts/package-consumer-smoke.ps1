param(
    [Parameter(Mandatory = $true)] [string] $PackagePath,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$candidatePath = (Resolve-Path -LiteralPath $PackagePath).Path
$candidateHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-smoke-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $temp 'feed'
$toolPath = Join-Path $temp 'tool'
$freshPackages = Join-Path $temp 'fresh-nuget-packages'
$httpCache = Join-Path $temp 'fresh-http-cache'
$ordinaryCache = Join-Path $temp 'ordinary-global-cache'
$nugetConfig = Join-Path $temp 'NuGet.config'
$pushed = $false
$oldLocation = Get-Location
$oldNugetPackages = $env:NUGET_PACKAGES
$oldHttpCache = $env:NUGET_HTTP_CACHE_PATH
$oldFallbackPackages = $env:NUGET_FALLBACK_PACKAGES

function Add-ArchiveMarker {
    param([string] $ArchivePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry('README.md')
        if ($null -eq $entry) { throw 'The package seed requires a README.md entry.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $content = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $entry.Delete()
        $replacement = $archive.CreateEntry('README.md')
        $writer = [IO.StreamWriter]::new($replacement.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($content + "`ncache-seed") }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Write-IsolatedNuGetConfig([string] $Path, [string] $Source) {
    $escapedSource = [Security.SecurityElement]::Escape($Source)
    $content = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$escapedSource" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="candidate">
      <package pattern="KeelMatrix.CliContract" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
    [IO.File]::WriteAllText($Path, $content, [Text.UTF8Encoding]::new($false))
}

New-Item -ItemType Directory -Path $feed, $toolPath, $freshPackages, $httpCache, $ordinaryCache | Out-Null
try {
    $feedPackage = Join-Path $feed ([IO.Path]::GetFileName($candidatePath))
    Copy-Item -LiteralPath $candidatePath -Destination $feedPackage
    $feedHash = (Get-FileHash -LiteralPath $feedPackage -Algorithm SHA256).Hash
    if ($feedHash -ne $candidateHash) { throw 'The local feed copy is not byte-identical to the package under test.' }
    Write-IsolatedNuGetConfig -Path $nugetConfig -Source $feed

    $seedPath = Join-Path $ordinaryCache 'keelmatrix.clicontract/0.1.0/KeelMatrix.CliContract.0.1.0.nupkg'
    $env:NUGET_PACKAGES = $ordinaryCache
    New-Item -ItemType Directory -Path (Split-Path -Parent $seedPath) -Force | Out-Null
    Copy-Item -LiteralPath $candidatePath -Destination $seedPath
    Add-ArchiveMarker -ArchivePath $seedPath
    $seedHash = (Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash
    if ($seedHash -eq $candidateHash) { throw 'The negative cache seed was not made distinct from the candidate package.' }

    $env:NUGET_PACKAGES = $freshPackages
    $env:NUGET_HTTP_CACHE_PATH = $httpCache
    Remove-Item Env:NUGET_FALLBACK_PACKAGES -ErrorAction SilentlyContinue

    $version = '0.1.0'
    $toolName = if ($IsWindows) { 'clicontract.exe' } else { 'clicontract' }
    $tool = Join-Path $toolPath $toolName
    $install = & dotnet tool install --tool-path $toolPath --configfile $nugetConfig --no-cache --version $version KeelMatrix.CliContract 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Packed tool installation failed: $($install -join ' ')" }
    if (-not (Test-Path -LiteralPath $tool)) { throw 'Installed tool command was not produced.' }

    $installedPackageRoot = Join-Path $toolPath '.store/keelmatrix.clicontract/0.1.0/keelmatrix.clicontract/0.1.0'
    $installedPackage = Join-Path $installedPackageRoot 'keelmatrix.clicontract.0.1.0.nupkg'
    if (-not (Test-Path -LiteralPath $installedPackage)) { throw 'The isolated tool store did not retain the installed package archive.' }
    $installedHash = (Get-FileHash -LiteralPath $installedPackage -Algorithm SHA256).Hash
    if ($installedHash -ne $candidateHash) { throw 'The installed package archive is not byte-identical to the candidate artifact.' }

    Push-Location $temp
    $pushed = $true
    $help = & $tool --help 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (($help -join "`n") -match 'snapshot')) { throw 'Installed tool help failed.' }

    $officialPetstore = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\fixtures\opencli\petstore-cli.ocs.yaml')).Path
    $officialBaseline = Join-Path $temp 'official-petstore.canonical.json'
    & $tool validate $officialPetstore --input opencli --no-telemetry | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Installed tool rejected the tagged alpha.14 command-key fixture.' }
    & $tool snapshot $officialPetstore --input opencli --output $officialBaseline --no-telemetry | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Installed tool could not snapshot the tagged alpha.14 command-key fixture.' }
    & $tool check $officialPetstore --input opencli --baseline $officialBaseline --no-telemetry | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Installed tool could not check the tagged alpha.14 command-key fixture against itself.' }
    Write-Output 'CASE=official-alpha14-command-key-grammar exit=0'

    foreach ($fixture in @('valid-whitespace-source.json', 'valid-whitespace-binary.json')) {
        $fixturePath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot ('..\fixtures\opencli\' + $fixture))).Path
        $fixtureName = [IO.Path]::GetFileNameWithoutExtension($fixture)
        $fixtureBaseline = Join-Path $temp ($fixtureName + '.canonical.json')
        & $tool validate $fixturePath --input opencli --no-telemetry | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Installed tool rejected $fixtureName whitespace source fixture." }
        & $tool snapshot $fixturePath --input opencli --output $fixtureBaseline --no-telemetry | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Installed tool could not snapshot $fixtureName whitespace source fixture." }
        & $tool check $fixturePath --input opencli --baseline $fixtureBaseline --no-telemetry | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Installed tool could not check $fixtureName whitespace source fixture." }
        & $tool diff $fixturePath $fixturePath --input opencli --no-telemetry | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Installed tool could not diff $fixtureName whitespace source fixture against itself." }
    }
    Write-Output 'CASE=whitespace-source-fixtures-consumer validate_snapshot_check_diff=PASS'

    function Assert-ToolError {
        param(
            [string] $Label,
            [int] $ExpectedExit,
            [string] $ExpectedCode,
            [string[]] $Arguments
        )

        $result = @(& $tool @Arguments 2>&1)
        $actualExit = $LASTEXITCODE
        $joined = $result -join "`n"
        if ($actualExit -ne $ExpectedExit -or $joined -notmatch [Regex]::Escape($ExpectedCode)) {
            throw "Packed-tool $Label error contract failed: exit=$actualExit output=$joined"
        }
    }

    foreach ($case in @(
        @{ Name = 'invalid-empty-source-value'; File = 'invalid-empty-source-value.json'; Code = 'OPENCLI_INFO' },
        @{ Name = 'invalid-empty-alias'; File = 'invalid-empty-alias.json'; Code = 'INVALID_STRING' },
        @{ Name = 'invalid-duplicate-alias'; File = 'invalid-duplicate-alias.json'; Code = 'INVALID_BASELINE' }
    )) {
        $fixturePath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot ('..\fixtures\opencli\' + $case.File))).Path
        $fixtureBaseline = Join-Path $temp ($case.Name + '.canonical.json')
        Assert-ToolError -Label "$($case.Name) consumer validate" -ExpectedExit 3 -ExpectedCode $case.Code -Arguments @('validate', $fixturePath, '--input', 'opencli', '--no-telemetry')
        Assert-ToolError -Label "$($case.Name) consumer snapshot" -ExpectedExit 3 -ExpectedCode $case.Code -Arguments @('snapshot', $fixturePath, '--input', 'opencli', '--output', $fixtureBaseline, '--no-telemetry')
        Assert-ToolError -Label "$($case.Name) consumer check" -ExpectedExit 3 -ExpectedCode $case.Code -Arguments @('check', $fixturePath, '--input', 'opencli', '--baseline', $officialBaseline, '--no-telemetry')
        Assert-ToolError -Label "$($case.Name) consumer diff" -ExpectedExit 3 -ExpectedCode $case.Code -Arguments @('diff', $fixturePath, $fixturePath, '--input', 'opencli', '--no-telemetry')
    }
    Write-Output 'CASE=whitespace-source-negative-fixtures-consumer validate_snapshot_check_diff=PASS'

    function Assert-InformationalCase {
        param(
            [string] $Label,
            [string] $OldText,
            [string] $NewText
        )

        $oldInformational = Join-Path $temp ($Label + '-old.json')
        $newInformational = Join-Path $temp ($Label + '-new.json')
        [IO.File]::WriteAllText($oldInformational, $OldText, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($newInformational, $NewText, [Text.UTF8Encoding]::new($false))

        foreach ($threshold in @('breaking', 'warning')) {
            $arguments = @('diff', $oldInformational, $newInformational, '--format', 'json', '--no-telemetry')
            if ($threshold -eq 'warning') { $arguments += @('--fail-on', 'warning') }
            $result = @(& $tool @arguments 2>&1)
            $exit = $LASTEXITCODE
            $output = $result -join "`n"
            if ($exit -ne 0) { throw "Packed-tool $Label ($threshold) unexpectedly gated an informational finding: exit=$exit output=$output" }
            try { $document = $output | ConvertFrom-Json } catch { throw "Packed-tool $Label ($threshold) did not return a JSON envelope: $output" }
            $findings = @($document.findings)
            $matching = @($findings | Where-Object { $_.code -eq 'KMCLI005' -and $_.category -eq 'info' })
            if ($matching.Count -eq 0) { throw "Packed-tool $Label ($threshold) returned no KMCLI005 info finding: $output" }
            Write-Output "CASE=$Label fail_on=$threshold exit=$exit code=KMCLI005 category=info finding_count=$($findings.Count)"
        }
    }

    $old = Join-Path $temp 'old.json'
    $new = Join-Path $temp 'new.json'
    $baseline = Join-Path $temp 'baseline.json'
    $schema = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool":{"flags":[{"name":"region","type":"string"}]}}}'
    $changed = $schema.Replace('"region","type":"string"', '"format","type":"string"')
    [IO.File]::WriteAllText($old, $schema, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($new, $changed, [Text.UTF8Encoding]::new($false))
    & $tool snapshot $old --output $baseline --no-telemetry | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Snapshot from the packed tool failed.' }
    & $tool check $old --baseline $baseline --no-telemetry | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Compatible packed-tool check did not return zero.' }
    $breaking = & $tool diff $old $new --format text --no-telemetry 2>&1
    $breakingExit = $LASTEXITCODE
    $breaking | Out-Host
    if ($breakingExit -ne 1 -or -not (($breaking -join "`n") -match 'KMCLI101')) { throw 'Removed-option packed-tool diff did not return stable exit 1/KMCLI101.' }

    $hostileSource = Join-Path $temp 'hostile-source.json'
    $hostileBaseline = Join-Path $temp 'hostile-baseline.json'
    $hostileCanonical = Join-Path $temp 'hostile-canonical.json'
    $hostileSourceText = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"global":{"config":{"json":"config.json"},"flags":[{"name":"value","type":"string","alternativeSources":[{"type":"$ENV","property":"VALUE"},{"type":"$FILE","property":"$.value"}]}]},"commands":{"tool":{},"tool run <target>":{"args":[{"name":"target","type":"string"}]}}}'
    [IO.File]::WriteAllText($hostileSource, $hostileSourceText, [Text.UTF8Encoding]::new($false))
    & $tool snapshot $hostileSource --output $hostileBaseline --no-telemetry | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Packed tool could not create the hostile-baseline seed.' }
    $hostileDocument = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($hostileBaseline))
    $hostileSources = $hostileDocument['GlobalConfig']['FileSources'].AsArray()
    $hostileSources.Add($hostileSources[0].DeepClone())
    [IO.File]::WriteAllText($hostileCanonical, $hostileDocument.ToJsonString(), [Text.UTF8Encoding]::new($false))
    Assert-ToolError -Label 'hostile canonical baseline check' -ExpectedExit 3 -ExpectedCode 'INVALID_BASELINE' -Arguments @('check', $hostileSource, '--baseline', $hostileCanonical, '--no-telemetry')
    Assert-ToolError -Label 'hostile canonical baseline diff' -ExpectedExit 3 -ExpectedCode 'INVALID_BASELINE' -Arguments @('diff', $hostileCanonical, $hostileBaseline, '--no-telemetry')
    Write-Output 'CASE=hostile-canonical-baseline-consumer check_exit=3 diff_exit=3'

    $hostileContractCases = @(
        @{ Name = 'contact-empty'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Contact'] = [System.Text.Json.Nodes.JsonNode]::Parse('{}') } },
        @{ Name = 'contact-name-null'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Contact'] = [System.Text.Json.Nodes.JsonNode]::Parse('{"Name":null}') } },
        @{ Name = 'contact-email-null'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Contact'] = [System.Text.Json.Nodes.JsonNode]::Parse('{"Email":null}') } },
        @{ Name = 'contact-url-null'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Contact'] = [System.Text.Json.Nodes.JsonNode]::Parse('{"Url":null}') } },
        @{ Name = 'contact-name-email-null'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Contact'] = [System.Text.Json.Nodes.JsonNode]::Parse('{"Name":null,"Email":null}') } },
        @{ Name = 'contact-name-url-null'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Contact'] = [System.Text.Json.Nodes.JsonNode]::Parse('{"Name":null,"Url":null}') } },
        @{ Name = 'contact-email-url-null'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Contact'] = [System.Text.Json.Nodes.JsonNode]::Parse('{"Email":null,"Url":null}') } },
        @{ Name = 'contact-all-null'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Contact'] = [System.Text.Json.Nodes.JsonNode]::Parse('{"Name":null,"Email":null,"Url":null}') } },
        @{ Name = 'install-empty'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Install'] = [System.Text.Json.Nodes.JsonNode]::Parse('[{}]') } },
        @{ Name = 'install-without-name'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Install'] = [System.Text.Json.Nodes.JsonNode]::Parse('[{"Command":"tool install"}]') } },
        @{ Name = 'install-without-command-or-url'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Install'] = [System.Text.Json.Nodes.JsonNode]::Parse('[{"Name":"source"}]') } },
        @{ Name = 'install-command-null'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Install'] = [System.Text.Json.Nodes.JsonNode]::Parse('[{"Name":"source","Command":null}]') } },
        @{ Name = 'install-url-null'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['Install'] = [System.Text.Json.Nodes.JsonNode]::Parse('[{"Name":"source","Url":null}]') } },
        @{ Name = 'license-empty-name'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Info']['License'] = [System.Text.Json.Nodes.JsonNode]::Parse('{"Name":"","SpdxId":"MIT","Url":null}') } },
        @{ Name = 'empty-example-content'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Root']['Subcommands'][0]['Examples'] = [System.Text.Json.Nodes.JsonNode]::Parse('[{"Title":null,"Content":""}]') } },
        @{ Name = 'command-path-123'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Root']['Subcommands'][0]['Path'] = 'root / 123' } },
        @{ Name = 'command-path-dash'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Root']['Subcommands'][0]['Path'] = 'root / -flag' } },
        @{ Name = 'command-path-underscore'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Root']['Subcommands'][0]['Path'] = 'root / _cmd' } },
        @{ Name = 'command-path-env'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Root']['Subcommands'][0]['Path'] = 'root / $ENV' } },
        @{ Name = 'command-path-unicode'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Root']['Subcommands'][0]['Path'] = 'root / écmd' } },
        @{ Name = 'command-path-adjacent-number'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Root']['Subcommands'][0]['Path'] = 'root / 123abc' } },
        @{ Name = 'command-path-adjacent-dash'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['Root']['Subcommands'][0]['Path'] = 'root / -flag2' } },
        @{ Name = 'required-true-zero-arity'; Code = 'OPENCLI_ARITY'; Mutate = { param($document) $document['GlobalOptions'][0]['Required'] = $true; $document['GlobalOptions'][0]['ArityMinimum'] = 0 } },
        @{ Name = 'optional-one-arity'; Code = 'OPENCLI_ARITY'; Mutate = { param($document) $document['GlobalOptions'][0]['Required'] = $false; $document['GlobalOptions'][0]['ArityMinimum'] = 1 } },
        @{ Name = 'required-variadic-option'; Code = 'OPENCLI_VARIADIC'; Mutate = { param($document) $document['GlobalOptions'][0]['Required'] = $true; $document['GlobalOptions'][0]['Variadic'] = $true } },
        @{ Name = 'reversed-choice-order'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['GlobalOptions'][0]['Choices'] = [System.Text.Json.Nodes.JsonNode]::Parse('[{"Value":"yes"},{"Value":"no"}]'); $document['GlobalOptions'][0]['AllowedValues'] = [System.Text.Json.Nodes.JsonNode]::Parse('["yes","no"]') } },
        @{ Name = 'empty-global-config-wrapper'; Code = 'INVALID_BASELINE'; Mutate = { param($document) $document['GlobalConfig']['FileSources'] = [System.Text.Json.Nodes.JsonArray]::new() } },
        @{ Name = 'required-argument-zero-arity'; Code = 'OPENCLI_ARITY'; Mutate = { param($document) $command = @($document['Root']['Subcommands'].AsArray() | Where-Object { $_['Path'].ToString() -eq 'root / run' })[0]; $command['Arguments'][0]['Required'] = $true; $command['Arguments'][0]['ArityMinimum'] = 0 } },
        @{ Name = 'reversed-variadic-bounds'; Code = 'OPENCLI_ARITY'; Mutate = { param($document) $document['GlobalOptions'][0]['Variadic'] = $true; $document['GlobalOptions'][0]['ArityMinimum'] = 3; $document['GlobalOptions'][0]['ArityMaximum'] = 2 } }
    )
    foreach ($case in $hostileContractCases) {
        $mutated = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($hostileBaseline))
        & $case.Mutate $mutated
        $mutatedPath = Join-Path $temp ($case.Name + '-consumer.canonical.json')
        [IO.File]::WriteAllText($mutatedPath, $mutated.ToJsonString(), [Text.UTF8Encoding]::new($false))
        Assert-ToolError -Label ("$($case.Name) consumer check") -ExpectedExit 3 -ExpectedCode $case.Code -Arguments @('check', $hostileSource, '--baseline', $mutatedPath, '--no-telemetry')
        Assert-ToolError -Label ("$($case.Name) consumer diff") -ExpectedExit 3 -ExpectedCode $case.Code -Arguments @('diff', $mutatedPath, $hostileBaseline, '--no-telemetry')
    }
    Write-Output 'CASE=family3-canonical-baselines-consumer check_exit=3 diff_exit=3'

    $globalScopeOld = Join-Path $temp 'global-scope-old.json'
    $globalScopeNew = Join-Path $temp 'global-scope-new.json'
    $globalScopeSchema = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"global":{"flags":[{"name":"verbose","type":"boolean"}]},"commands":{"tool":{},"tool sub":{}}}'
    $rootLocalScopeSchema = $globalScopeSchema.Replace('"global":{"flags":[{"name":"verbose","type":"boolean"}]},', '').Replace('"tool":{}', '"tool":{"flags":[{"name":"verbose","type":"boolean"}]}')
    [IO.File]::WriteAllText($globalScopeOld, $globalScopeSchema, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($globalScopeNew, $rootLocalScopeSchema, [Text.UTF8Encoding]::new($false))
    $globalScope = & $tool diff $globalScopeOld $globalScopeNew --format text --no-telemetry 2>&1
    $globalScopeExit = $LASTEXITCODE
    $globalScope | Out-Host
    if ($globalScopeExit -ne 1 -or -not (($globalScope -join "`n") -match 'KMCLI101') -or (($globalScope -join "`n") -notmatch 'root / sub / --verbose')) {
        throw 'Global-to-root-local scope removal on the packed tool did not report the descendant callable surface.'
    }
    $globalScopeReverse = & $tool diff $globalScopeNew $globalScopeOld --format text --no-telemetry 2>&1
    $globalScopeReverseExit = $LASTEXITCODE
    $globalScopeReverse | Out-Host
    if ($globalScopeReverseExit -ne 0 -and $globalScopeReverseExit -ne 1) { throw 'Root-local-to-global scope comparison failed unexpectedly.' }
    if (($globalScopeReverse -join "`n") -notmatch 'KMCLI002|KMCLI101') { throw 'Root-local-to-global scope comparison did not report the accepted descendant option change.' }
    Write-Output "CASE=global-local-scope-consumer removed_exit=$globalScopeExit reverse_exit=$globalScopeReverseExit"

    $derivedGroupOld = Join-Path $temp 'derived-group-old.json'
    $derivedGroupNew = Join-Path $temp 'derived-group-new.json'
    $explicitGroupSchema = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool":{"kind":"group"},"tool parent":{"kind":"group","aliases":["p"]},"tool parent sub":{}}}'
    $implicitGroupSchema = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool parent sub":{}}}'
    [IO.File]::WriteAllText($derivedGroupOld, $explicitGroupSchema, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($derivedGroupNew, $implicitGroupSchema, [Text.UTF8Encoding]::new($false))
    $derivedGroup = & $tool diff $derivedGroupOld $derivedGroupNew --format text --no-telemetry 2>&1
    $derivedGroupExit = $LASTEXITCODE
    $derivedGroup | Out-Host
    if ($derivedGroupExit -ne 1 -or (($derivedGroup -join "`n") -notmatch 'KMCLI104')) { throw 'Explicit parent alias removal through a derived group was not reported.' }
    Write-Output "CASE=derived-group-alias-consumer exit=$derivedGroupExit"

    $actionParent = Join-Path $temp 'action-parent.json'
    $implicitParent = Join-Path $temp 'implicit-parent.json'
    $actionParentSchema = $explicitGroupSchema.Replace('"tool":{"kind":"group"}', '"tool":{"kind":"action"}')
    [IO.File]::WriteAllText($actionParent, $actionParentSchema, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($implicitParent, $implicitGroupSchema, [Text.UTF8Encoding]::new($false))
    $actionToDerived = & $tool diff $actionParent $implicitParent --format text --no-telemetry 2>&1
    $actionToDerivedExit = $LASTEXITCODE
    $actionToDerived | Out-Host
    if ($actionToDerivedExit -ne 1 -or (($actionToDerived -join "`n") -notmatch 'KMCLI111')) { throw 'Action-to-derived-group removal was not reported as a callable-surface break.' }
    Write-Output "CASE=action-to-derived-group-consumer exit=$actionToDerivedExit"

    $mixedOld = Join-Path $temp 'mixed-old.json'
    $mixedNew = Join-Path $temp 'mixed-new.json'
    $mixedOldText = $schema.Replace('"region","type":"string"', '"colour","type":"string","choices":[{"value":"red"},{"value":"blue"}]')
    $mixedNewText = $mixedOldText.Replace('[{"value":"red"},{"value":"blue"}]', '[{"value":"red"},{"value":"green"}]')
    [IO.File]::WriteAllText($mixedOld, $mixedOldText, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($mixedNew, $mixedNewText, [Text.UTF8Encoding]::new($false))
    $mixed = & $tool diff $mixedOld $mixedNew --no-telemetry 2>&1
    $mixedExit = $LASTEXITCODE
    $mixed | Out-Host
    if ($mixedExit -ne 1 -or -not (($mixed -join "`n") -match 'KMCLI107')) { throw 'Mixed choice removal on the packed tool did not return exit 1/KMCLI107.' }

    $oldPositional = Join-Path $temp 'old-positional.json'
    $newPositional = Join-Path $temp 'new-positional.json'
    $oldPositionalText = $schema.Replace('"commands":{"tool":{"flags":[{"name":"region","type":"string"}]}}', '"commands":{"tool run <target>":{"args":[{"name":"target"}]}}')
    $newPositionalText = $oldPositionalText.Replace('<target>', '<mode> <target>').Replace('[{"name":"target"}]', '[{"name":"mode"},{"name":"target"}]')
    [IO.File]::WriteAllText($oldPositional, $oldPositionalText, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($newPositional, $newPositionalText, [Text.UTF8Encoding]::new($false))
    $positional = & $tool diff $oldPositional $newPositional --no-telemetry 2>&1
    $positionalExit = $LASTEXITCODE
    $positional | Out-Host
    if ($positionalExit -ne 1 -or -not (($positional -join "`n") -match 'KMCLI109')) { throw 'Prepended positional insertion on the packed tool did not return exit 1/KMCLI109.' }

    $renamed = Join-Path $temp 'renamed.json'
    $renamedText = $schema.Replace('"binary":"tool"', '"binary":"renamed"').Replace('"commands":{"tool"', '"commands":{"renamed"')
    [IO.File]::WriteAllText($renamed, $renamedText, [Text.UTF8Encoding]::new($false))
    $binary = & $tool diff $old $renamed --no-telemetry 2>&1
    $binaryExit = $LASTEXITCODE
    $binary | Out-Host
    if ($binaryExit -ne 1 -or -not (($binary -join "`n") -match 'KMCLI110')) { throw 'Binary rename on the packed tool did not return exit 1/KMCLI110.' }

    $infoDescriptionOld = $schema.Replace('"version":"1"', '"version":"1","description":"old info description"')
    $infoDescriptionNew = $infoDescriptionOld.Replace('old info description', 'new info description')
    Assert-InformationalCase -Label 'info-description' -OldText $infoDescriptionOld -NewText $infoDescriptionNew

    $choiceDescriptionOld = $schema.Replace('"region","type":"string"', '"region","type":"string","choices":[{"value":"red","description":"old choice description"}]')
    $choiceDescriptionNew = $choiceDescriptionOld.Replace('old choice description', 'new choice description')
    Assert-InformationalCase -Label 'choice-description' -OldText $choiceDescriptionOld -NewText $choiceDescriptionNew

    $installGuidanceOld = $schema.Replace(',"commands":', ',"install":[{"name":"download","url":"https://install.invalid/old","description":"old install guidance"}],"commands":')
    $installGuidanceNew = $installGuidanceOld.Replace('old install guidance', 'new install guidance')
    Assert-InformationalCase -Label 'install-guidance' -OldText $installGuidanceOld -NewText $installGuidanceNew

    $validateOptions = & $tool validate $old --baseline $baseline --no-telemetry 2>&1
    if ($LASTEXITCODE -ne 2 -or -not (($validateOptions -join "`n") -match 'UNSUPPORTED_OPTION')) { throw 'Packed-tool validate accepted unsupported --baseline.' }
    $checkOptions = & $tool check $old --baseline $baseline --output ignored.json --no-telemetry 2>&1
    if ($LASTEXITCODE -ne 2 -or -not (($checkOptions -join "`n") -match 'UNSUPPORTED_OPTION')) { throw 'Packed-tool check accepted unsupported --output.' }
    $malformed = Join-Path $temp 'malformed.json'
    [IO.File]::WriteAllText($malformed, '{"opencliVersion":"1.0.0-alpha.14"}', [Text.UTF8Encoding]::new($false))
    & $tool validate $malformed --no-telemetry | Out-Host
    if ($LASTEXITCODE -ne 3) { throw 'Malformed packed-tool input did not return exit 3.' }
    $unsupported = Join-Path $temp 'unsupported.json'
    [IO.File]::WriteAllText($unsupported, $schema.Replace('1.0.0-alpha.14', '1.0.0-alpha.13'), [Text.UTF8Encoding]::new($false))
    & $tool validate $unsupported --no-telemetry | Out-Host
    if ($LASTEXITCODE -ne 3) { throw 'Unsupported-version packed-tool input did not return exit 3.' }

    $missing = Join-Path $temp 'missing.json'
    Assert-ToolError -Label 'missing source text' -ExpectedExit 2 -ExpectedCode 'INPUT_NOT_FOUND' -Arguments @('validate', $missing, '--format', 'text', '--no-telemetry')
    Assert-ToolError -Label 'missing source json' -ExpectedExit 2 -ExpectedCode 'INPUT_NOT_FOUND' -Arguments @('validate', $missing, '--format', 'json', '--no-telemetry')
    $invalidUtf8 = Join-Path $temp 'invalid-utf8.json'
    [IO.File]::WriteAllBytes($invalidUtf8, [byte[]](0x7B, 0xFF, 0x7D))
    Assert-ToolError -Label 'invalid UTF-8 text' -ExpectedExit 3 -ExpectedCode 'INVALID_UTF8' -Arguments @('validate', $invalidUtf8, '--format', 'text', '--no-telemetry')
    Assert-ToolError -Label 'invalid UTF-8 json' -ExpectedExit 3 -ExpectedCode 'INVALID_UTF8' -Arguments @('validate', $invalidUtf8, '--format', 'json', '--no-telemetry')
    $oversized = Join-Path $temp 'oversized.json'
    [IO.File]::WriteAllBytes($oversized, [byte[]]::new(2097153))
    Assert-ToolError -Label 'oversized source' -ExpectedExit 3 -ExpectedCode 'INPUT_TOO_LARGE' -Arguments @('validate', $oversized, '--format', 'json', '--no-telemetry')
    Assert-ToolError -Label 'oversized source text' -ExpectedExit 3 -ExpectedCode 'INPUT_TOO_LARGE' -Arguments @('validate', $oversized, '--format', 'text', '--no-telemetry')
    Assert-ToolError -Label 'missing baseline' -ExpectedExit 2 -ExpectedCode 'BASELINE_NOT_FOUND' -Arguments @('check', $old, '--baseline', $missing, '--format', 'text', '--no-telemetry')
    Assert-ToolError -Label 'missing baseline json' -ExpectedExit 2 -ExpectedCode 'BASELINE_NOT_FOUND' -Arguments @('check', $old, '--baseline', $missing, '--format', 'json', '--no-telemetry')
    $invalidBaseline = Join-Path $temp 'invalid-baseline.json'
    [IO.File]::WriteAllText($invalidBaseline, 'not canonical json', [Text.UTF8Encoding]::new($false))
    Assert-ToolError -Label 'malformed baseline' -ExpectedExit 3 -ExpectedCode 'INVALID_BASELINE' -Arguments @('check', $old, '--baseline', $invalidBaseline, '--format', 'json', '--no-telemetry')
    Assert-ToolError -Label 'malformed baseline text' -ExpectedExit 3 -ExpectedCode 'INVALID_BASELINE' -Arguments @('check', $old, '--baseline', $invalidBaseline, '--format', 'text', '--no-telemetry')
    $unsupportedCanonicalBaseline = Join-Path $temp 'unsupported-canonical-baseline.json'
    $unsupportedCanonicalText = (Get-Content -Raw -LiteralPath $baseline).Replace('"SchemaVersion": 2', '"SchemaVersion": 99')
    [IO.File]::WriteAllText($unsupportedCanonicalBaseline, $unsupportedCanonicalText, [Text.UTF8Encoding]::new($false))
    Assert-ToolError -Label 'unsupported canonical baseline' -ExpectedExit 3 -ExpectedCode 'UNSUPPORTED_MANIFEST_VERSION' -Arguments @('check', $old, '--baseline', $unsupportedCanonicalBaseline, '--format', 'json', '--no-telemetry')
    Assert-ToolError -Label 'unsupported canonical baseline diff' -ExpectedExit 3 -ExpectedCode 'UNSUPPORTED_MANIFEST_VERSION' -Arguments @('diff', $unsupportedCanonicalBaseline, $baseline, '--format', 'json', '--no-telemetry')
    $missingIgnore = Join-Path $temp 'missing-ignore.json'
    Assert-ToolError -Label 'missing suppression' -ExpectedExit 2 -ExpectedCode 'IGNORE_NOT_FOUND' -Arguments @('diff', $old, $new, '--ignore', $missingIgnore, '--format', 'text', '--no-telemetry')
    Assert-ToolError -Label 'missing suppression json' -ExpectedExit 2 -ExpectedCode 'IGNORE_NOT_FOUND' -Arguments @('diff', $old, $new, '--ignore', $missingIgnore, '--format', 'json', '--no-telemetry')
    $invalidIgnore = Join-Path $temp 'invalid-ignore.json'
    [IO.File]::WriteAllBytes($invalidIgnore, [byte[]](0x7B, 0xFF, 0x7D))
    Assert-ToolError -Label 'invalid suppression UTF-8' -ExpectedExit 2 -ExpectedCode 'INVALID_UTF8' -Arguments @('diff', $old, $new, '--ignore', $invalidIgnore, '--format', 'text', '--no-telemetry')
    Assert-ToolError -Label 'invalid suppression UTF-8 json' -ExpectedExit 2 -ExpectedCode 'INVALID_UTF8' -Arguments @('diff', $old, $new, '--ignore', $invalidIgnore, '--format', 'json', '--no-telemetry')
    Assert-ToolError -Label 'output write' -ExpectedExit 2 -ExpectedCode 'OUTPUT_NOT_WRITABLE' -Arguments @('snapshot', $old, '--output', $temp, '--format', 'json', '--no-telemetry')
    Assert-ToolError -Label 'output write text' -ExpectedExit 2 -ExpectedCode 'OUTPUT_NOT_WRITABLE' -Arguments @('snapshot', $old, '--output', $temp, '--format', 'text', '--no-telemetry')
    if ($SelfTest) { Write-Output "PACKAGE_CACHE_NEGATIVE=PASS ordinary_cache_sha256=$seedHash fresh_install_sha256=$installedHash" }
    Write-Output "PACKAGE_CONSUMER_SMOKE=PASS source=local-exclusive-candidate candidate_sha256=$candidateHash installed_sha256=$installedHash fresh_nuget_packages=$freshPackages config=$nugetConfig"
}
finally {
    if ($pushed) { Pop-Location }
    if ($null -eq $oldNugetPackages) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue } else { $env:NUGET_PACKAGES = $oldNugetPackages }
    if ($null -eq $oldHttpCache) { Remove-Item Env:NUGET_HTTP_CACHE_PATH -ErrorAction SilentlyContinue } else { $env:NUGET_HTTP_CACHE_PATH = $oldHttpCache }
    if ($null -eq $oldFallbackPackages) { Remove-Item Env:NUGET_FALLBACK_PACKAGES -ErrorAction SilentlyContinue } else { $env:NUGET_FALLBACK_PACKAGES = $oldFallbackPackages }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
    Set-Location $oldLocation
}
