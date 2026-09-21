$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$probe = Join-Path $root 'src/KeelMatrix.CliContract.Probe/KeelMatrix.CliContract.Probe.csproj'
$fixture = Join-Path $root 'fixtures/opencli/phase0.json'
$reordered = Join-Path $root 'fixtures/opencli/phase0-reordered.json'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-phase0-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    dotnet run --project $probe -c Release --no-restore -- normalize opencli $fixture (Join-Path $temp 'first.json') | Out-Host
    dotnet run --project $probe -c Release --no-restore -- normalize opencli $fixture (Join-Path $temp 'second.json') | Out-Host
    dotnet run --project $probe -c Release --no-restore -- normalize opencli $reordered (Join-Path $temp 'reordered.json') | Out-Host
    $crlf = Join-Path $temp 'crlf.json'
    [IO.File]::WriteAllText($crlf, ([IO.File]::ReadAllText($fixture)).Replace("`n", "`r`n"))
    dotnet run --project $probe -c Release --no-restore -- normalize opencli $crlf (Join-Path $temp 'crlf-output.json') | Out-Host
    $hashes = Get-FileHash (Join-Path $temp '*.json') -Algorithm SHA256
    $hashes | Format-Table -AutoSize
    $firstHash = ($hashes | Where-Object Path -like '*first.json').Hash
    foreach ($name in 'second.json', 'reordered.json', 'crlf-output.json') {
        $actual = ($hashes | Where-Object Path -like "*$name").Hash
        if ($actual -ne $firstHash) { throw "Canonical output mismatch for $name." }
    }
    Write-Output 'PASS: repeated, reordered, and CRLF inputs produced identical canonical bytes.'
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
