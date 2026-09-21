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
    public void DomainNarrowingIsBreakingButWideningIsNot()
    {
        var oldManifest = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"number","choices":[{"value":1},{"value":2}]}]}}}""");
        var narrowed = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"integer","choices":[{"value":1}]}]}}}""");
        var widened = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"number","choices":[{"value":1},{"value":2},{"value":3}]}]}}}""");

        Assert.Contains(CompatibilityAnalyzer.Compare(oldManifest, narrowed).Findings, finding => finding.Code == "KMCLI107");
        Assert.DoesNotContain(CompatibilityAnalyzer.Compare(oldManifest, widened).Findings, finding => finding.Code == "KMCLI107");
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
        var root = JsonNode.Parse(commands)!.AsObject();
        root["opencliVersion"] = Normalizer.OpenCliVersion;
        root["info"] = new JsonObject { ["title"] = "Tool", ["binary"] = "tool", ["version"] = "1" };
        return Normalizer.Normalize("opencli", root.ToJsonString());
    }

    private static string Fixture(params string[] parts) => Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", .. parts]));
}
