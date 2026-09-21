$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = (Get-ChildItem "$root\src\KeelMatrix.CliContract.Core" -Filter *.cs -File | ForEach-Object { Get-Content -Raw $_.FullName }) -join [Environment]::NewLine
$forbidden = @('System.Diagnostics.Process', 'ProcessStartInfo', 'System.Net.', 'HttpClient', 'WebClient', 'Socket', 'Assembly.Load', 'Activator.CreateInstance')
$hits = foreach ($term in $forbidden) { if ($source.IndexOf($term, [StringComparison]::Ordinal) -ge 0) { $term } }
if ($hits) { throw "Forbidden runtime reference(s): $($hits -join ', ')" }
Write-Output 'PASS: Core normalization source has no process-start, network, dynamic-load, or activation reference.'
