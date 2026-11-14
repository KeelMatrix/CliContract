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
