using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KeelMatrix.CliContract.Tests;

[CollectionDefinition("Tool boundary", DisableParallelization = true)]
public sealed class ToolBoundaryFixture;

[Collection("Tool boundary")]
public sealed class ToolBoundaryTests
{
    [Fact]
    public void DiffUsesOneInputRoleModelAcrossSourceCanonicalAndMixedOperands()
    {
        using var files = new TestFiles();
        var source = files.Write("source.json", SourceJson("old"));
        var changed = files.Write("changed.json", SourceJson("new"));
        var canonical = files.Path("source.canonical.json");
        Assert.Equal(0, Run("snapshot", source, "--input", "opencli", "--output", canonical, "--no-telemetry"));

        Assert.Equal(0, Run("diff", source, changed, "--input", "auto", "--no-telemetry"));
        Assert.Equal(0, Run("diff", canonical, canonical, "--input", "auto", "--no-telemetry"));
        Assert.Equal(0, Run("diff", source, canonical, "--input", "auto", "--no-telemetry"));
        Assert.Equal(0, Run("diff", canonical, source, "--input", "auto", "--no-telemetry"));

        Assert.Equal(3, Run("diff", canonical, source, "--input", "opencli", "--no-telemetry"));
        Assert.Equal(3, Run("diff", source, canonical, "--input", "opencli", "--no-telemetry"));
        Assert.Equal(3, Run("snapshot", canonical, "--input", "auto", "--output", files.Path("bad-snapshot.json"), "--no-telemetry"));
        Assert.Equal(3, Run("validate", canonical, "--input", "auto", "--no-telemetry"));

        var malformedCanonical = files.Write("malformed-canonical.json", "{\"SchemaVersion\":2}");
        Assert.Equal(3, Run("diff", malformedCanonical, canonical, "--input", "auto", "--no-telemetry"));
        Assert.Equal(3, Run("diff", malformedCanonical, source, "--input", "opencli", "--no-telemetry"));
        var ambiguous = files.Write("ambiguous.json", "{\"opencliVersion\":\"1.0.0-alpha.14\",\"SchemaVersion\":2}");
        Assert.Equal(3, Run("diff", ambiguous, source, "--input", "auto", "--no-telemetry"));
    }

    [Fact]
    public void TextOutputEscapesUnicodeFormatControlsAndWorkflowMarkersAtTheBoundary()
    {
        using var files = new TestFiles();
        var hostile = "alias\u202Ertl\u2066isolate\u200Bzero\u001Besc\u0001c0\u0085c1\u2028line\u2029para::marker";
        var oldPath = files.Write("old.json", SourceJson(null, hostile));
        var newPath = files.Write("new.json", SourceJson(null));
        var result = Capture(() => CliApplication.Run(["diff", oldPath, newPath, "--input", "opencli", "--no-telemetry"]));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("\\u202E", result.Output);
        Assert.Contains("\\u2066", result.Output);
        Assert.Contains("\\u200B", result.Output);
        Assert.Contains("\\u001B", result.Output);
        Assert.Contains("\\u0001", result.Output);
        Assert.Contains("\\u0085", result.Output);
        Assert.Contains("\\u2028", result.Output);
        Assert.Contains("\\u2029", result.Output);
        Assert.Contains("\\u003A\\u003A", result.Output);
        Assert.DoesNotContain(hostile, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("::warning::", result.Output, StringComparison.Ordinal);

        var invalid = Capture(() => CliApplication.Run(["validate", oldPath, "--input", "opencli", "--bad\u202Earg", "--no-telemetry"]));
        Assert.Equal(2, invalid.ExitCode);
        Assert.Contains("\\u202E", invalid.Output);
        Assert.DoesNotContain("\u202E", invalid.Output, StringComparison.Ordinal);
    }

    private static int Run(params string[] args) => CliApplication.Run(args);

    private static string SourceJson(string? summary, string? alias = null)
    {
        var flag = new JsonObject { ["name"] = "value", ["type"] = "string" };
        if (alias is not null) flag["aliases"] = new JsonArray(alias);
        var document = new JsonObject
        {
            ["opencliVersion"] = "1.0.0-alpha.14",
            ["info"] = new JsonObject { ["title"] = "Tool", ["binary"] = "tool", ["version"] = "1" },
            ["commands"] = new JsonObject
            {
                ["tool"] = new JsonObject { ["flags"] = new JsonArray(flag) }
            }
        };
        if (summary is not null) document["commands"]!["tool"]!["summary"] = summary;
        return document.ToJsonString();
    }

    private static CapturedResult Capture(Func<int> action)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = action();
            return new CapturedResult(exitCode, stdout.ToString() + stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record CapturedResult(int ExitCode, string Output);

    private sealed class TestFiles : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clicontract-tests-" + Guid.NewGuid().ToString("N"));

        public TestFiles() => Directory.CreateDirectory(_root);

        public string Path(string name) => System.IO.Path.Combine(_root, name);

        public string Write(string name, string content)
        {
            var path = Path(name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
