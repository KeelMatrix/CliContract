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
    Write-Utf8 $source $valid
    Write-Utf8 $duplicatePath $duplicate

    Assert-Case 'duplicate-opencli' (Invoke-Tool @('validate', $duplicatePath, '--input', 'opencli', '--no-telemetry')) 3 'DUPLICATE_JSON_KEY'
    Assert-Case 'duplicate-auto' (Invoke-Tool @('validate', $duplicatePath, '--input', 'auto', '--no-telemetry')) 3 'DUPLICATE_JSON_KEY'

    $badInputs = @{
        'bad-type' = $valid.Replace('"type":"string"', '"type":["string"]')
        'bad-min' = $valid.Replace('"type":"string"', '"type":"string","minItems":2,"maxItems":1')
        'empty-sources' = $valid.Replace('"type":"string"', '"type":"string","alternativeSources":[]')
        'remote-ref' = $valid.Replace('"type":"string"', '"type":"string","$ref":"https://example.invalid/schema"')
    }
    foreach ($name in $badInputs.Keys) {
        $path = Join-Path $temp ($name + '.json')
        Write-Utf8 $path $badInputs[$name]
        Assert-Case $name (Invoke-Tool @('validate', $path, '--input', 'auto', '--no-telemetry')) 3 $null
    }
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
