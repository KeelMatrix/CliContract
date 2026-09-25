param(
    [string] $ToolPath = './src/KeelMatrix.CliContract.Tool/bin/Release/net8.0/KeelMatrix.CliContract.dll'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$tool = (Resolve-Path -LiteralPath $ToolPath).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-regressions-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$previousNoTelemetry = $env:KEELMATRIX_NO_TELEMETRY
$env:KEELMATRIX_NO_TELEMETRY = '1'
$legacyDiagnostic = 'pr' + 'obe'

function Invoke-Tool([string[]] $Arguments) {
    $output = @(& dotnet $tool @Arguments 2>&1)
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Write-Utf8([string] $Path, [string] $Value) {
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Assert-Case([string] $Name, $Result, [int] $ExpectedExit, [string] $ExpectedText) {
    if ($Result.ExitCode -ne $ExpectedExit) { throw "$Name returned $($Result.ExitCode), expected $ExpectedExit." }
    $joined = $Result.Output -join "`n"
    if ($ExpectedText -and $joined -notmatch [regex]::Escape($ExpectedText)) { throw "$Name did not contain $ExpectedText." }
    Write-Output "CASE=$Name exit=$($Result.ExitCode)"
    $Result.Output | ForEach-Object { Write-Output ("OUTPUT=" + $_) }
}

try {
    $valid = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool":{"flags":[{"name":"region","type":"string"}]}}}'
    $duplicate = '{"opencliVersion":"1.0.0-alpha.14","opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"}}'
    $source = Join-Path $temp 'source.json'
    $duplicatePath = Join-Path $temp 'duplicate.json'
    $baselinePath = Join-Path $temp 'baseline.canonical.json'
    Write-Utf8 $source $valid
    Write-Utf8 $duplicatePath $duplicate

    Assert-Case 'duplicate-opencli' (Invoke-Tool @('validate', $duplicatePath, '--input', 'opencli', '--no-telemetry')) 3 'DUPLICATE_JSON_KEY'
    Assert-Case 'duplicate-auto' (Invoke-Tool @('validate', $duplicatePath, '--input', 'auto', '--no-telemetry')) 3 'DUPLICATE_JSON_KEY'
    Assert-Case 'valid-baseline' (Invoke-Tool @('snapshot', $source, '--input', 'opencli', '--output', $baselinePath, '--no-telemetry')) 0 'SNAPSHOT'
    Assert-Case 'validate-rejects-baseline' (Invoke-Tool @('validate', $source, '--baseline', (Join-Path $temp 'missing.json'), '--no-telemetry')) 2 'UNSUPPORTED_OPTION'
    Assert-Case 'check-rejects-output' (Invoke-Tool @('check', $source, '--baseline', $baselinePath, '--output', (Join-Path $temp 'ignored.json'), '--no-telemetry')) 2 'UNSUPPORTED_OPTION'

    $richSource = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"global":{"config":{"json":"config.json"},"exitCodes":[{"code":0,"status":"OK","summary":"ok"}],"flags":[{"name":"verbose","type":"string","alternativeSources":[{"type":"$ENV","property":"VERBOSE"},{"type":"$FILE","property":"$.verbose"}],"choices":[{"value":"yes"}]}]},"commands":{"tool":{"exitCodes":[{"code":0,"status":"OK","summary":"ok"}],"flags":[{"name":"local","type":"boolean"}]},"tool run <target>":{"args":[{"name":"target","type":"string"}]}}}'
    $richSourcePath = Join-Path $temp 'rich-source.json'
    $richBaselinePath = Join-Path $temp 'rich-baseline.canonical.json'
    Write-Utf8 $richSourcePath $richSource
    Assert-Case 'rich-baseline' (Invoke-Tool @('snapshot', $richSourcePath, '--input', 'opencli', '--output', $richBaselinePath, '--no-telemetry')) 0 'SNAPSHOT'

    function Write-CanonicalMutation([string] $Name, [scriptblock] $Mutation) {
        $document = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($richBaselinePath))
        & $Mutation $document
        $path = Join-Path $temp ($Name + '.canonical.json')
        Write-Utf8 $path $document.ToJsonString()
        return $path
    }

    $hostileCanonicalCases = @(
        @{ Name = 'duplicate-global-file-format'; Mutate = { param($document) $sources = $document['GlobalConfig']['FileSources'].AsArray(); $sources.Add($sources[0].DeepClone()) } },
        @{ Name = 'duplicate-global-exit-code'; Mutate = { param($document) $codes = $document['GlobalExitCodes'].AsArray(); $codes.Add($codes[0].DeepClone()) } },
        @{ Name = 'duplicate-command-exit-code'; Mutate = { param($document) $codes = $document['Root']['ExitCodes'].AsArray(); $codes.Add($codes[0].DeepClone()) } },
        @{ Name = 'invalid-alternative-source'; Mutate = { param($document) $document['GlobalOptions'][0]['AlternativeSources'][0]['Type'] = '$BAD' } },
        @{ Name = 'argument-default'; Mutate = { param($document) $document['Root']['Subcommands'][0]['Arguments'][0]['DefaultValue'] = 'not-allowed' } },
        @{ Name = 'unrepresentable-status'; Mutate = { param($document) $document['Root']['Status'] = 'DEPRECATED' } },
        @{ Name = 'contradictory-domain'; Mutate = { param($document) $document['GlobalOptions'][0]['AllowedValues'][0] = 'other' } },
        @{ Name = 'required-true-zero-arity'; Mutate = { param($document) $document['GlobalOptions'][0]['Required'] = $true; $document['GlobalOptions'][0]['ArityMinimum'] = 0 } },
        @{ Name = 'optional-one-arity'; Mutate = { param($document) $document['GlobalOptions'][0]['Required'] = $false; $document['GlobalOptions'][0]['ArityMinimum'] = 1 } },
        @{ Name = 'required-variadic-option'; Mutate = { param($document) $document['GlobalOptions'][0]['Required'] = $true; $document['GlobalOptions'][0]['Variadic'] = $true } },
        @{ Name = 'reversed-choice-order'; Mutate = { param($document) $document['GlobalOptions'][0]['Choices'] = [System.Text.Json.Nodes.JsonNode]::Parse('[{"Value":"yes"},{"Value":"no"}]'); $document['GlobalOptions'][0]['AllowedValues'] = [System.Text.Json.Nodes.JsonNode]::Parse('["yes","no"]') } },
        @{ Name = 'empty-global-config-wrapper'; Mutate = { param($document) $document['GlobalConfig']['FileSources'] = [System.Text.Json.Nodes.JsonArray]::new() } },
        @{ Name = 'required-argument-zero-arity'; Mutate = { param($document) $command = @($document['Root']['Subcommands'].AsArray() | Where-Object { $_['Path'].ToString() -eq 'root / run' })[0]; $command['Arguments'][0]['Required'] = $true; $command['Arguments'][0]['ArityMinimum'] = 0 } },
        @{ Name = 'reversed-variadic-bounds'; Mutate = { param($document) $document['GlobalOptions'][0]['Variadic'] = $true; $document['GlobalOptions'][0]['ArityMinimum'] = 3; $document['GlobalOptions'][0]['ArityMaximum'] = 2 } }
    )
    foreach ($case in $hostileCanonicalCases) {
        $casePath = Write-CanonicalMutation $case.Name $case.Mutate
        Assert-Case ("$($case.Name)-check") (Invoke-Tool @('check', $richSourcePath, '--baseline', $casePath, '--no-telemetry')) 3 $null
        Assert-Case ("$($case.Name)-diff") (Invoke-Tool @('diff', $casePath, $richBaselinePath, '--no-telemetry')) 3 $null
    }
    Write-Output 'HOSTILE_CANONICAL_BASELINES=PASS check_exit=3 diff_exit=3'

    $mixedDomain = $valid.Replace('"type":"string"', '"type":"string","choices":[{"value":"red"},{"value":"blue"}]').Replace('"region"', '"colour"')
    $mixedDomainCurrent = $mixedDomain.Replace('[{"value":"red"},{"value":"blue"}]', '[{"value":"red"},{"value":"green"}]')
    $mixedDomainPath = Join-Path $temp 'mixed-domain-old.json'
    $mixedDomainCurrentPath = Join-Path $temp 'mixed-domain-new.json'
    Write-Utf8 $mixedDomainPath $mixedDomain
    Write-Utf8 $mixedDomainCurrentPath $mixedDomainCurrent
    Assert-Case 'mixed-domain-removal' (Invoke-Tool @('diff', $mixedDomainPath, $mixedDomainCurrentPath, '--no-telemetry')) 1 'KMCLI107'

    $sourceOld = $valid.Replace('"type":"string"', '"type":"string","alternativeSources":[{"type":"$ENV","property":"REGION"},{"type":"$FILE","property":"$.region"}]').Replace(',"commands":', ',"global":{"config":{"json":"old.json"}},"commands":')
    $sourceNew = $sourceOld.Replace('"property":"REGION"', '"property":"NEW_REGION"').Replace('[{"type":"$ENV","property":"NEW_REGION"},{"type":"$FILE","property":"$.region"}]', '[{"type":"$FILE","property":"$.region"},{"type":"$ENV","property":"NEW_REGION"}]').Replace('"old.json"', '"new.json"')
    $sourceOldPath = Join-Path $temp 'sources-old.json'
    $sourceNewPath = Join-Path $temp 'sources-new.json'
    Write-Utf8 $sourceOldPath $sourceOld
    Write-Utf8 $sourceNewPath $sourceNew
    Assert-Case 'default-source-change-warning' (Invoke-Tool @('diff', $sourceOldPath, $sourceNewPath, '--fail-on', 'warning', '--no-telemetry')) 1 'KMCLI204'

    $badInputs = @{
        'bad-type' = $valid.Replace('"type":"string"', '"type":["string"]')
        'bad-min' = $valid.Replace('"type":"string"', '"type":"string","minItems":2,"maxItems":1')
        'empty-sources' = $valid.Replace('"type":"string"', '"type":"string","alternativeSources":[]')
        'remote-ref' = $valid.Replace('"type":"string"', '"type":"string","$ref":"https://example.invalid/schema"')
        'remote-include' = $valid.Replace(',"commands":', ',"include":"https://example.invalid/schema","commands":')
    }

    $duplicateFlag = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool":{"flags":[{"name":"region","type":"string"},{"name":"region","type":"string"}]}}}'
    $duplicateArgument = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool run <region> <region>":{"args":[{"name":"region"},{"name":"region"}]}}}'
    $normalizedCollision = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool":{"flags":[{"name":"region","type":"string"},{"name":"--region","type":"string"}]}}}'
    foreach ($case in @(
        @{ Name = 'duplicate-flag-name'; Value = $duplicateFlag },
        @{ Name = 'duplicate-argument-name'; Value = $duplicateArgument },
        @{ Name = 'normalized-parameter-collision'; Value = $normalizedCollision }
    )) {
        $path = Join-Path $temp ($case.Name + '.json')
        Write-Utf8 $path $case.Value
        Assert-Case $case.Name (Invoke-Tool @('validate', $path, '--input', 'opencli', '--no-telemetry')) 3 'OPENCLI_DUPLICATE_PARAMETER'
    }

    $duplicateNestedCommandPath = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool run <first>":{},"tool run <second>":{}}}'
    $duplicateRootCommandPath = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool":{},"tool <arg>":{}}}'
    foreach ($case in @(
        @{ Name = 'duplicate-command-path-nested'; Value = $duplicateNestedCommandPath },
        @{ Name = 'duplicate-command-path-root'; Value = $duplicateRootCommandPath }
    )) {
        $path = Join-Path $temp ($case.Name + '.json')
        Write-Utf8 $path $case.Value
        Assert-Case ("$($case.Name)-validate") (Invoke-Tool @('validate', $path, '--input', 'opencli', '--no-telemetry')) 3 'OPENCLI_DUPLICATE_COMMAND_PATH'
        Assert-Case ("$($case.Name)-snapshot") (Invoke-Tool @('snapshot', $path, '--input', 'opencli', '--output', (Join-Path $temp ($case.Name + '.canonical.json')), '--no-telemetry')) 3 'OPENCLI_DUPLICATE_COMMAND_PATH'
        Assert-Case ("$($case.Name)-check") (Invoke-Tool @('check', $path, '--baseline', $baselinePath, '--input', 'opencli', '--no-telemetry')) 3 'OPENCLI_DUPLICATE_COMMAND_PATH'
        Assert-Case ("$($case.Name)-diff-self") (Invoke-Tool @('diff', $path, $path, '--input', 'opencli', '--no-telemetry')) 3 'OPENCLI_DUPLICATE_COMMAND_PATH'
    }

    $oldArguments = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool run <first> <second>":{"args":[{"name":"first"},{"name":"second"}]}}}'
    $newArguments = $oldArguments.Replace('[{"name":"first"},{"name":"second"}]', '[{"name":"second"},{"name":"first"}]')
    $oldArgumentsPath = Join-Path $temp 'old-arguments.json'
    $newArgumentsPath = Join-Path $temp 'new-arguments.json'
    Write-Utf8 $oldArgumentsPath $oldArguments
    Write-Utf8 $newArgumentsPath $newArguments
    Assert-Case 'positional-argument-order' (Invoke-Tool @('diff', $oldArgumentsPath, $newArgumentsPath, '--format', 'text', '--no-telemetry')) 1 'KMCLI109'
    $prependedArguments = $oldArguments.Replace('<first> <second>', '<mode> <first> <second>').Replace('[{"name":"first"},{"name":"second"}]', '[{"name":"mode"},{"name":"first"},{"name":"second"}]')
    $prependedPath = Join-Path $temp 'prepended-arguments.json'
    Write-Utf8 $prependedPath $prependedArguments
    Assert-Case 'prepended-optional-argument' (Invoke-Tool @('diff', $oldArgumentsPath, $prependedPath, '--no-telemetry')) 1 'KMCLI109'
    $trailingArguments = $oldArguments.Replace('<first> <second>', '<first> <second> <mode>').Replace('[{"name":"first"},{"name":"second"}]', '[{"name":"first"},{"name":"second"},{"name":"mode"}]')
    $trailingPath = Join-Path $temp 'trailing-arguments.json'
    Write-Utf8 $trailingPath $trailingArguments
    Assert-Case 'trailing-optional-argument' (Invoke-Tool @('diff', $oldArgumentsPath, $trailingPath, '--no-telemetry')) 0 'KMCLI003'

    $action = $valid.Replace('"commands":{"tool":{"flags":[{"name":"region","type":"string"}]}}', '"commands":{"tool":{"kind":"action"}}')
    $group = $action.Replace('"kind":"action"', '"kind":"group"')
    $actionPath = Join-Path $temp 'action.json'
    $groupPath = Join-Path $temp 'group.json'
    Write-Utf8 $actionPath $action
    Write-Utf8 $groupPath $group
    Assert-Case 'action-to-group' (Invoke-Tool @('diff', $actionPath, $groupPath, '--no-telemetry')) 1 'KMCLI111'

    $binaryRename = $valid.Replace('"binary":"tool"', '"binary":"renamed"').Replace('"commands":{"tool"', '"commands":{"renamed"')
    $binaryPath = Join-Path $temp 'binary-renamed.json'
    Write-Utf8 $binaryPath $binaryRename
    Assert-Case 'binary-rename' (Invoke-Tool @('diff', $source, $binaryPath, '--no-telemetry')) 1 'KMCLI110'
    foreach ($name in $badInputs.Keys) {
        $path = Join-Path $temp ($name + '.json')
        Write-Utf8 $path $badInputs[$name]
        $expectedText = if ($name -in @('remote-ref', 'remote-include')) { 'OPENCLI_REMOTE_REFERENCE' } else { $null }
        Assert-Case $name (Invoke-Tool @('validate', $path, '--input', 'auto', '--no-telemetry')) 3 $expectedText
    }

    $contactUrl = 'https://metadata.example.invalid/contact?source=OpenCLI%2F1.0.0-alpha.14'
    $licenseUrl = 'https://metadata.example.invalid/license'
    $installUrl = 'https://metadata.example.invalid/install?channel=stable'
    $metadata = $valid.Replace('"info":{"title":"Tool","binary":"tool","version":"1"}', ('"info":{"title":"Tool","binary":"tool","version":"1","contact":{"url":"' + $contactUrl + '"},"license":{"name":"MIT","url":"' + $licenseUrl + '"}}'))
    $metadata = $metadata.Replace(',"commands":', (',"install":[{"name":"download","url":"' + $installUrl + '"}],"commands":'))
    $metadataPath = Join-Path $temp 'metadata-urls.json'
    $metadataManifest = Join-Path $temp 'metadata-urls.canonical.json'
    Write-Utf8 $metadataPath $metadata
    Assert-Case 'informational-metadata-url' (Invoke-Tool @('snapshot', $metadataPath, '--input', 'opencli', '--output', $metadataManifest, '--no-telemetry')) 0 'SNAPSHOT'
    $metadataDocument = Get-Content -Raw -LiteralPath $metadataManifest | ConvertFrom-Json
    if ($metadataDocument.Info.Contact.Url -cne $contactUrl -or $metadataDocument.Info.License.Url -cne $licenseUrl -or $metadataDocument.Info.Install[0].Url -cne $installUrl) {
        throw 'Informational metadata URLs were not preserved verbatim in the canonical manifest.'
    }
    Write-Output 'CASE=informational-metadata-url preserved=true network=not-requested'
    $malformedPath = Join-Path $temp 'malformed.json'
    $unsupportedPath = Join-Path $temp 'unsupported.json'
    Write-Utf8 $malformedPath '{'
    Write-Utf8 $unsupportedPath $valid.Replace('1.0.0-alpha.14', '1.0.0-alpha.13')
    foreach ($mode in @('auto', 'opencli')) {
        Assert-Case ("malformed-$mode") (Invoke-Tool @('validate', $malformedPath, '--input', $mode, '--no-telemetry')) 3 $null
        $unsupportedResult = Invoke-Tool @('validate', $unsupportedPath, '--input', $mode, '--no-telemetry')
        Assert-Case ("unsupported-$mode") $unsupportedResult 3 'UNSUPPORTED_OPENCLI_VERSION'
        if (($unsupportedResult.Output -join "`n") -match ('(?i)\b' + $legacyDiagnostic + '\b')) { throw "unsupported-$mode exposed an internal diagnostic term." }
    }

    $numericInteger = Join-Path $temp 'numeric-integer.json'
    $numericDecimal = Join-Path $temp 'numeric-decimal.json'
    Write-Utf8 $numericInteger $valid.Replace('"type":"string"', '"type":"number","default":7')
    Write-Utf8 $numericDecimal $valid.Replace('"type":"string"', '"type":"number","default":7.0')
    $manifestInteger = Join-Path $temp 'integer.canonical.json'
    $manifestDecimal = Join-Path $temp 'decimal.canonical.json'
    Assert-Case 'numeric-integer-snapshot' (Invoke-Tool @('snapshot', $numericInteger, '--output', $manifestInteger, '--no-telemetry')) 0 'SNAPSHOT'
    Assert-Case 'numeric-decimal-snapshot' (Invoke-Tool @('snapshot', $numericDecimal, '--output', $manifestDecimal, '--no-telemetry')) 0 'SNAPSHOT'
    $integerHash = (Get-FileHash -LiteralPath $manifestInteger -Algorithm SHA256).Hash
    $decimalHash = (Get-FileHash -LiteralPath $manifestDecimal -Algorithm SHA256).Hash
    if ($integerHash -ne $decimalHash) { throw 'Equivalent numeric defaults produced different canonical bytes.' }
    Write-Output "CASE=numeric-canonical-bytes sha256=$integerHash"

    $yamlNumeric = Join-Path $root 'fixtures/opencli/numeric-defaults-yaml.yaml'
    $jsonNumeric = Join-Path $root 'fixtures/opencli/numeric-defaults-yaml.json'
    $yamlNumericManifest = Join-Path $temp 'numeric-yaml.canonical.json'
    $jsonNumericManifest = Join-Path $temp 'numeric-json.canonical.json'
    Assert-Case 'yaml-numeric-snapshot' (Invoke-Tool @('snapshot', $yamlNumeric, '--input', 'opencli', '--output', $yamlNumericManifest, '--no-telemetry')) 0 'SNAPSHOT'
    Assert-Case 'json-numeric-snapshot' (Invoke-Tool @('snapshot', $jsonNumeric, '--input', 'opencli', '--output', $jsonNumericManifest, '--no-telemetry')) 0 'SNAPSHOT'
    $yamlNumericHash = (Get-FileHash -LiteralPath $yamlNumericManifest -Algorithm SHA256).Hash
    $jsonNumericHash = (Get-FileHash -LiteralPath $jsonNumericManifest -Algorithm SHA256).Hash
    if ($yamlNumericHash -ne $jsonNumericHash) { throw 'Equivalent JSON/YAML numeric values produced different canonical bytes.' }
    Assert-Case 'yaml-json-numeric-diff' (Invoke-Tool @('diff', $yamlNumeric, $jsonNumeric, '--fail-on', 'warning', '--no-telemetry')) 0 'COMPATIBLE'
    Write-Output "CASE=yaml-json-numeric-canonical-bytes trailing-dot-exponents=true sha256=$yamlNumericHash"

    $invalidIntegerDomainJson = Join-Path $root 'fixtures/opencli/numeric-integer-domain-fraction.json'
    $invalidIntegerDomainYaml = Join-Path $root 'fixtures/opencli/numeric-integer-domain-fraction.yaml'
    foreach ($sourcePath in @($invalidIntegerDomainJson, $invalidIntegerDomainYaml)) {
        $label = [IO.Path]::GetFileNameWithoutExtension($sourcePath)
        $invalidManifest = Join-Path $temp ($label + '.canonical.json')
        Assert-Case "$label-validate" (Invoke-Tool @('validate', $sourcePath, '--format', 'json', '--no-telemetry')) 3 'OPENCLI_CHOICE'
        Assert-Case "$label-snapshot" (Invoke-Tool @('snapshot', $sourcePath, '--format', 'json', '--output', $invalidManifest, '--no-telemetry')) 3 'OPENCLI_CHOICE'
        Assert-Case "$label-check" (Invoke-Tool @('check', $sourcePath, '--format', 'json', '--baseline', $baselinePath, '--no-telemetry')) 3 'OPENCLI_CHOICE'
        Assert-Case "$label-diff" (Invoke-Tool @('diff', $sourcePath, $sourcePath, '--format', 'json', '--no-telemetry')) 3 'OPENCLI_CHOICE'
    }

    $validFixtures = @(
        'example-cli.json',
        'example-cli-reordered.json',
        'example-cli.yaml',
        'fix-round10.yaml',
        'fix-round17-scope-and-trie.json',
        'global-config-order-a.json',
        'global-config-order-a.yaml',
        'global-config-order-b.json',
        'global-config-order-b.yaml',
        'globalflags-cli.ocs.yaml',
        'numeric-defaults-exponent.json',
        'numeric-defaults-extreme.json',
        'numeric-defaults-plain.json',
        'numeric-defaults-yaml.json',
        'numeric-defaults-yaml.yaml',
        'petstore-cli.ocs.json',
        'petstore-cli.ocs.yaml',
        'pleasantries-cli.ocs.yaml',
        'regression-fixtures.json',
        'valid-argument-passthrough.json',
        'valid-argument-passthrough-false.json'
    )
    foreach ($fixture in $validFixtures) {
        $sourcePath = Join-Path $root ('fixtures/opencli/' + $fixture)
        $label = $fixture -replace '[^A-Za-z0-9]', '-'
        $manifestPath = Join-Path $temp ($label + '.self.canonical.json')
        Assert-Case "$label-self-snapshot" (Invoke-Tool @('snapshot', $sourcePath, '--input', 'opencli', '--output', $manifestPath, '--no-telemetry')) 0 'SNAPSHOT'
        Assert-Case "$label-self-check" (Invoke-Tool @('check', $sourcePath, '--input', 'opencli', '--baseline', $manifestPath, '--no-telemetry')) 0 'COMPATIBLE'
        Assert-Case "$label-self-diff" (Invoke-Tool @('diff', $manifestPath, $manifestPath, '--input', 'opencli', '--no-telemetry')) 0 'COMPATIBLE'
    }

    $configOld = Join-Path $root 'fixtures/opencli/global-config-order-a.json'
    $configNew = Join-Path $root 'fixtures/opencli/global-config-order-b.json'
    Assert-Case 'global-config-order-diff' (Invoke-Tool @('diff', $configOld, $configNew, '--fail-on', 'warning', '--no-telemetry')) 0 'COMPATIBLE'

    $passthroughEnabled = Join-Path $root 'fixtures/opencli/valid-argument-passthrough.json'
    $passthroughDisabled = Join-Path $root 'fixtures/opencli/valid-argument-passthrough-false.json'
    $passthroughBaseline = Join-Path $temp 'passthrough.canonical.json'
    Assert-Case 'passthrough-snapshot' (Invoke-Tool @('snapshot', $passthroughEnabled, '--input', 'opencli', '--output', $passthroughBaseline, '--no-telemetry')) 0 'SNAPSHOT'
    Assert-Case 'passthrough-removal' (Invoke-Tool @('diff', $passthroughEnabled, $passthroughDisabled, '--fail-on', 'warning', '--no-telemetry')) 1 'KMCLI112'
    Assert-Case 'passthrough-check-removal' (Invoke-Tool @('check', $passthroughDisabled, '--baseline', $passthroughBaseline, '--fail-on', 'warning', '--no-telemetry')) 1 'KMCLI112'
    Assert-Case 'passthrough-addition' (Invoke-Tool @('diff', $passthroughDisabled, $passthroughEnabled, '--fail-on', 'warning', '--no-telemetry')) 0 'KMCLI112'

    $optional = Join-Path $temp 'optional.json'
    $required = Join-Path $temp 'required.json'
    Write-Utf8 $optional $valid
    Write-Utf8 $required $valid.Replace('"type":"string"', '"type":"string","required":true')
    $comparison = Invoke-Tool @('diff', $optional, $required, '--format', 'text', '--no-telemetry')
    Assert-Case 'requiredness-only' $comparison 1 'KMCLI105'
    if (($comparison.Output -join "`n") -match 'KMCLI106') { throw 'Implicit requiredness arity change emitted KMCLI106.' }
    Write-Output 'CASE=requiredness-only-no-implicit-arity exit=pass'
    $ignore = Join-Path $temp 'ignore.json'
    Write-Utf8 $ignore '["KMCLI105"]'
    $suppressed = Invoke-Tool @('diff', $optional, $required, '--ignore', $ignore, '--format', 'text', '--no-telemetry')
    Assert-Case 'requiredness-suppressed' $suppressed 0 'COMPATIBLE'
    if (($suppressed.Output -join "`n") -match 'KMCLI106') { throw 'Suppressing KMCLI105 left an implicit KMCLI106.' }

    $defaultTelemetryOptOut = Invoke-Tool @('diff', $optional, $required, '--format', 'text')
    Assert-Case 'shared-telemetry-optout' $defaultTelemetryOptOut 1 'KMCLI105'

    $help = Invoke-Tool @('--help')
    Assert-Case 'help-exit-codes' $help 0 '4  Unexpected tool failure'
    $helpText = $help.Output -join "`n"
    if ($helpText -notmatch 'Argument passthrough' -or $helpText -notmatch 'exact numeric value' -or $helpText -notmatch 'trailing-dot exponent mantissas' -or $helpText -notmatch 'YAML \.inf and \.nan') { throw 'Help output does not describe the canonical compatibility semantics.' }
    Write-Output 'CASE=help-canonicalization-contract exit=pass'

    Assert-Case 'duplicate-format' (Invoke-Tool @('validate', $source, '--format', 'text', '--format', 'json', '--no-telemetry')) 2 'DUPLICATE_OPTION'
    Assert-Case 'duplicate-fail-on' (Invoke-Tool @('validate', $source, '--fail-on', 'breaking', '--fail-on', 'warning', '--no-telemetry')) 2 'DUPLICATE_OPTION'
    Assert-Case 'duplicate-output' (Invoke-Tool @('snapshot', $source, '--output', (Join-Path $temp 'one.json'), '--output', (Join-Path $temp 'two.json'), '--no-telemetry')) 2 'DUPLICATE_OPTION'

    $invalidUtf8 = Join-Path $temp 'invalid-utf8.json'
    [IO.File]::WriteAllBytes($invalidUtf8, [Text.Encoding]::UTF8.GetBytes($valid) + [byte]0xFF)
    Assert-Case 'invalid-utf8' (Invoke-Tool @('validate', $invalidUtf8, '--no-telemetry')) 3 'INVALID_UTF8'

    $oversizedInvalidUtf8 = Join-Path $temp 'oversized-invalid-utf8.json'
    $oversizedBytes = [byte[]]::new((2 * 1024 * 1024) + 1)
    $oversizedBytes[$oversizedBytes.Length - 1] = 0xFF
    [IO.File]::WriteAllBytes($oversizedInvalidUtf8, $oversizedBytes)
    Assert-Case 'oversized-auto-pre-read-bound' (Invoke-Tool @('validate', $oversizedInvalidUtf8, '--input', 'auto', '--no-telemetry')) 3 'INPUT_TOO_LARGE'
    Assert-Case 'oversized-schema-pre-read-bound' (Invoke-Tool @('snapshot', $oversizedInvalidUtf8, '--input', 'opencli', '--output', (Join-Path $temp 'oversized-output.json'), '--no-telemetry')) 3 'INPUT_TOO_LARGE'
    Assert-Case 'oversized-baseline-pre-read-bound' (Invoke-Tool @('check', $source, '--baseline', $oversizedInvalidUtf8, '--no-telemetry')) 3 'INPUT_TOO_LARGE'

    $deep = '0'
    $deep = '"SENSITIVE_DEPTH_SENTINEL"'
    for ($index = 0; $index -lt 100; $index++) { $deep = '{"nested":' + $deep + '}' }
    $deepInput = '{"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":' + $deep + '}'
    $deepPath = Join-Path $temp 'deep.json'
    Write-Utf8 $deepPath $deepInput
    $deepAuto = Invoke-Tool @('validate', $deepPath, '--input', 'auto', '--no-telemetry')
    Assert-Case 'deep-json-auto-depth-limit' $deepAuto 3 'DEPTH_LIMIT'
    if (($deepAuto.Output -join "`n") -match 'SENSITIVE_DEPTH_SENTINEL') { throw 'deep-json-auto-depth-limit echoed input content.' }
    Assert-Case 'deep-json-schema-depth-limit' (Invoke-Tool @('validate', $deepPath, '--input', 'opencli', '--no-telemetry')) 3 'DEPTH_LIMIT'
    Write-Output 'CONTRACT_REGRESSIONS=PASS'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
    if ($null -eq $previousNoTelemetry) { Remove-Item Env:KEELMATRIX_NO_TELEMETRY -ErrorAction SilentlyContinue }
    else { $env:KEELMATRIX_NO_TELEMETRY = $previousNoTelemetry }
}
