using System.Text;
using KeelMatrix.CliContract.Core;
using Xunit;

namespace KeelMatrix.CliContract.Tests;

[CollectionDefinition("Telemetry activation", DisableParallelization = true)]
public sealed class TelemetryActivationFixture;

[Collection("Telemetry activation")]
public sealed class TelemetryActivationTests
{
    [Fact]
    public void RunnableSurfaceRequiresAnActionAndIgnoresCosmeticOrNonRunnableState()
    {
        var action = new CanonicalCommand
        {
            Path = "root",
            Kind = "action",
            Aliases = ["tool"],
            Summary = "summary",
            Description = "description",
            Hidden = true,
            Examples = [new CanonicalExample { Content = "tool" }]
        };
        var group = new CanonicalCommand
        {
            Path = "root",
            Kind = "group",
            Aliases = ["tool"],
            Summary = "summary",
            Description = "description",
            Hidden = true,
            Examples = [new CanonicalExample { Content = "tool" }],
            Arguments = [new CanonicalArgument { Name = "argument" }],
            Options = [new CanonicalOption { Name = "--option", Hidden = true }]
        };

        Assert.True(CliApplication.HasMeaningfulCommand(action));
        Assert.False(CliApplication.HasMeaningfulCommand(group));
        Assert.False(CliApplication.HasMeaningfulCommand(new CanonicalCommand { Path = "root", Kind = "group" }));
    }

    [Fact]
    public void RootOnlyJsonActionActivatesOnceForSnapshotCheckAndDiff()
    {
        using var temp = new TestFiles();
        var source = temp.Write("root.json", RootActionJson());
        var baseline = temp.Path("root.canonical.json");

        var snapshotSink = new ActivationSpy();
        using (ActivationTelemetry.UseTestSink(snapshotSink))
        {
            Assert.Equal(0, Run("snapshot", source, "--input", "opencli", "--output", baseline));
        }
        Assert.Equal(0, snapshotSink.Attempts);

        var checkSink = new ActivationSpy();
        using (ActivationTelemetry.UseTestSink(checkSink))
        {
            using var environment = ClearTelemetryEnvironment();
            Assert.Equal(0, Run("check", source, "--input", "opencli", "--baseline", baseline));
        }
        Assert.Equal(1, checkSink.Attempts);

        var diffSink = new ActivationSpy();
        using (ActivationTelemetry.UseTestSink(diffSink))
        {
            using var environment = ClearTelemetryEnvironment();
            Assert.Equal(0, Run("diff", source, source, "--input", "opencli"));
        }
        Assert.Equal(1, diffSink.Attempts);
    }

    [Fact]
    public void NestedJsonAndYamlActionsActivateExactlyOnce()
    {
        using var temp = new TestFiles();
        var json = temp.Write("nested.json", NestedActionJson());
        var yaml = temp.Write("nested.yaml", NestedActionYaml());
        var jsonBaseline = temp.Path("nested-json.canonical.json");
        var yamlBaseline = temp.Path("nested-yaml.canonical.json");

        var jsonSink = new ActivationSpy();
        using (ActivationTelemetry.UseTestSink(jsonSink))
        {
            using var environment = ClearTelemetryEnvironment();
            Assert.Equal(0, Run("snapshot", json, "--input", "opencli", "--output", jsonBaseline));
            Assert.Equal(0, Run("check", json, "--input", "opencli", "--baseline", jsonBaseline));
        }
        Assert.Equal(1, jsonSink.Attempts);

        var yamlSink = new ActivationSpy();
        using (ActivationTelemetry.UseTestSink(yamlSink))
        {
            using var environment = ClearTelemetryEnvironment();
            Assert.Equal(0, Run("snapshot", yaml, "--input", "opencli", "--output", yamlBaseline));
            Assert.Equal(0, Run("diff", yaml, yaml, "--input", "opencli"));
        }
        Assert.Equal(1, yamlSink.Attempts);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    public void GroupOnlyAndEmptyTreesNeverActivate(string format)
    {
        using var temp = new TestFiles();
        var group = temp.Write("group." + format, format == "json" ? GroupOnlyJson() : GroupOnlyYaml());
        var nestedGroup = temp.Write("nested-group." + format, format == "json" ? NestedGroupOnlyJson() : NestedGroupOnlyYaml());
        var empty = temp.Write("empty." + format, format == "json" ? EmptyJson() : EmptyYaml());

        var sink = new ActivationSpy();
        using (ActivationTelemetry.UseTestSink(sink))
        {
            using var environment = ClearTelemetryEnvironment();
            foreach (var (source, name) in new[] { (group, "group"), (nestedGroup, "nested-group"), (empty, "empty") })
            {
                var baseline = temp.Path(name + ".canonical.json");
                Assert.Equal(0, Run("snapshot", source, "--input", "opencli", "--output", baseline));
                Assert.Equal(0, Run("check", source, "--input", "opencli", "--baseline", baseline));
                Assert.Equal(0, Run("diff", source, source, "--input", "opencli"));
            }
        }

        Assert.Equal(0, sink.Attempts);
    }

    [Fact]
    public void CosmeticChangesDoNotChangeRunnableEligibility()
    {
        using var temp = new TestFiles();
        var oldSource = temp.Write("old.json", RootActionJson(summary: "old", description: "old description"));
        var newSource = temp.Write("new.json", RootActionJson(summary: "new", description: "new description"));
        var baseline = temp.Path("old.canonical.json");

        var sink = new ActivationSpy();
        using (ActivationTelemetry.UseTestSink(sink))
        {
            using var environment = ClearTelemetryEnvironment();
            Assert.Equal(0, Run("snapshot", oldSource, "--input", "opencli", "--output", baseline));
            Assert.Equal(0, Run("check", newSource, "--input", "opencli", "--baseline", baseline));
            Assert.Equal(0, Run("diff", oldSource, newSource, "--input", "opencli"));
        }

        Assert.Equal(2, sink.Attempts);
    }

    [Fact]
    public void ExplicitOptOutRemainsEffectiveAndCustomerCiCanActivate()
    {
        using var temp = new TestFiles();
        var source = temp.Write("root.json", RootActionJson());
        var baseline = temp.Path("root.canonical.json");
        var sink = new ActivationSpy();

        using (ActivationTelemetry.UseTestSink(sink))
        {
            using var environment = ClearTelemetryEnvironment();
            Assert.Equal(0, Run("snapshot", source, "--input", "opencli", "--output", baseline));
            Assert.Equal(0, Run("check", source, "--input", "opencli", "--baseline", baseline, "--no-telemetry"));
            EnvironmentScope.Set("CI", "true");
            Assert.Equal(0, Run("diff", source, source, "--input", "opencli"));
        }

        Assert.Equal(1, sink.Attempts);
    }

    private static int Run(params string[] args) => CliApplication.Run(args);

    private static string RootActionJson(string? summary = null, string? description = null)
    {
        var properties = new List<string>();
        if (summary is not null) properties.Add($"\"summary\":\"{summary}\"");
        if (description is not null) properties.Add($"\"description\":\"{description}\"");
        var command = string.Join(",", properties);
        var prefix = "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"Tool\",\"binary\":\"tool\",\"version\":\"1\"},\"commands\":{\"tool\":{";
        return prefix + command + "}}}";
    }

    private static string NestedActionJson() => "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"Tool\",\"binary\":\"tool\",\"version\":\"1\"},\"commands\":{\"tool group\":{\"kind\":\"group\"},\"tool group run\":{}}}";

    private static string GroupOnlyJson() => "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"Tool\",\"binary\":\"tool\",\"version\":\"1\"},\"commands\":{\"tool\":{\"kind\":\"group\",\"aliases\":[\"t\"]}}}";

    private static string NestedActionYaml() => "opencliVersion: 1.0.0-alpha.14\ninfo:\n  title: Tool\n  binary: tool\n  version: '1'\ncommands:\n  tool group:\n    kind: group\n  tool group run: {}\n";

    private static string GroupOnlyYaml() => "opencliVersion: 1.0.0-alpha.14\ninfo:\n  title: Tool\n  binary: tool\n  version: '1'\ncommands:\n  tool:\n    kind: group\n    aliases:\n      - t\n";

    private static string NestedGroupOnlyJson() => "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"Tool\",\"binary\":\"tool\",\"version\":\"1\"},\"commands\":{\"tool group\":{\"kind\":\"group\",\"aliases\":[\"g\"]}}}";

    private static string NestedGroupOnlyYaml() => "opencliVersion: 1.0.0-alpha.14\ninfo:\n  title: Tool\n  binary: tool\n  version: '1'\ncommands:\n  tool group:\n    kind: group\n    aliases:\n      - g\n";

    private static string EmptyJson() => "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"Tool\",\"binary\":\"tool\",\"version\":\"1\"}}";

    private static string EmptyYaml() => "opencliVersion: 1.0.0-alpha.14\ninfo:\n  title: Tool\n  binary: tool\n  version: '1'\n";

    private static EnvironmentScope ClearTelemetryEnvironment() => new("CI", "KEELMATRIX_NO_TELEMETRY");

    private sealed class ActivationSpy : IActivationSink
    {
        public int Attempts { get; private set; }

        public void TrackActivation()
        {
            Attempts++;
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> previous = [];

        public EnvironmentScope(params string[] names)
        {
            foreach (var name in names)
            {
                previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public static void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

        public void Dispose()
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    private sealed class TestFiles : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clicontract-tests-" + Guid.NewGuid().ToString("N"));

        public TestFiles() => Directory.CreateDirectory(root);

        public string Path(string name) => System.IO.Path.Combine(root, name);

        public string Write(string name, string content)
        {
            var path = Path(name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
