using System.Text.Json.Nodes;
using KeelMatrix.CliContract.Core;
using Xunit;

namespace KeelMatrix.CliContract.Tests;

public sealed class CompatibilityTests
{
    [Fact]
    public void RemovedOptionIsBreakingAndAddedOptionIsInformational()
    {
        var oldManifest = Normalize("""{"commands":{"tool":{"flags":[{"name":"region","type":"string"}]}}}""");
        var newManifest = Normalize("""{"commands":{"tool":{"flags":[{"name":"format","type":"string"}]}}}""");

        var findings = CompatibilityAnalyzer.Compare(oldManifest, newManifest).Findings;

        Assert.Contains(findings, finding => finding.Code == "KMCLI101" && finding.Category == "breaking" && finding.Path == "root / --region");
        Assert.Contains(findings, finding => finding.Code == "KMCLI002" && finding.Category == "info" && finding.Path == "root / --format");
    }

    [Fact]
    public void RequirednessArityAliasDefaultAndDescriptionChangesUseStableCategories()
    {
        var oldManifest = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"string","aliases":["-v"],"required":false,"variadic":true,"maxItems":3,"default":"one","description":"old"}]}}}""");
        var newManifest = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"string","aliases":["-x"],"required":true,"variadic":true,"maxItems":1,"default":"two","description":"new"}]}}}""");

        var findings = CompatibilityAnalyzer.Compare(oldManifest, newManifest).Findings;

        Assert.Contains(findings, finding => finding.Code == "KMCLI105" && finding.Category == "breaking");
        Assert.Contains(findings, finding => finding.Code == "KMCLI106" && finding.Category == "breaking");
        Assert.Contains(findings, finding => finding.Code == "KMCLI104" && finding.Category == "breaking");
        Assert.Contains(findings, finding => finding.Code == "KMCLI004" && finding.Category == "info");
        Assert.Contains(findings, finding => finding.Code == "KMCLI201" && finding.Category == "warning");
        Assert.Contains(findings, finding => finding.Code == "KMCLI005" && finding.Category == "info");
    }

    [Fact]
    public void RequirednessChangeDoesNotAlsoReportImplicitArityNarrowing()
    {
        var optional = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"string","required":false}]}}}""");
        var required = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"string","required":true}]}}}""");

        var findings = CompatibilityAnalyzer.Compare(optional, required).Findings;

        Assert.Equal(["KMCLI105"], findings.Select(finding => finding.Code));
    }

    [Fact]
    public void PassthroughRemovalIsBreakingAndAdditionIsInformational()
    {
        var enabled = Normalize("{\"commands\":{\"tool run <rest>\":{\"args\":[{\"name\":\"rest\",\"passthrough\":true}]}}}");
        var disabled = Normalize("{\"commands\":{\"tool run <rest>\":{\"args\":[{\"name\":\"rest\",\"passthrough\":false}]}}}");

        var removed = CompatibilityAnalyzer.Compare(enabled, disabled).Findings;
        var added = CompatibilityAnalyzer.Compare(disabled, enabled).Findings;

        Assert.Contains(removed, finding => finding.Code == "KMCLI112" && finding.Category == "breaking");
        Assert.Contains(added, finding => finding.Code == "KMCLI112" && finding.Category == "info");
    }

    [Fact]
    public void PositionalArgumentReorderingIsBreaking()
    {
        var baseline = Normalize("""{"commands":{"tool run <first> <second>":{"args":[{"name":"first"},{"name":"second"}]}}}""");
        var current = Normalize("""{"commands":{"tool run <first> <second>":{"args":[{"name":"second"},{"name":"first"}]}}}""");

        var findings = CompatibilityAnalyzer.Compare(baseline, current).Findings;

        Assert.Contains(findings, finding => finding.Code == "KMCLI109" && finding.Category == "breaking" && finding.Path == "root / run");
    }

    [Fact]
    public void CompatibilityAnalyzerRejectsDuplicateCanonicalParameterNames()
    {
        var manifest = new CanonicalManifest
        {
            Adapter = "opencli",
            SourceVersion = Normalizer.OpenCliVersion,
            Root = new CanonicalCommand
            {
                Path = "root",
                Options =
                [
                    new CanonicalOption { Name = "--region" },
                    new CanonicalOption { Name = "--region" }
                ]
            }
        };

        var error = Assert.Throws<CompatibilityException>(() => CompatibilityAnalyzer.Compare(manifest, manifest));

        Assert.Equal("OPENCLI_DUPLICATE_PARAMETER", error.Code);
    }

    [Theory]
    [InlineData("root / run", "root / run")]
    [InlineData("root", "root")]
    public void CompatibilityAnalyzerRejectsDuplicateCanonicalCommandPaths(string firstPath, string secondPath)
    {
        var manifest = new CanonicalManifest
        {
            Adapter = "opencli",
            SourceVersion = Normalizer.OpenCliVersion,
            Root = new CanonicalCommand
            {
                Path = "root",
                Subcommands =
                [
                    new CanonicalCommand { Path = firstPath },
                    new CanonicalCommand { Path = secondPath }
                ]
            }
        };

        var error = Assert.Throws<CompatibilityException>(() => CompatibilityAnalyzer.Compare(manifest, manifest));

        Assert.Equal("OPENCLI_DUPLICATE_COMMAND_PATH", error.Code);
    }

    [Fact]
    public void CanonicalManifestReaderRejectsDuplicateCommandPaths()
    {
        var manifest = new CanonicalManifest
        {
            Adapter = "opencli",
            SourceVersion = Normalizer.OpenCliVersion,
            Root = new CanonicalCommand
            {
                Path = "root",
                Subcommands =
                [
                    new CanonicalCommand { Path = "root / run" },
                    new CanonicalCommand { Path = "root / run" }
                ]
            }
        };

        var error = Assert.Throws<NormalizationException>(() => CanonicalManifestReader.Read(Normalizer.Serialize(manifest)));

        Assert.Equal("OPENCLI_DUPLICATE_COMMAND_PATH", error.Code);
    }

    [Fact]
    public void DomainNarrowingIsBreakingButWideningIsNot()
    {
        var oldManifest = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"number","choices":[{"value":1},{"value":2}]}]}}}""");
        var narrowed = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"integer","choices":[{"value":1}]}]}}}""");
        var widened = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"number","choices":[{"value":1},{"value":2},{"value":3}]}]}}}""");

        Assert.Contains(CompatibilityAnalyzer.Compare(oldManifest, narrowed).Findings, finding => finding.Code == "KMCLI107");
        Assert.DoesNotContain(CompatibilityAnalyzer.Compare(oldManifest, widened).Findings, finding => finding.Code == "KMCLI107");
    }

    [Fact]
    public void RemovingAChoiceRemainsBreakingWhenAnotherChoiceIsAdded()
    {
        var baseline = Normalize("""{"commands":{"tool":{"flags":[{"name":"colour","type":"string","choices":[{"value":"red"},{"value":"blue"}]}]}}}""");
        var mixed = Normalize("""{"commands":{"tool":{"flags":[{"name":"colour","type":"string","choices":[{"value":"red"},{"value":"green"}]}]}}}""");
        var disjoint = Normalize("""{"commands":{"tool":{"flags":[{"name":"colour","type":"string","choices":[{"value":"green"},{"value":"yellow"}]}]}}}""");

        Assert.Contains(CompatibilityAnalyzer.Compare(baseline, mixed).Findings, finding => finding.Code == "KMCLI107" && finding.Category == "breaking");
        Assert.Contains(CompatibilityAnalyzer.Compare(baseline, disjoint).Findings, finding => finding.Code == "KMCLI107" && finding.Category == "breaking");
    }

    [Fact]
    public void PositionalInsertionBeforeOrBetweenExistingArgumentsIsBreakingButTrailingAdditionIsNot()
    {
        var baseline = Normalize("""{"commands":{"tool run <target>":{"args":[{"name":"target"}]}}}""");
        var prepended = Normalize("""{"commands":{"tool run <mode> <target>":{"args":[{"name":"mode"},{"name":"target"}]}}}""");
        var trailing = Normalize("""{"commands":{"tool run <target> <mode>":{"args":[{"name":"target"},{"name":"mode"}]}}}""");

        Assert.Contains(CompatibilityAnalyzer.Compare(baseline, prepended).Findings, finding => finding.Code == "KMCLI109" && finding.Category == "breaking");
        Assert.DoesNotContain(CompatibilityAnalyzer.Compare(baseline, trailing).Findings, finding => finding.Code == "KMCLI109");
    }

    [Fact]
    public void BinaryRenameAndActionToGroupAreInvocationBreakingChanges()
    {
        var baseline = Normalize("""{"commands":{"tool run":{"kind":"action"}}}""");
        var changed = NormalizeWithInfo("""{"commands":{"renamed run":{"kind":"group"}}}""", "renamed");

        var findings = CompatibilityAnalyzer.Compare(baseline, changed).Findings;

        Assert.Contains(findings, finding => finding.Code == "KMCLI110" && finding.Category == "breaking");
        Assert.Contains(findings, finding => finding.Code == "KMCLI111" && finding.Category == "breaking");
    }

    [Fact]
    public void DefaultSourcesAndGlobalFileConfigurationAreWarningsIncludingOrderChanges()
    {
        var baseline = NormalizeWithGlobal("""{"commands":{"tool":{"flags":[{"name":"format","type":"string","alternativeSources":[{"type":"$ENV","property":"FORMAT"},{"type":"$FILE","property":"$.format"}]}]}}}""", "FORMAT", "$.format", "config.json");
        var changed = NormalizeWithGlobal("""{"commands":{"tool":{"flags":[{"name":"format","type":"string","alternativeSources":[{"type":"$FILE","property":"$.format"},{"type":"$ENV","property":"FORMAT_NEW"}]}]}}}""", "FORMAT_NEW", "$.format", "other.json");

        var findings = CompatibilityAnalyzer.Compare(baseline, changed).Findings;

        Assert.Contains(findings, finding => finding.Code == "KMCLI204" && finding.Category == "warning" && finding.Path == "root / --format");
        Assert.Contains(findings, finding => finding.Code == "KMCLI204" && finding.Category == "warning" && finding.Path == "root");
    }

    [Fact]
    public void DistinctLargeNumericDefaultsRemainACompatibilityChange()
    {
        var baseline = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"number","default":16777216.0}]}}}""");
        var current = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"number","default":16777217.0}]}}}""");

        Assert.Contains(CompatibilityAnalyzer.Compare(baseline, current).Findings, finding => finding.Code == "KMCLI201" && finding.Category == "warning");
    }

    [Fact]
    public void CanonicalManifestRoundTripsAndRejectsUnknownVersion()
    {
        var manifest = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"string"}]}}}""");
        var serialized = Normalizer.Serialize(manifest);
        var roundTrip = CanonicalManifestReader.Read(serialized);

        Assert.Equal(serialized, Normalizer.Serialize(roundTrip));
        var unsupported = serialized.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 2", StringComparison.Ordinal);
        var error = Assert.Throws<NormalizationException>(() => CanonicalManifestReader.Read(unsupported));
        Assert.Equal("UNSUPPORTED_MANIFEST_VERSION", error.Code);
    }

    [Fact]
    public void BaselineSourceVersionMismatchFailsClosed()
    {
        var manifest = Normalize("""{"commands":{"tool":{}}}""");
        var changed = new CanonicalManifest { Adapter = manifest.Adapter, SourceVersion = "1.0.0-alpha.13", Root = manifest.Root };

        var error = Assert.Throws<CompatibilityException>(() => CompatibilityAnalyzer.Compare(manifest, changed));
        Assert.Equal("BASELINE_VERSION_MISMATCH", error.Code);
    }

    [Fact]
    public void FixtureManifestReaderPreservesEveryCanonicalField()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "example-cli.json")));
        var roundTrip = CanonicalManifestReader.Read(Normalizer.Serialize(manifest));

        Assert.Equal(Normalizer.Serialize(manifest), Normalizer.Serialize(roundTrip));
        Assert.Empty(CompatibilityAnalyzer.Compare(roundTrip, manifest).Findings);
    }

    private static CanonicalManifest Normalize(string commands)
    {
        return NormalizeWithInfo(commands, "tool");
    }

    private static CanonicalManifest NormalizeWithInfo(string commands, string binary)
    {
        var root = JsonNode.Parse(commands)!.AsObject();
        root["opencliVersion"] = Normalizer.OpenCliVersion;
        root["info"] = new JsonObject { ["title"] = "Tool", ["binary"] = binary, ["version"] = "1" };
        return Normalizer.Normalize("opencli", root.ToJsonString());
    }

    private static CanonicalManifest NormalizeWithGlobal(string commands, string environmentProperty, string fileProperty, string configPath)
    {
        var root = JsonNode.Parse(commands)!.AsObject();
        root["opencliVersion"] = Normalizer.OpenCliVersion;
        root["info"] = new JsonObject { ["title"] = "Tool", ["binary"] = "tool", ["version"] = "1" };
        root["global"] = new JsonObject
        {
            ["config"] = new JsonObject { ["json"] = configPath },
            ["flags"] = new JsonArray(new JsonObject
            {
                ["name"] = "global",
                ["type"] = "string",
                ["alternativeSources"] = new JsonArray(new JsonObject { ["type"] = "$ENV", ["property"] = environmentProperty }, new JsonObject { ["type"] = "$FILE", ["property"] = fileProperty })
            })
        };
        return Normalizer.Normalize("opencli", root.ToJsonString());
    }

    private static string Fixture(params string[] parts) => Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", .. parts]));
}
