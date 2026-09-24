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
        var oldManifest = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"string","aliases":["-v"],"required":false,"default":"one","description":"old"},{"name":"items","type":"string","variadic":true,"maxItems":3}]}}}""");
        var newManifest = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"string","aliases":["-x"],"required":true,"default":"two","description":"new"},{"name":"items","type":"string","variadic":true,"maxItems":1}]}}}""");

        var findings = CompatibilityAnalyzer.Compare(oldManifest, newManifest).Findings;

        Assert.Contains(findings, finding => finding.Code == "KMCLI105" && finding.Category == "breaking");
        Assert.Contains(findings, finding => finding.Code == "KMCLI106" && finding.Category == "breaking");
        Assert.Contains(findings, finding => finding.Code == "KMCLI104" && finding.Category == "breaking");
        Assert.Contains(findings, finding => finding.Code == "KMCLI004" && finding.Category == "info");
        Assert.Contains(findings, finding => finding.Code == "KMCLI201" && finding.Category == "warning");
        Assert.Contains(findings, finding => finding.Code == "KMCLI005" && finding.Category == "info");
    }

    [Fact]
    public void RepresentedInfoInstallAndChoiceHelpChangesAreInformationalInBothDirections()
    {
        var baseline = ManifestWithRepresentedInfo("old", "old-choice");
        var current = ManifestWithRepresentedInfo("new", "new-choice");

        var forward = CompatibilityAnalyzer.Compare(baseline, current).Findings;
        var reverse = CompatibilityAnalyzer.Compare(current, baseline).Findings;

        var expectedPaths = new[]
        {
            "info / title",
            "info / summary",
            "info / description",
            "info / version",
            "info / license / name",
            "info / license / spdxId",
            "info / license / url",
            "info / contact / name",
            "info / contact / email",
            "info / contact / url",
            "info / install / 0 / name",
            "info / install / 0 / command",
            "info / install / 0 / url",
            "info / install / 0 / description",
            "root / --value / choices"
        };

        foreach (var findings in new[] { forward, reverse })
        {
            Assert.Equal(expectedPaths.Length, findings.Count);
            Assert.All(expectedPaths, path => Assert.Contains(findings, finding => finding.Code == "KMCLI005" && finding.Category == "info" && finding.Path == path));
        }
    }

    [Fact]
    public void AddedAndRemovedRepresentedHelpValuesProduceInformationalFindings()
    {
        var withoutInfo = ManifestWithRepresentedInfo(null, null, includeInstall: false, includeChoiceDescription: false);
        var withInfo = ManifestWithRepresentedInfo("added", "added-choice", includeInstall: true, includeChoiceDescription: true);

        var added = CompatibilityAnalyzer.Compare(withoutInfo, withInfo).Findings;
        var removed = CompatibilityAnalyzer.Compare(withInfo, withoutInfo).Findings;

        var expectedPaths = new[]
        {
            "info / title",
            "info / summary",
            "info / description",
            "info / version",
            "info / license / name",
            "info / license / spdxId",
            "info / license / url",
            "info / contact / name",
            "info / contact / email",
            "info / contact / url",
            "info / install / 0 / name",
            "info / install / 0 / command",
            "info / install / 0 / url",
            "info / install / 0 / description",
            "root / --value / choices"
        };

        foreach (var findings in new[] { added, removed })
        {
            Assert.Equal(expectedPaths.Length, findings.Count);
            Assert.All(expectedPaths, path => Assert.Contains(findings, finding => finding.Code == "KMCLI005" && finding.Category == "info" && finding.Path == path));
        }
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
    public void CommandRenameWithoutAliasIsBreaking()
    {
        var baseline = Normalize("""{"commands":{"tool deploy":{}}}""");
        var current = Normalize("""{"commands":{"tool release":{}}}""");

        var findings = CompatibilityAnalyzer.Compare(baseline, current).Findings;

        Assert.Contains(findings, finding => finding.Code == "KMCLI103" && finding.Category == "breaking");
    }

    [Fact]
    public void CommandRenameWithAliasPreservesTheAcceptedInvocation()
    {
        var baseline = Normalize("""{"commands":{"tool deploy run":{}}}""");
        var current = Normalize("""{"commands":{"tool release":{"aliases":["deploy"]},"tool release run":{}}}""");

        var findings = CompatibilityAnalyzer.Compare(baseline, current).Findings;

        Assert.DoesNotContain(findings, finding => finding.Category == "breaking");
        Assert.DoesNotContain(findings, finding => finding.Code == "KMCLI103");
    }

    [Fact]
    public void AliasRemovalIsBreakingOnlyWhenItRemovesAnAcceptedInvocation()
    {
        var baseline = Normalize("""{"commands":{"tool deploy":{"aliases":["d"]}}}""");
        var retainedThroughPrimary = Normalize("""{"commands":{"tool deploy":{}}}""");
        var duplicatePrimary = Normalize("""{"commands":{"tool deploy":{"aliases":["deploy"]}}}""");

        var retainedFindings = CompatibilityAnalyzer.Compare(baseline, retainedThroughPrimary).Findings;
        var duplicateFindings = CompatibilityAnalyzer.Compare(duplicatePrimary, retainedThroughPrimary).Findings;

        Assert.Contains(retainedFindings, finding => finding.Code == "KMCLI104" && finding.Category == "breaking");
        Assert.DoesNotContain(duplicateFindings, finding => finding.Code == "KMCLI104" && finding.Category == "breaking");
    }

    [Theory]
    [InlineData("string", "number", true)]
    [InlineData("string", "integer", true)]
    [InlineData("string", "boolean", true)]
    [InlineData("number", "string", false)]
    [InlineData("number", "integer", true)]
    [InlineData("number", "boolean", true)]
    [InlineData("integer", "string", false)]
    [InlineData("integer", "number", false)]
    [InlineData("integer", "boolean", true)]
    [InlineData("boolean", "string", false)]
    [InlineData("boolean", "number", true)]
    [InlineData("boolean", "integer", true)]
    public void SupportedTypeTransitionsUseAcceptedLexicalDomain(string oldType, string newType, bool breaking)
    {
        var baseline = Normalize($"{{\"commands\":{{\"tool\":{{\"flags\":[{{\"name\":\"value\",\"type\":\"{oldType}\"}}]}}}}}}");
        var current = Normalize($"{{\"commands\":{{\"tool\":{{\"flags\":[{{\"name\":\"value\",\"type\":\"{newType}\"}}]}}}}}}");

        var findings = CompatibilityAnalyzer.Compare(baseline, current).Findings;

        Assert.Equal(breaking, findings.Any(finding => finding.Code == "KMCLI107" && finding.Category == "breaking"));
    }

    [Theory]
    [InlineData("string", "\"1\"", "number", false)]
    [InlineData("string", "\"1\"", "integer", false)]
    [InlineData("string", "\"true\"", "boolean", false)]
    [InlineData("number", "1", "string", false)]
    [InlineData("number", "1", "integer", false)]
    [InlineData("number", "1", "boolean", true)]
    [InlineData("integer", "1", "string", false)]
    [InlineData("integer", "1", "number", false)]
    [InlineData("integer", "1", "boolean", true)]
    [InlineData("boolean", "true", "string", false)]
    [InlineData("boolean", "true", "number", true)]
    [InlineData("boolean", "true", "integer", true)]
    public void ConstrainedChoiceDomainsUseTheSameLexicalTypeRelation(string oldType, string oldChoice, string newType, bool breaking)
    {
        var baseline = Normalize($"{{\"commands\":{{\"tool\":{{\"flags\":[{{\"name\":\"value\",\"type\":\"{oldType}\",\"choices\":[{{\"value\":{oldChoice}}}]}}]}}}}}}");
        var current = Normalize($"{{\"commands\":{{\"tool\":{{\"flags\":[{{\"name\":\"value\",\"type\":\"{newType}\"}}]}}}}}}");

        var findings = CompatibilityAnalyzer.Compare(baseline, current).Findings;

        Assert.Equal(breaking, findings.Any(finding => finding.Code == "KMCLI107" && finding.Category == "breaking"));
    }

    [Fact]
    public void ConstrainingAnPreviouslyUnboundedDomainIsBreakingAndWideningAChoiceIsNot()
    {
        var unbounded = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"string"}]}}}""");
        var constrained = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"string","choices":[{"value":"one"}]}]}}}""");
        var oldChoice = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"string","choices":[{"value":"one"}]}]}}}""");

        Assert.Contains(CompatibilityAnalyzer.Compare(unbounded, constrained).Findings, finding => finding.Code == "KMCLI107" && finding.Category == "breaking");
        Assert.DoesNotContain(CompatibilityAnalyzer.Compare(oldChoice, unbounded).Findings, finding => finding.Code == "KMCLI107");
    }

    [Fact]
    public void ExitCodeChangesArePreservedAndWarn()
    {
        var baseline = NormalizeWithExitCodes("""[{"code":0,"status":"OK","summary":"ok"}]""", """[{"code":2,"status":"BAD_USER_INPUT_ERROR","summary":"bad"}]""");
        var current = NormalizeWithExitCodes("""[{"code":0,"status":"OK","summary":"ok"},{"code":1,"status":"INTERNAL_CLI_ERROR","summary":"failed"}]""", """[{"code":2,"status":"BAD_USER_INPUT_ERROR","summary":"changed"}]""");

        Assert.Single(baseline.GlobalExitCodes);
        Assert.Single(baseline.Root.ExitCodes);
        Assert.Equal("changed", current.Root.ExitCodes.Single().Summary);
        var findings = CompatibilityAnalyzer.Compare(baseline, current).Findings;
        Assert.Equal(2, findings.Count(finding => finding.Code == "KMCLI205" && finding.Category == "warning" && finding.Path == "root"));
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

    [Fact]
    public void ExactNumericDomainsRemainReflexiveOutsideDecimalRange()
    {
        var huge = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"number","choices":[{"value":1e1000},{"value":1e-1000},{"value":-0.0}]}]}}}""");
        var integral = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"integer","choices":[{"value":1e1000}]}]}}}""");
        var numericIntegral = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"number","choices":[{"value":1e1000}]}]}}}""");

        Assert.Empty(CompatibilityAnalyzer.Compare(huge, huge).Findings);
        Assert.DoesNotContain(CompatibilityAnalyzer.Compare(integral, numericIntegral).Findings, finding => finding.Code == "KMCLI107");

        var fraction = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"number","choices":[{"value":1e-1000}]}]}}}""");
        var integer = Normalize("""{"commands":{"tool":{"flags":[{"name":"value","type":"integer"}]}}}""");
        Assert.Contains(CompatibilityAnalyzer.Compare(fraction, integer).Findings, finding => finding.Code == "KMCLI107" && finding.Category == "breaking");
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

    private static CanonicalManifest ManifestWithRepresentedInfo(string? suffix, string? choiceDescription, bool includeInstall = true, bool includeChoiceDescription = true)
    {
        var info = new CanonicalInfo
        {
            Title = suffix is null ? null : "Title " + suffix,
            Summary = suffix is null ? null : "Summary " + suffix,
            Description = suffix is null ? null : "Description " + suffix,
            Binary = "tool",
            Version = suffix is null ? null : "1." + suffix,
            License = suffix is null ? null : new CanonicalLicense { Name = "MIT " + suffix, SpdxId = "MIT-" + suffix, Url = "https://license.invalid/" + suffix },
            Contact = suffix is null ? null : new CanonicalContact { Name = "Contact " + suffix, Email = suffix + "@example.invalid", Url = "https://contact.invalid/" + suffix },
            Install = includeInstall && suffix is not null
                ? [new CanonicalInstall { Name = "download-" + suffix, Command = "tool install " + suffix, Url = "https://install.invalid/" + suffix, Description = "Install " + suffix }]
                : []
        };

        return new CanonicalManifest
        {
            Adapter = "opencli",
            SourceVersion = Normalizer.OpenCliVersion,
            Info = info,
            Root = new CanonicalCommand
            {
                Path = "root",
                Options =
                [
                    new CanonicalOption
                    {
                        Name = "--value",
                        Type = "string",
                        Choices =
                        [
                            new CanonicalChoice { Value = JsonValue.Create("one")!, Description = includeChoiceDescription ? choiceDescription : null }
                        ]
                    }
                ]
            }
        };
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

    private static CanonicalManifest NormalizeWithExitCodes(string globalExitCodes, string commandExitCodes)
    {
        var root = JsonNode.Parse("""{"commands":{"tool":{"exitCodes":[]}}}""")!.AsObject();
        root["opencliVersion"] = Normalizer.OpenCliVersion;
        root["info"] = new JsonObject { ["title"] = "Tool", ["binary"] = "tool", ["version"] = "1" };
        root["global"] = new JsonObject { ["exitCodes"] = JsonNode.Parse(globalExitCodes) };
        root["commands"]!["tool"]!["exitCodes"] = JsonNode.Parse(commandExitCodes);
        return Normalizer.Normalize("opencli", root.ToJsonString());
    }

    private static string Fixture(params string[] parts) => Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", .. parts]));
}
