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
