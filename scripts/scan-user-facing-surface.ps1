param(
    [string] $RootPath = (Split-Path -Parent $PSScriptRoot),
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RootPath).Path

$nonTextExceptions = [ordered]@{
    'icon.png' = 'binary PNG asset; textual wording does not apply'
    'scripts/scan-history-wording.ps1' = 'commit-message guard contains match literals required to detect disallowed metadata and wording'
    'scripts/scan-user-facing-surface.ps1' = 'surface guard contains match literals required to detect disallowed wording'
}

function Invoke-GitBytes {
    param(
        [Parameter(Mandatory = $true)] [string] $WorkingDirectory,
        [Parameter(Mandatory = $true)] [string[]] $Arguments
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdout = [IO.MemoryStream]::new()
    try {
        if (-not $process.Start()) { throw 'Could not start git.' }
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        [void] $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw ("git {0} failed with exit code {1}: {2}" -f ($Arguments -join ' '), $process.ExitCode, $stderr.Trim())
        }
        return ,$stdout.ToArray()
    }
    finally {
        $stdout.Dispose()
        $process.Dispose()
    }
}

function ConvertFrom-Utf8Strict {
    param([byte[]] $Bytes)
    try {
        return [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    }
    catch {
        throw 'invalid UTF-8 byte sequence'
    }
}

function Get-NulRecords {
    param([byte[]] $Bytes)

    $records = [Collections.Generic.List[byte[]]]::new()
    $start = 0
    for ($index = 0; $index -lt $Bytes.Length; $index++) {
        if ($Bytes[$index] -ne 0) { continue }
        if ($index -eq $start) { throw 'git returned an empty tracked-file record' }
        $record = [byte[]]::new($index - $start)
        [Buffer]::BlockCopy($Bytes, $start, $record, 0, $record.Length)
        $records.Add($record)
        $start = $index + 1
    }
    if ($start -ne $Bytes.Length) { throw 'git returned an unterminated tracked-file record' }
    return ,$records
}

function ConvertTo-ByteSlice {
    param(
        [byte[]] $Bytes,
        [int] $StartIndex,
        [int] $Length
    )
    $slice = [byte[]]::new($Length)
    if ($Length -gt 0) { [Buffer]::BlockCopy($Bytes, $StartIndex, $slice, 0, $Length) }
    return ,$slice
}

function Test-BytePrefix {
    param(
        [byte[]] $Bytes,
        [byte[]] $Prefix
    )
    if ($Bytes.Length -lt $Prefix.Length) { return $false }
    for ($index = 0; $index -lt $Prefix.Length; $index++) {
        if ($Bytes[$index] -ne $Prefix[$index]) { return $false }
    }
    return $true
}

function Test-AllowedTextCharacters {
    param([string] $Text)
    foreach ($character in $Text.ToCharArray()) {
        if ([char]::IsControl($character) -and $character -notin @([char] 0x09, [char] 0x0a, [char] 0x0d)) {
            return $false
        }
    }
    return $true
}

function Try-DecodeText {
    param(
        [byte[]] $Bytes,
        [Text.Encoding] $Encoding,
        [int] $Offset = 0
    )
    try {
        $payload = ConvertTo-ByteSlice -Bytes $Bytes -StartIndex $Offset -Length ($Bytes.Length - $Offset)
        $text = $Encoding.GetString($payload)
        if (-not (Test-AllowedTextCharacters -Text $text)) { return $null }
        return $text
    }
    catch {
        return $null
    }
}

function Get-NullRatio {
    param(
        [byte[]] $Bytes,
        [int] $Modulo,
        [int] $Position
    )
    $total = 0
    $zeros = 0
    for ($index = $Position; $index -lt $Bytes.Length; $index += $Modulo) {
        $total++
        if ($Bytes[$index] -eq 0) { $zeros++ }
    }
    if ($total -eq 0) { return 0.0 }
    return [double] $zeros / $total
}

function Decode-TrackedFile {
    param([string] $Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) { return '' }

    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $utf16le = [Text.UnicodeEncoding]::new($false, $false, $true)
    $utf16be = [Text.UnicodeEncoding]::new($true, $false, $true)
    $utf32le = [Text.UTF32Encoding]::new($false, $false, $true)
    $utf32be = [Text.UTF32Encoding]::new($true, $false, $true)

    try {
        if (Test-BytePrefix -Bytes $bytes -Prefix ([byte[]](0xff, 0xfe, 0x00, 0x00))) { $text = Try-DecodeText -Bytes $bytes -Encoding $utf32le -Offset 4 }
        elseif (Test-BytePrefix -Bytes $bytes -Prefix ([byte[]](0x00, 0x00, 0xfe, 0xff))) { $text = Try-DecodeText -Bytes $bytes -Encoding $utf32be -Offset 4 }
        elseif (Test-BytePrefix -Bytes $bytes -Prefix ([byte[]](0xef, 0xbb, 0xbf))) { $text = Try-DecodeText -Bytes $bytes -Encoding $utf8 -Offset 3 }
        elseif (Test-BytePrefix -Bytes $bytes -Prefix ([byte[]](0xff, 0xfe))) { $text = Try-DecodeText -Bytes $bytes -Encoding $utf16le -Offset 2 }
        elseif (Test-BytePrefix -Bytes $bytes -Prefix ([byte[]](0xfe, 0xff))) { $text = Try-DecodeText -Bytes $bytes -Encoding $utf16be -Offset 2 }
        else {
            $text = $null
            if ($bytes.Length % 4 -eq 0 -and (Get-NullRatio -Bytes $bytes -Modulo 4 -Position 1) -ge 0.75 -and (Get-NullRatio -Bytes $bytes -Modulo 4 -Position 2) -ge 0.75 -and (Get-NullRatio -Bytes $bytes -Modulo 4 -Position 3) -ge 0.75) {
                $text = Try-DecodeText -Bytes $bytes -Encoding $utf32le
            }
            elseif ($bytes.Length % 4 -eq 0 -and (Get-NullRatio -Bytes $bytes -Modulo 4 -Position 0) -ge 0.75 -and (Get-NullRatio -Bytes $bytes -Modulo 4 -Position 1) -ge 0.75 -and (Get-NullRatio -Bytes $bytes -Modulo 4 -Position 2) -ge 0.75) {
                $text = Try-DecodeText -Bytes $bytes -Encoding $utf32be
            }
            elseif ($bytes.Length % 2 -eq 0 -and (Get-NullRatio -Bytes $bytes -Modulo 2 -Position 1) -ge 0.50) {
                $text = Try-DecodeText -Bytes $bytes -Encoding $utf16le
            }
            elseif ($bytes.Length % 2 -eq 0 -and (Get-NullRatio -Bytes $bytes -Modulo 2 -Position 0) -ge 0.50) {
                $text = Try-DecodeText -Bytes $bytes -Encoding $utf16be
            }
            if ($null -eq $text) { $text = Try-DecodeText -Bytes $bytes -Encoding $utf8 }
        }
        if ($null -eq $text) { throw 'unsupported or invalid text encoding' }
        return $text
    }
    catch {
        throw "Could not decode tracked text file '$Path': $($_.Exception.Message)"
    }
}

function Get-TrackedTextFiles {
    param([string] $ScanRoot)

    $gitBytes = Invoke-GitBytes -WorkingDirectory $ScanRoot -Arguments @('ls-files', '--full-name', '-z')
    $records = Get-NulRecords -Bytes $gitBytes
    if ($records.Count -eq 0) { throw 'The repository has no tracked files to scan.' }
    for ($index = 0; $index -lt $records.Count; $index++) {
        $record = $records[$index]
        $relativePath = ConvertFrom-Utf8Strict $record
        if ([string]::IsNullOrWhiteSpace($relativePath)) { throw 'Git returned an empty tracked path.' }
        if ($nonTextExceptions.Contains($relativePath)) { continue }
        $path = Join-Path $ScanRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Tracked file is missing from the checkout: $relativePath" }
        $relativePath
    }
}

function ConvertTo-ScanText {
    param([string] $Text)

    $normalized = $Text.Normalize([Text.NormalizationForm]::FormKC)
    $normalized = [regex]::Replace($normalized, '[\u00ad\u034f\u061c\u180e\u200b-\u200f\u202a-\u202e\u2060\u2066-\u2069\ufeff]', '')
    $map = @{
        ([char] 0x0430) = 'a'; ([char] 0x0410) = 'A'; ([char] 0x0435) = 'e'; ([char] 0x0415) = 'E'
        ([char] 0x043e) = 'o'; ([char] 0x041e) = 'O'; ([char] 0x0440) = 'p'; ([char] 0x0420) = 'P'
        ([char] 0x0441) = 'c'; ([char] 0x0421) = 'C'; ([char] 0x0445) = 'x'; ([char] 0x0425) = 'X'
        ([char] 0x0456) = 'i'; ([char] 0x0406) = 'I'; ([char] 0x0458) = 'j'; ([char] 0x0408) = 'J'
        ([char] 0x03b1) = 'a'; ([char] 0x0391) = 'A'; ([char] 0x03bf) = 'o'; ([char] 0x039f) = 'O'
        ([char] 0x03c1) = 'p'; ([char] 0x03a1) = 'P'; ([char] 0x03b5) = 'e'; ([char] 0x0395) = 'E'
    }
    $builder = [Text.StringBuilder]::new($normalized.Length)
    foreach ($character in $normalized.ToCharArray()) {
        if ($map.ContainsKey($character)) { [void] $builder.Append($map[$character]) }
        else { [void] $builder.Append($character) }
    }
    return $builder.ToString()
}

function New-WrappedLiteral {
    param([string] $Value)
    $lineWrap = '(?:[\r\n\u2028\u2029][\s\p{Z}]*)?'
    return (($Value.ToCharArray() | ForEach-Object { [regex]::Escape([string] $_) }) -join $lineWrap)
}

function New-WrappedTokenPattern {
    param([string] $Value)
    return '(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value $Value) + '(?![\p{L}\p{N}_])'
}

function Get-ForbiddenPatterns {
    $patterns = [Collections.Generic.List[object]]::new()
    foreach ($value in @('probe', 'evidence', 'orchestration', 'Codex', 'Paperclip', 'frontier', 'agent', 'model', 'founder', 'acceptance', 'task')) {
        $patterns.Add([pscustomobject]@{ Name = $value; Pattern = '(?i)' + (New-WrappedTokenPattern -Value $value) })
    }
    $patterns.Add([pscustomobject]@{ Name = 'phase-0'; Pattern = '(?i)(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value 'phase') + '[\s\p{Z}\p{Pd}_]*0(?![\p{L}\p{N}_])' })
    $patterns.Add([pscustomobject]@{ Name = 'internal-error'; Pattern = '(?i)(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value 'INTERNAL') + '[\s\p{Z}\p{Pd}_]*' + (New-WrappedLiteral -Value 'ERROR') + '(?![\p{L}\p{N}_])' })
    $patterns.Add([pscustomobject]@{ Name = 'internal analysis error'; Pattern = '(?i)(?<![\p{L}\p{N}_])' + (New-WrappedLiteral -Value 'INTERNAL') + '[\s\p{Z}]+analysis[\s\p{Z}]+' + (New-WrappedLiteral -Value 'error') + '(?![\p{L}\p{N}_])' })
    $patterns.Add([pscustomobject]@{ Name = 'issue identifier'; Pattern = '(?i)(?<![\p{L}\p{N}_])K' + (New-WrappedLiteral -Value 'E') + (New-WrappedLiteral -Value 'E') + '[\s\p{Z}\p{Pd}_]*-?[\s\p{Z}]*\d(?:[\s\p{Z}]*\d)*(?![\p{L}\p{N}_])' })
    return $patterns.ToArray()
}

function Invoke-SurfaceScan {
    param([string] $ScanRoot)

    $surfaceFiles = @(Get-TrackedTextFiles -ScanRoot $ScanRoot)
    $patterns = @(Get-ForbiddenPatterns)
    Write-Output "SURFACE_INVENTORY_COUNT=$($surfaceFiles.Count)"
    foreach ($exception in $nonTextExceptions.GetEnumerator()) {
        Write-Output "SURFACE_EXCEPTION=$($exception.Key) reason=$($exception.Value)"
    }
    $surfaceFiles | ForEach-Object { Write-Output "SURFACE_FILE=$($_)" }
    $hits = [Collections.Generic.List[object]]::new()
    foreach ($relativePath in $surfaceFiles) {
        $path = Join-Path $ScanRoot $relativePath
        try { $content = Decode-TrackedFile -Path $path }
        catch { throw $_ }
        $content = ConvertTo-ScanText -Text $content
        foreach ($pattern in $patterns) {
            if ($content -match $pattern.Pattern) {
                $hits.Add([pscustomobject]@{ Path = $relativePath; Pattern = $pattern.Pattern })
            }
        }
    }

    if ($hits.Count -gt 0) {
        $hits | ForEach-Object { Write-Output "SURFACE_WORDING_HIT path=$($_.Path) pattern=$($_.Pattern)" }
        throw 'Forbidden internal or implementation wording found in the shipped/user-facing surface.'
    }

    Write-Output 'SURFACE_WORDING_SCAN=PASS'
}

function New-SurfaceFixture {
    param(
        [string] $Path,
        [byte[]] $Bytes
    )
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    & git -C $Path init --quiet
    & git -C $Path config user.name KeelMatrix
    & git -C $Path config user.email keelmatrix@gmail.com
    [IO.File]::WriteAllBytes((Join-Path $Path 'README.md'), $Bytes)
    & git -C $Path add README.md
    & git -C $Path commit --quiet -m clean
}

function Invoke-ChildSurfaceScan {
    param([string] $Path)
    $output = @(& pwsh -NoProfile -File $PSCommandPath -RootPath $Path 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
    }
}

function Assert-SurfaceChildReject {
    param(
        [string] $Name,
        [object] $Result
    )
    if ($Result.ExitCode -eq 0 -or $Result.Text -notmatch 'SURFACE_INVENTORY_COUNT=1' -or $Result.Text -notmatch 'SURFACE_FILE=README\.md') {
        throw "Surface wording self-test was vacuous or accepted fixture $Name."
    }
    Write-Output "SURFACE_WORDING_FIXTURE=$Name EXPECTED=REJECT exit=$($Result.ExitCode) inventory=1"
}

if ($SelfTest) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('clicontract-surface-' + [Guid]::NewGuid().ToString('N'))
    try {
        $utf8 = [Text.UTF8Encoding]::new($false)
        $utf8Bom = [Text.UTF8Encoding]::new($true)
        $utf16le = [Text.UnicodeEncoding]::new($false, $false, $true)
        $utf16be = [Text.UnicodeEncoding]::new($true, $false, $true)
        $utf32le = [Text.UTF32Encoding]::new($false, $false, $true)
        $utf32be = [Text.UTF32Encoding]::new($true, $false, $true)
        $cases = @(
            @{ Name = 'utf8-no-bom'; Bytes = $utf8.GetBytes('Paperclip') },
            @{ Name = 'utf8-bom'; Bytes = [byte[]]($utf8Bom.GetPreamble() + $utf8Bom.GetBytes('Paperclip')) },
            @{ Name = 'utf16le-no-bom'; Bytes = $utf16le.GetBytes('Paperclip') },
            @{ Name = 'utf16le-bom'; Bytes = [byte[]](([Text.Encoding]::Unicode.GetPreamble()) + $utf16le.GetBytes('Paperclip')) },
            @{ Name = 'utf16be-no-bom'; Bytes = $utf16be.GetBytes('Paperclip') },
            @{ Name = 'utf16be-bom'; Bytes = [byte[]](([Text.Encoding]::BigEndianUnicode.GetPreamble()) + $utf16be.GetBytes('Paperclip')) },
            @{ Name = 'utf32le-no-bom'; Bytes = $utf32le.GetBytes('Paperclip') },
            @{ Name = 'utf32le-bom'; Bytes = [byte[]](([Text.UTF32Encoding]::new($false, $true, $true).GetPreamble()) + $utf32le.GetBytes('Paperclip')) },
            @{ Name = 'utf32be-no-bom'; Bytes = $utf32be.GetBytes('Paperclip') },
            @{ Name = 'utf32be-bom'; Bytes = [byte[]](([Text.UTF32Encoding]::new($true, $true, $true).GetPreamble()) + $utf32be.GetBytes('Paperclip')) },
            @{ Name = 'invalid-utf8'; Bytes = [byte[]](0x50, 0x61, 0x70, 0x65, 0x72, 0xc3, 0x28, 0x63, 0x6c, 0x69, 0x70) },
            @{ Name = 'invalid-utf8-even'; Bytes = [byte[]](0x50, 0x61, 0x70, 0x65, 0x72, 0x63, 0x6c, 0x69, 0x70, 0xff) },
            @{ Name = 'line-wrap-crlf'; Bytes = $utf8.GetBytes("Paper`r`nclip") },
            @{ Name = 'zero-width'; Bytes = $utf8.GetBytes("Paper`u{200b}clip") },
            @{ Name = 'soft-hyphen'; Bytes = $utf8.GetBytes("Paper`u{00ad}clip") },
            @{ Name = 'word-joiner'; Bytes = $utf8.GetBytes("Paper`u{2060}clip") },
            @{ Name = 'cyrillic-confusable'; Bytes = $utf8.GetBytes("P`u{0430}perclip") }
        )
        foreach ($case in $cases) {
            $caseRoot = Join-Path $temp $case.Name
            New-SurfaceFixture -Path $caseRoot -Bytes $case.Bytes
            $result = Invoke-ChildSurfaceScan -Path $caseRoot
            Assert-SurfaceChildReject -Name $case.Name -Result $result
        }
        $sourceFiles = @(Get-TrackedTextFiles -ScanRoot $root)
        $nonVacuityRoot = Join-Path $temp 'non-vacuity'
        New-Item -ItemType Directory -Path $nonVacuityRoot -Force | Out-Null
        foreach ($relativePath in $sourceFiles) {
            $source = Join-Path $root $relativePath
            $target = Join-Path $nonVacuityRoot $relativePath
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath $source -Destination $target
        }
        & git -C $nonVacuityRoot init --quiet
        & git -C $nonVacuityRoot config user.name KeelMatrix
        & git -C $nonVacuityRoot config user.email keelmatrix@gmail.com
        & git -C $nonVacuityRoot add --all
        if ($LASTEXITCODE -ne 0) { throw 'Surface scan self-test could not create the tracked-file fixture.' }
        [IO.File]::AppendAllText((Join-Path $nonVacuityRoot 'README.md'), "`npr`obe`n")
        $child = Invoke-ChildSurfaceScan -Path $nonVacuityRoot
        if ($child.ExitCode -eq 0 -or $child.Text -notmatch 'SURFACE_INVENTORY_COUNT=104') { throw 'Surface wording gate accepted an injected forbidden term or had no tracked-file inventory.' }
        Write-Output "SURFACE_NON_VACUITY_CHILD_EXIT=$($child.ExitCode) inventory=104"
        Write-Output 'SURFACE_NON_VACUITY=PASS'
        Write-Output 'SURFACE_WORDING_REGRESSION_FIXTURES=PASS'
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
    }
}

Invoke-SurfaceScan -ScanRoot $root
