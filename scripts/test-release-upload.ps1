param([string] $ScratchDirectory = [IO.Path]::GetTempPath())

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = Split-Path -Parent $PSScriptRoot
$boundary = [IO.Path]::GetFullPath($ScratchDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$temp = Join-Path $boundary ('clicontract-upload-' + [Guid]::NewGuid().ToString('N'))
$commit = '0123456789012345678901234567890123456789'
$key = 'loopback-test-value'
$count = 0
Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// A local-only NuGet endpoint. HttpListener decodes chunked request bodies.
public sealed class LoopbackNuGetFeed : IDisposable
{
    public sealed record Request(string Method, string Path, byte[] Body, string ContentType, string ApiKey);
    private readonly HttpListener listener = new();
    private readonly Task worker;
    public readonly ConcurrentQueue<Request> Requests = new();
    public string Source { get; }

    public LoopbackNuGetFeed(string resources, int mainStatus, int symbolStatus, int indexStatus = 200)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var origin = $"http://127.0.0.1:{port}";
        Source = origin + "/v3/index.json";
        listener.Prefixes.Add(origin + "/");
        listener.Start();
        worker = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                var request = context.Request;
                using var body = new MemoryStream();
                await request.InputStream.CopyToAsync(body);
                var path = request.Url.AbsolutePath.TrimEnd('/');
                Requests.Enqueue(new Request(request.HttpMethod, path, body.ToArray(),
                    request.ContentType ?? "", request.Headers["X-NuGet-ApiKey"] ?? ""));
                var response = context.Response;
                byte[] content = Array.Empty<byte>();
                if (request.HttpMethod == "GET" && path == "/v3/index.json")
                {
                    response.StatusCode = indexStatus;
                    var entries = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, string>>();
                    if (resources != "missing-main")
                        entries.Add(new() { ["@id"] = origin + "/package", ["@type"] = "PackagePublish/2.0.0" });
                    if (resources != "missing-symbol")
                        entries.Add(new() { ["@id"] = origin + "/symbols", ["@type"] = resources == "wrong-symbol" ? "SymbolPackagePublish/99.0.0" : "SymbolPackagePublish/4.9.0" });
                    content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = "3.0.0", resources = entries }));
                    response.ContentType = "application/json";
                }
                else if (request.HttpMethod == "PUT" && (path == "/package" || path == "/symbols"))
                {
                    var status = path == "/package" ? mainStatus : symbolStatus;
                    if (status == -1) { response.Abort(); continue; }
                    response.StatusCode = status;
                }
                else { response.StatusCode = 404; }
                response.ContentLength64 = content.Length;
                await response.OutputStream.WriteAsync(content);
                response.Close();
            }
        });
    }

    public void Dispose()
    {
        listener.Close();
        if (!worker.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Local NuGet fixture did not stop.");
        worker.GetAwaiter().GetResult();
    }
}

'@

function Get-Payload([LoopbackNuGetFeed+Request] $Request) {
    if ($Request.ContentType -notmatch '^multipart/form-data;\s*boundary="?([^";]+)"?$') { throw 'Upload is not multipart form data.' }
    $delimiter = $Matches[1]
    $text = [Text.Encoding]::Latin1.GetString($Request.Body)
    $start = $text.IndexOf("`r`n`r`n", [StringComparison]::Ordinal) + 4
    $end = $text.IndexOf("`r`n--$delimiter", $start, [StringComparison]::Ordinal)
    if ($start -lt 4 -or $end -lt $start) { throw 'Missing multipart package payload.' }
    return ,([Text.Encoding]::Latin1.GetBytes($text.Substring($start, $end - $start)))
}

function Invoke-UploadCase([string] $Name, [string] $Resources = 'both', [int] $MainStatus = 201, [int] $SymbolStatus = 201,
    [int] $IndexStatus = 200, [bool] $Success = $false, [string[]] $ExpectedUploads = @(), [switch] $DirectSource,
    [string] $ExpectedError = '') {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $feed = [LoopbackNuGetFeed]::new($Resources, $MainStatus, $SymbolStatus, $IndexStatus)
    try {
        $env:NUGET_HTTP_CACHE_PATH = Join-Path $temp "http-cache/$Name"
        # Only destinations and expensive preparation checks change in this fixture.
        # Both actual push calls, their receipt guard and the caller execute unchanged.
        $source = $feed.Source
        $script = (Get-Content -Raw (Join-Path $root 'scripts/publish-release.ps1')).Replace('https://api.nuget.org/v3/index.json', $source).
            Replace('https://symbols.nuget.org/upload', $source.Replace('/v3/index.json', '/upload'))
        if ($DirectSource) {
            $direct = $source.Replace('/v3/index.json', '/upload')
            $script = $script.Replace("'$source'", "`$(if (`$Kind -eq 'symbol') { '$direct' } else { '$source' })")
        }
        [IO.File]::WriteAllText((Join-Path $temp 'scripts/publish-release.ps1'), $script)
        Push-Location $temp
        try {
            $output = @(Invoke-NestedPwsh -NoProfile -File ./scripts/publish-release.ps1 -Version 0.1.0 -Tag v0.1.0 -ExpectedCommit $commit -SkipPreparation -Publish 2>&1)
            $code = $LASTEXITCODE
        }
        finally { Pop-Location }
        $requests = @($feed.Requests.ToArray())
        $puts = @($requests | Where-Object Method -eq 'PUT')
        $paths = @($puts | ForEach-Object Path)
        if (($Success -and $code -ne 0) -or (-not $Success -and $code -eq 0) -or
            (($output -join "`n") -match 'RELEASE_PUBLICATION=PASS') -ne $Success -or
            ($paths -join ',') -cne ($ExpectedUploads -join ',') -or
            ($ExpectedError -and ($output -join "`n") -notlike "*$ExpectedError*")) {
            throw "Upload case $Name failed: exit=$code PUT=[$($paths -join ',')] output=$($output -join "`n")"
        }
        if (-not $DirectSource -and @($requests | Where-Object { $_.Method -eq 'GET' -and $_.Path -eq '/v3/index.json' }).Count -eq 0) { throw 'NuGet did not discover the service index.' }
        foreach ($put in $puts) {
            $extension = if ($put.Path -eq '/package') { 'nupkg' } else { 'snupkg' }
            $expected = [IO.File]::ReadAllBytes((Join-Path $temp "artifacts/packages/KeelMatrix.CliContract.0.1.0.$extension"))
            $actual = Get-Payload $put
            if ([Convert]::ToHexString($actual) -cne [Convert]::ToHexString($expected) -or $put.ApiKey -cne $key) { throw 'Upload changed package bytes or lost the explicit test key.' }
        }
        $script:count++
        Write-Output "RELEASE_UPLOAD_CASE=PASS name=$Name exit=$code uploads=$($puts.Count) exact_bytes=true duration_ms=$($watch.ElapsedMilliseconds)"
    }
    finally { $feed.Dispose() }
}

$oldKey = $env:RELEASE_NUGET_CREDENTIAL
$oldCache = $env:NUGET_HTTP_CACHE_PATH
try {
    New-Item -ItemType Directory -Path (Join-Path $temp 'scripts'), (Join-Path $temp 'build'), (Join-Path $temp 'artifacts/packages') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $temp 'build/Invoke-NestedPwsh.ps1'), (Get-Content -Raw (Join-Path $root 'build/Invoke-NestedPwsh.ps1')))
    foreach ($name in @('verify-release-contract', 'verify-release-artifacts')) {
        [IO.File]::WriteAllText((Join-Path $temp "scripts/$name.ps1"), 'exit 0')
    }
    [IO.File]::WriteAllText((Join-Path $temp 'NuGet.config'), '<configuration><packageSources><clear /></packageSources></configuration>')
    [IO.File]::WriteAllText((Join-Path $temp 'global.json'), (Get-Content -Raw (Join-Path $root 'global.json')))
    foreach ($extension in @('nupkg', 'snupkg')) {
        $path = Join-Path $temp "artifacts/packages/KeelMatrix.CliContract.0.1.0.$extension"
        $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry('KeelMatrix.CliContract.nuspec').Open())
            $writer.Write('<package><metadata><id>KeelMatrix.CliContract</id><version>0.1.0</version><authors>KeelMatrix</authors><description>Local protocol fixture</description></metadata></package>')
            $writer.Dispose()
            $stream = $zip.CreateEntry("payload.$extension").Open()
            $stream.Write([byte[]](0..255)); $stream.Dispose()
        }
        finally { $zip.Dispose() }
    }
    $env:RELEASE_NUGET_CREDENTIAL = $key
    Invoke-UploadCase 'created-pair' -Success $true -ExpectedUploads '/package', '/symbols'
    Invoke-UploadCase 'accepted-main' -MainStatus 202 -Success $true -ExpectedUploads '/package', '/symbols'
    Invoke-UploadCase 'missing-symbol-service' -Resources 'missing-symbol' -ExpectedUploads '/package' -ExpectedError 'no accepted upload receipt'
    Invoke-UploadCase 'unknown-symbol-service' -Resources 'wrong-symbol' -ExpectedUploads '/package' -ExpectedError 'no accepted upload receipt'
    Invoke-UploadCase 'missing-main-service' -Resources 'missing-main'
    Invoke-UploadCase 'index-not-found' -IndexStatus 404
    Invoke-UploadCase 'direct-source-no-op' -DirectSource -ExpectedUploads '/package' -ExpectedError 'no accepted upload receipt'
    foreach ($status in @(400, 401, 403, 404, 409, 413)) {
        Invoke-UploadCase "main-rejected-$status" -MainStatus $status -ExpectedUploads '/package'
        Invoke-UploadCase "symbols-rejected-$status" -SymbolStatus $status -ExpectedUploads '/package', '/symbols'
    }
    Invoke-UploadCase 'main-unexpected-ok' -MainStatus 200 -ExpectedUploads '/package'
    Invoke-UploadCase 'symbols-unexpected-ok' -SymbolStatus 200 -ExpectedUploads '/package', '/symbols'
    Invoke-UploadCase 'main-unexpected-no-content' -MainStatus 204 -ExpectedUploads '/package'
    Invoke-UploadCase 'symbols-unexpected-no-content' -SymbolStatus 204 -ExpectedUploads '/package', '/symbols'
    Invoke-UploadCase 'symbols-unexpected-accepted' -SymbolStatus 202 -ExpectedUploads '/package', '/symbols'
    Invoke-UploadCase 'main-server-error' -MainStatus 500 -ExpectedUploads '/package', '/package', '/package'
    Invoke-UploadCase 'symbols-server-error' -SymbolStatus 500 -ExpectedUploads '/package', '/symbols', '/symbols', '/symbols'
    Invoke-UploadCase 'main-transport-error' -MainStatus -1 -ExpectedUploads '/package', '/package', '/package'
    Invoke-UploadCase 'symbols-transport-error' -SymbolStatus -1 -ExpectedUploads '/package', '/symbols', '/symbols', '/symbols'
    Write-Output "RELEASE_UPLOAD_TESTS=PASS cases=$count"
}
finally {
    $env:RELEASE_NUGET_CREDENTIAL = $oldKey
    $env:NUGET_HTTP_CACHE_PATH = $oldCache
    $resolved = [IO.Path]::GetFullPath($temp)
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Upload test cleanup escaped the scratch directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
