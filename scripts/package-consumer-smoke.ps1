param(
    [Parameter(Mandatory = $true)] [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-smoke-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $temp 'feed'
$toolPath = Join-Path $temp 'tool'
New-Item -ItemType Directory -Path $feed, $toolPath | Out-Null
Copy-Item -LiteralPath (Resolve-Path -LiteralPath $PackagePath) -Destination $feed
$oldLocation = Get-Location
try {
    $version = '0.1.0'
    $tool = Join-Path $toolPath 'clicontract.exe'
    $install = & dotnet tool install --tool-path $toolPath --add-source $feed --ignore-failed-sources --no-cache --version $version KeelMatrix.CliContract 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Packed tool installation failed: $($install -join ' ')" }
    if (-not (Test-Path -LiteralPath $tool)) { throw 'Installed tool command was not produced.' }
    Push-Location $temp
    $help = & $tool --help 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (($help -join "`n") -match 'snapshot')) { throw 'Installed tool help failed.' }

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
    $malformed = Join-Path $temp 'malformed.json'
    [IO.File]::WriteAllText($malformed, '{"opencliVersion":"1.0.0-alpha.14"}', [Text.UTF8Encoding]::new($false))
    & $tool validate $malformed --no-telemetry | Out-Host
    if ($LASTEXITCODE -ne 3) { throw 'Malformed packed-tool input did not return exit 3.' }
    $unsupported = Join-Path $temp 'unsupported.json'
    [IO.File]::WriteAllText($unsupported, $schema.Replace('1.0.0-alpha.14', '1.0.0-alpha.13'), [Text.UTF8Encoding]::new($false))
    & $tool validate $unsupported --no-telemetry | Out-Host
    if ($LASTEXITCODE -ne 3) { throw 'Unsupported-version packed-tool input did not return exit 3.' }
    Write-Output 'PACKAGE_CONSUMER_SMOKE=PASS isolated feed, clean tool path, no sibling repository or target executable.'
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
    Set-Location $oldLocation
}
