param(
    [string[]] $AdditionalPath = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sourceExtensions = @('.cs', '.fs', '.vb', '.csproj', '.fsproj', '.vbproj', '.props', '.targets', '.sln', '.slnx')
$trackedFiles = @(git -C $root ls-files | Where-Object { $sourceExtensions -contains [IO.Path]::GetExtension($_).ToLowerInvariant() })
$files = @($trackedFiles | ForEach-Object { Join-Path $root $_ })
$files += @($AdditionalPath | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
$source = ($files | ForEach-Object { Get-Content -Raw -LiteralPath $_ }) -join [Environment]::NewLine
$forbidden = @('System.Diagnostics.Process', 'ProcessStartInfo', 'System.Net.', 'HttpClient', 'WebClient', 'Socket', 'Assembly.Load', 'Activator.CreateInstance')
$hits = foreach ($term in $forbidden) { if ($source.IndexOf($term, [StringComparison]::Ordinal) -ge 0) { $term } }
if ($hits) { throw "Forbidden reference(s) found in scanned files: $($hits -join ', ')" }
Write-Output "PASS: scanned $($files.Count) tracked source/config file(s) plus $($AdditionalPath.Count) explicit file(s); no process-start, network, dynamic-load, or activation reference found. This proves only the scanned files, not runtime behavior or untracked/future files."
