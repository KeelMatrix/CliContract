$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$normalizerProject = Join-Path $root 'src/KeelMatrix.CliContract.Harness/KeelMatrix.CliContract.Harness.csproj'
$fixture = Join-Path $root 'fixtures/opencli/example-cli.json'
$reordered = Join-Path $root 'fixtures/opencli/example-cli-reordered.json'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-example-cli-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    function Invoke-Normalization {
        param(
            [string] $InputPath,
            [string] $OutputPath
        )

        $output = & dotnet run --project $normalizerProject -c Release --no-restore -- normalize opencli $InputPath $OutputPath 2>&1
        $exitCode = $LASTEXITCODE
        $output | Out-Host
        if ($exitCode -ne 0) {
            throw "Normalization failed for $InputPath with native exit code $exitCode."
        }

        if (-not (Test-Path -LiteralPath $OutputPath) -or (Get-Item -LiteralPath $OutputPath).Length -eq 0) {
            throw "Normalization produced no output for $InputPath."
        }
    }

    Invoke-Normalization $fixture (Join-Path $temp 'first.json')
    Invoke-Normalization $fixture (Join-Path $temp 'second.json')
    Invoke-Normalization $reordered (Join-Path $temp 'reordered.json')
    $crlf = Join-Path $temp 'crlf.json'
    [IO.File]::WriteAllText($crlf, ([IO.File]::ReadAllText($fixture)).Replace("`n", "`r`n"))
    Invoke-Normalization $crlf (Join-Path $temp 'crlf-output.json')
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
