param(
    [string[]] $AdditionalPath = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sourceExtensions = @('.cs', '.fs', '.vb', '.csproj', '.fsproj', '.vbproj', '.props', '.targets', '.sln', '.slnx')
$gitOutput = @(git -C $root ls-files 2>&1)
$gitExitCode = $LASTEXITCODE
if ($gitExitCode -ne 0) {
    $detail = ($gitOutput -join ' ').Trim()
    throw "Could not enumerate tracked source/config files with git ls-files: $detail"
}

$trackedFiles = @($gitOutput | Where-Object { $sourceExtensions -contains [IO.Path]::GetExtension($_).ToLowerInvariant() })
$files = @($trackedFiles | ForEach-Object { Join-Path $root $_ })
$explicitFiles = @($AdditionalPath | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
$files += $explicitFiles
if ($files.Count -eq 0) {
    throw 'No tracked source/config files or explicit files were selected for scanning.'
}

$source = ($files | ForEach-Object { Get-Content -Raw -LiteralPath $_ }) -join [Environment]::NewLine
$forbidden = @('System.Diagnostics.Process', 'ProcessStartInfo', 'System.Net.', 'HttpClient', 'WebClient', 'Socket', 'Assembly.Load', 'Activator.CreateInstance')
$hits = foreach ($term in $forbidden) { if ($source.IndexOf($term, [StringComparison]::Ordinal) -ge 0) { $term } }
if ($hits) { throw "Forbidden reference(s) found in scanned files: $($hits -join ', ')" }
Write-Output "PASS: scanned $($trackedFiles.Count) tracked source/config file(s) plus $($explicitFiles.Count) explicit file(s); no process-start, network, dynamic-load, or activation reference found. This proves only the scanned files, not runtime behavior or untracked/future files."
