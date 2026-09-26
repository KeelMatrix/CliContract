using System.Text.Json.Nodes;
using KeelMatrix.CliContract.Core;
using Xunit;

namespace KeelMatrix.CliContract.Tests;

public sealed class SourceProducibilityGuardTests
{
    private static readonly string[] EdgeValues =
    [
        "",
        " ",
        "---",
        " mixed\t whitespace ",
        "é",
        "!@#",
        new string('x', 4_096)
    ];

    private static readonly SourceFieldCase[] SourceFields = SourceContractFields.ExpectedFields
        .Select(field => new SourceFieldCase(field.Name, field.SetSource, field.Accepts))
        .ToArray();

    [Fact]
    public void ProjectionTraceMatchesIndependentlyAuthoredSourceFieldContract()
    {
        var trace = new SourceProjectionTrace();
        var projected = SourceContractProjection.Create(NormalizeAndRead(BaseDocument()), trace);
        trace.ObserveOutput(projected);
        var expected = SourceContractExpectedFields.Fields;
        var covered = trace.Fields;

        AssertProjectionCoverage(expected, covered);

        var commandKeys = projected["commands"]!.AsObject().Select(property => property.Key).ToArray();
        Assert.Contains("tool", commandKeys);
        Assert.Contains("tool run", commandKeys);
        Assert.Contains("tool run deploy", commandKeys);
        Console.WriteLine($"covered_fields={covered.Count}");
        Console.WriteLine($"expected_fields={expected.Count}");
        Console.WriteLine($"covered_field_names={string.Join(',', covered.OrderBy(value => value, StringComparer.Ordinal))}");
    }

    [Fact]
    public void DescriptorCatalogCoversEveryProjectionFieldAcrossAllScopes()
    {
        var trace = new SourceProjectionTrace();
        var projected = SourceContractProjection.Create(NormalizeAndRead(BaseDocument()), trace);
        trace.ObserveOutput(projected);

        AssertProjectionCoverage(
            trace.CatalogFields,
            SourceFields.Select(field => field.Name).ToArray());
    }

    [Fact]
    public void CatalogOnlyProjectionFieldFailsCoverageGuardWithItsPath()
    {
        var trace = new SourceProjectionTrace();
        var projected = SourceContractProjection.Create(NormalizeAndRead(BaseDocument()), trace);
        trace.ObserveOutput(projected);
        var catalog = SourceFields.Select(field => field.Name).Append("catalog.unwritten").ToArray();

        var failure = Record.Exception(() => AssertProjectionCoverage(trace.CatalogFields, catalog));

        Assert.NotNull(failure);
        Assert.Contains("catalog.unwritten", failure!.Message);
        Console.WriteLine("CATALOG_COVERAGE_GUARD_EXIT=1 unwritten=catalog.unwritten");
    }

    [Fact]
    public void AllowListedUnregisteredProjectionFieldFailsCoverageGuardWithItsPath()
    {
        var trace = new SourceProjectionTrace("unregistered");
        var projected = SourceContractProjection.Create(NormalizeAndRead(BaseDocument()), trace);
        projected["unregistered"] = "scratch-probe";
        trace.ObserveOutput(projected);

        var failure = Record.Exception(() => AssertProjectionCoverage(
            SourceContractExpectedFields.Fields,
            trace.Fields));

        Assert.NotNull(failure);
        Assert.Contains("root.unregistered", failure!.Message);
        Console.WriteLine("ALLOWLISTED_COVERAGE_GUARD_EXIT=1 uncovered=root.unregistered");
    }

    [Fact]
    public void Alpha14SourceFieldEdgeMatrixRemainsReflexiveAndBounded()
    {
        foreach (var field in SourceFields)
        {
            foreach (var value in EdgeValues)
            {
                var document = field.Create(value);
                var error = Record.Exception(() => NormalizeAndRead(document));
                if (field.Accepts(value))
                {
                    Assert.Null(error);
                }
                else
                {
                    Assert.IsType<NormalizationException>(error);
                }
            }
        }
    }

    private static CanonicalManifest NormalizeAndRead(JsonObject document)
    {
        var manifest = Normalizer.Normalize("opencli", document.ToJsonString());
        var serialized = Normalizer.Serialize(manifest);
        var roundTrip = CanonicalManifestReader.Read(serialized);
        Assert.Equal(serialized, Normalizer.Serialize(roundTrip));
        Assert.Empty(CompatibilityAnalyzer.Compare(roundTrip, roundTrip).Findings);
        return roundTrip;
    }

    private static void AssertProjectionCoverage(IReadOnlyCollection<string> expected, IReadOnlyCollection<string> covered)
    {
        var duplicateExpected = expected
            .GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var duplicateCovered = covered
            .GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var missing = expected.Except(covered, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var unexpected = covered.Except(expected, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();

        Assert.True(
            duplicateExpected.Length == 0 && duplicateCovered.Length == 0 && missing.Length == 0 && unexpected.Length == 0,
            $"Source projection coverage mismatch. missing={string.Join(',', missing)} unexpected={string.Join(',', unexpected)} duplicate_expected={string.Join(',', duplicateExpected)} duplicate_covered={string.Join(',', duplicateCovered)}");
    }

    private static JsonObject BaseDocument() => JsonNode.Parse("""
        {
          "opencliVersion": "1.0.0-alpha.14",
          "info": {
            "title": "Tool",
            "summary": "Summary",
            "description": "Description",
            "binary": "tool",
            "version": "1",
            "license": { "name": "MIT", "spdxId": "MIT", "url": "https://example.invalid/license" },
            "contact": { "name": "Support", "email": "support@example.invalid", "url": "https://example.invalid/contact" }
          },
          "install": [{ "name": "download", "command": "tool install", "url": "https://example.invalid/install", "description": "Install" }],
          "global": {
            "config": { "json": "config.json", "toml": "config.toml", "yaml": "config.yaml" },
            "exitCodes": [{ "code": 0, "status": "OK", "summary": "ok", "description": "success" }],
            "flags": [{
              "name": "global-option",
              "aliases": ["global-alias"],
              "type": "string",
              "required": true,
              "variadic": false,
              "summary": "Option summary",
              "description": "Option description",
              "hint": "value",
              "hidden": true,
              "default": "default",
              "choices": [{ "value": "choice", "description": "Choice" }],
              "alternativeSources": [{ "type": "$ENV", "property": "GLOBAL_OPTION" }]
            }, { "name": "global-var-option", "type": "string", "variadic": true, "minItems": 1, "maxItems": 2 }]
          },
          "commands": {
            "tool": {
              "aliases": ["t"],
              "summary": "Root summary",
              "description": "Root description",
              "kind": "action",
              "hidden": true,
              "exitCodes": [{ "code": 0, "status": "OK", "summary": "ok", "description": "success" }],
              "examples": [{ "title": "Example", "content": "tool" }],
              "args": [{ "name": "root-argument", "type": "string", "summary": "Argument summary", "description": "Argument description", "required": true, "variadic": true, "minItems": 1, "maxItems": 2, "passthrough": true, "choices": [{ "value": "root-choice", "description": "Root choice" }] }],
              "flags": [{ "name": "root-option", "aliases": ["root-alias"], "type": "string", "required": true, "variadic": false, "summary": "Root option summary", "description": "Root option description", "hint": "value", "hidden": true, "default": "root-default", "choices": [{ "value": "root-flag-choice", "description": "Root flag choice" }], "alternativeSources": [{ "type": "$ENV", "property": "ROOT_OPTION" }] }, { "name": "root-var-option", "type": "string", "variadic": true, "minItems": 1, "maxItems": 2 }]
            },
            "tool run": {
              "aliases": ["r"],
              "summary": "Run summary",
              "description": "Run description",
              "kind": "action",
              "hidden": true,
              "exitCodes": [{ "code": 0, "status": "OK", "summary": "run ok", "description": "run success" }],
              "examples": [{ "title": "Run example", "content": "tool run" }],
              "args": [{ "name": "run-argument", "type": "string", "summary": "Run argument summary", "description": "Run argument description", "required": true, "variadic": true, "minItems": 1, "maxItems": 3, "passthrough": true, "choices": [{ "value": "one", "description": "One" }] }],
              "flags": [{ "name": "run-option", "aliases": ["run-alias"], "type": "string", "required": true, "variadic": false, "summary": "Run option summary", "description": "Run option description", "hint": "value", "hidden": true, "default": "run-default", "choices": [{ "value": "run-choice", "description": "Run choice" }], "alternativeSources": [{ "type": "$ENV", "property": "RUN_OPTION" }] }, { "name": "run-var-option", "type": "string", "variadic": true, "minItems": 1, "maxItems": 3 }]
            },
            "tool run deploy": { "kind": "group", "summary": "Deploy summary" }
          }
        }
        """)!.AsObject();

    private sealed record SourceFieldCase(string Name, Action<JsonObject, string> Set, Func<string, bool> Accepts)
    {
        public JsonObject Create(string value)
        {
            var document = (JsonNode.Parse(BaseDocument().ToJsonString())!).AsObject();
            Set(document, value);
            return document;
        }
    }
}
