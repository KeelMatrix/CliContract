using System.Text.Json.Nodes;
using KeelMatrix.CliContract.Core;
using Xunit;

namespace KeelMatrix.CliContract.Tests;

public sealed class NormalizationTests
{
    [Fact]
    public void OpenCliCanonicalizesFixture()
    {
        var input = File.ReadAllText(Fixture("opencli", "example-cli.json"));
        var manifest = Normalizer.Normalize("opencli", input);
        var deploy = manifest.Root.Subcommands.Single(c => c.Path == "root / deploy");

        Assert.Equal("1.0.0-alpha.14", manifest.SourceVersion);
        Assert.Equal("--region", deploy.Options.Single().Name);
        Assert.Equal(["-r"], deploy.Options.Single().Aliases);
        Assert.Equal(["eu", "us"], deploy.Options.Single().AllowedValues.Select(value => value.GetValue<string>()));
        Assert.Equal("us", deploy.Options.Single().DefaultValue!.GetValue<string>());
        Assert.Contains("🚀", deploy.Summary);
        Assert.Contains(manifest.Root.Subcommands, command => command.Path == "root / empty");
    }

    [Fact]
    public void PinnedAlpha14ConformanceCorpusMatchesItsOracle()
    {
        var corpus = JsonNode.Parse(File.ReadAllText(Fixture("opencli", "alpha14-conformance-corpus.json")))!.AsArray();

        foreach (var item in corpus)
        {
            var caseId = item!["id"]!.GetValue<string>();
            var document = item["document"]!.ToJsonString();
            if (item["valid"]!.GetValue<bool>())
            {
                _ = Normalizer.Normalize("opencli", document);
                continue;
            }

            var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", document));
            Assert.Equal(item["expectedCode"]!.GetValue<string>(), error.Code);
            Assert.False(string.IsNullOrWhiteSpace(caseId));
        }
    }

    [Fact]
    public void PinnedAlpha14JsonAndYamlMinimalDocumentsAreEquivalent()
    {
        var json = Normalizer.Normalize("opencli", """
            {"opencliVersion":"1.0.0-alpha.14","info":{"title":"Tool","binary":"tool","version":"1"},"commands":{"tool":{"flags":[{"name":"enabled","type":"boolean","default":true}]}}}
            """);
        var yaml = Normalizer.Normalize("opencli", """
            opencliVersion: 1.0.0-alpha.14
            info:
              title: Tool
              binary: tool
              version: '1'
            commands:
              tool:
                flags:
                  - name: enabled
                    type: boolean
                    default: true
            """);

        Assert.Equal(Normalizer.Serialize(json), Normalizer.Serialize(yaml));
    }

    [Fact]
    public void OpenCliSourceOrderAndLineEndingsDoNotChangeBytes()
    {
        var first = File.ReadAllText(Fixture("opencli", "example-cli.json"));
        var second = File.ReadAllText(Fixture("opencli", "example-cli-reordered.json"));
        Assert.Equal(Normalizer.Serialize(Normalizer.Normalize("opencli", first)), Normalizer.Serialize(Normalizer.Normalize("opencli", second.Replace("\n", "\r\n"))));

        var yaml = File.ReadAllText(Fixture("opencli", "example-cli.yaml"));
        Assert.Equal(Normalizer.Serialize(Normalizer.Normalize("opencli", first)), Normalizer.Serialize(Normalizer.Normalize("opencli", yaml)));
    }

    [Theory]
    [InlineData("invalid-license-missing-name.json", "OPENCLI_INFO")]
    [InlineData("invalid-contact-anyof.json", "OPENCLI_INFO")]
    [InlineData("invalid-install-anyof.json", "OPENCLI_INSTALL")]
    [InlineData("invalid-example-content.json", "OPENCLI_COMMAND")]
    [InlineData("invalid-global-config-empty.json", "OPENCLI_GLOBAL")]
    [InlineData("invalid-exit-code-required.json", "OPENCLI_EXIT_CODE")]
    [InlineData("invalid-exit-code-status.json", "OPENCLI_EXIT_CODE")]
    public void OpenCliSchemaRequiredShapesFailClosed(string fixture, string expectedCode)
    {
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", fixture))));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void OpenCliAcceptsSchemaValidArgumentPassthrough()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "valid-argument-passthrough.json")));

        Assert.Equal("rest", manifest.Root.Subcommands.Single().Arguments.Single().Name);
        Assert.True(manifest.Root.Subcommands.Single().Arguments.Single().Passthrough);
        var roundTrip = CanonicalManifestReader.Read(Normalizer.Serialize(manifest));
        Assert.True(roundTrip.Root.Subcommands.Single().Arguments.Single().Passthrough);
    }

    [Fact]
    public void ArgumentPassthroughDefaultsToFalseAndExplicitFalseIsStable()
    {
        var omitted = Normalizer.Normalize("opencli", OpenCliDocument("{\"commands\":{\"tool run <rest>\":{\"args\":[{\"name\":\"rest\"}]}}}"));
        var explicitFalse = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "valid-argument-passthrough-false.json")));

        Assert.False(omitted.Root.Subcommands.Single().Arguments.Single().Passthrough);
        Assert.Equal(Normalizer.Serialize(omitted), Normalizer.Serialize(explicitFalse));
    }

    [Fact]
    public void TaggedYamlNumericValuesMatchJsonWithoutFloatingPointConversion()
    {
        var json = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "numeric-defaults-yaml.json")));
        var yaml = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "numeric-defaults-yaml.yaml")));

        Assert.Equal(Normalizer.Serialize(json), Normalizer.Serialize(yaml));
        Assert.Empty(CompatibilityAnalyzer.Compare(json, yaml).Findings);
        Assert.Equal("16", yaml.Root.Options.Single(option => option.Name == "--hex").DefaultValue!.ToJsonString());
        Assert.Equal("0.12345678901234567890123456789", yaml.Root.Options.Single(option => option.Name == "--fraction").DefaultValue!.ToJsonString());
    }

    [Theory]
    [InlineData("!!float 5.e2", "500")]
    [InlineData("5.e2", "500")]
    [InlineData("!!float +5.e+2", "500")]
    [InlineData("+5.e+2", "500")]
    [InlineData("!!float -5.e+2", "-500")]
    [InlineData("-5.e-2", "-0.05")]
    public void TrailingDotYamlFloatsMatchEquivalentJsonNumbers(string yamlScalar, string jsonScalar)
    {
        var json = Normalizer.Normalize("opencli", OpenCliDocument("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"number\",\"default\":" + jsonScalar + "}]}}}"));
        var yaml = Normalizer.Normalize("opencli", NumericYamlDocument(yamlScalar));

        Assert.Equal(Normalizer.Serialize(json), Normalizer.Serialize(yaml));
        Assert.Empty(CompatibilityAnalyzer.Compare(json, yaml).Findings);
    }

    [Fact]
    public void TrailingDotYamlFloatsKeepDistinctRepresentedValues()
    {
        var fiveHundred = Normalizer.Normalize("opencli", NumericYamlDocument("5.e2"));
        var fiveThousand = Normalizer.Normalize("opencli", NumericYamlDocument("5.e3"));

        Assert.NotEqual(Normalizer.Serialize(fiveHundred), Normalizer.Serialize(fiveThousand));
        Assert.Contains(CompatibilityAnalyzer.Compare(fiveHundred, fiveThousand).Findings, finding => finding.Code == "KMCLI201");
    }

    [Theory]
    [InlineData(".inf")]
    [InlineData("-.Inf")]
    [InlineData(".nan")]
    public void ExplicitNonFiniteYamlFloatsRemainOutsideTheJsonNumberBoundary(string yamlScalar)
    {
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", NumericYamlDocument($"!!float {yamlScalar}")));
        var implicitError = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", NumericYamlDocument(yamlScalar)));

        Assert.Equal("OPENCLI_NUMBER", error.Code);
        Assert.Equal("OPENCLI_DEFAULT", implicitError.Code);
    }

    [Fact]
    public void ExplicitYamlStringTagRemainsDistinctFromNumericValue()
    {
        var numeric = OpenCliDocument("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"number\",\"default\":16}]}}}");
        var yaml = """
        opencliVersion: 1.0.0-alpha.14
        info: {title: Tool, binary: tool, version: '1'}
        commands:
          tool:
            flags:
              - name: value
                type: number
                default: !!str 0x10
        """;

        var numericManifest = Normalizer.Normalize("opencli", numeric);
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", yaml));

        Assert.Equal("16", numericManifest.Root.Options.Single().DefaultValue!.ToJsonString());
        Assert.Equal("OPENCLI_DEFAULT", error.Code);
    }

    [Fact]
    public void GlobalConfigKeyOrderDoesNotChangeCanonicalBytesOrCompatibility()
    {
        var jsonFirst = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "global-config-order-a.json")));
        var jsonSecond = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "global-config-order-b.json")));
        var yamlFirst = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "global-config-order-a.yaml")));
        var yamlSecond = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "global-config-order-b.yaml")));

        var canonical = Normalizer.Serialize(jsonFirst);
        Assert.Equal(canonical, Normalizer.Serialize(jsonSecond));
        Assert.Equal(canonical, Normalizer.Serialize(yamlFirst));
        Assert.Equal(canonical, Normalizer.Serialize(yamlSecond));
        Assert.Empty(CompatibilityAnalyzer.Compare(jsonFirst, jsonSecond).Findings);
        Assert.Empty(CompatibilityAnalyzer.Compare(yamlFirst, yamlSecond).Findings);
    }

    [Fact]
    public void NumericSpellingsCanonicalizeAcrossSmallLargeAndNegativeZeroValues()
    {
        var plain = Normalizer.Serialize(Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "numeric-defaults-plain.json"))));
        var exponent = Normalizer.Serialize(Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "numeric-defaults-exponent.json"))));

        Assert.Equal(plain, exponent);
        Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "numeric-defaults-extreme.json")));
    }

    [Fact]
    public void UnsupportedOpenCliVersionFailsClosed()
    {
        var input = File.ReadAllText(Fixture("opencli", "example-cli.json"));
        var changed = input.Replace(Normalizer.OpenCliVersion, "1.0.0-alpha.13", StringComparison.Ordinal);
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", changed));
        Assert.Equal("UNSUPPORTED_OPENCLI_VERSION", error.Code);
        Assert.DoesNotContain("pr" + "obe", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LargeInputIsRejectedWithoutEchoingContent()
    {
        var secret = new string('x', 101);
        var input = "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"t\",\"binary\":\"b\",\"version\":\"1\"},\"commands\":{\"b\":{\"description\":\"" + secret + "\"}}}";
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", input, new NormalizationLimits(MaxStringLength: 100)));
        Assert.Equal("STRING_TOO_LARGE", error.Code);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InputByteLimitUsesUtf8BytesAndAcceptsExactBoundary()
    {
        var input = "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"t\",\"binary\":\"tool\",\"version\":\"1\"},\"commands\":{\"tool\":{\"description\":\"" + new string('é', 1_000) + "\"}}}";
        var inputBytes = System.Text.Encoding.UTF8.GetByteCount(input);

        Normalizer.Normalize("opencli", input, new NormalizationLimits(MaxInputBytes: inputBytes));

        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", input, new NormalizationLimits(MaxInputBytes: inputBytes - 1)));
        Assert.Equal("INPUT_TOO_LARGE", error.Code);
        Assert.Contains("UTF-8 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonObjectCollectionLimitAcceptsExactCommandMapAndRejectsOneOver()
    {
        var exactCommands = new JsonObject();
        var overCommands = new JsonObject();
        for (var index = 0; index < 3; index++)
        {
            exactCommands[$"tool command{index}"] = new JsonObject();
        }

        for (var index = 0; index < 4; index++)
        {
            overCommands[$"tool command{index}"] = new JsonObject();
        }

        Normalizer.Normalize("opencli", OpenCliDocument(new JsonObject { ["commands"] = exactCommands }.ToJsonString()), new NormalizationLimits(MaxCollectionItems: 3));

        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", OpenCliDocument(new JsonObject { ["commands"] = overCommands }.ToJsonString()), new NormalizationLimits(MaxCollectionItems: 3)));
        Assert.Equal("COLLECTION_TOO_LARGE", error.Code);
    }

    [Theory]
    [InlineData("flags")]
    [InlineData("args")]
    [InlineData("choices")]
    [InlineData("aliases")]
    [InlineData("alternativeSources")]
    public void OpenCliCollectionLimitsAcceptExactBoundaryAndRejectOneOver(string collection)
    {
        var exact = ParameterCollectionDocument(collection, 3);
        var over = ParameterCollectionDocument(collection, 4);
        var limits = new NormalizationLimits(MaxCollectionItems: 3);

        Normalizer.Normalize("opencli", exact, limits);

        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", over, limits));
        Assert.Equal("COLLECTION_TOO_LARGE", error.Code);
    }

    [Fact]
    public void DuplicateJsonObjectKeysAreRejectedAtEveryLevel()
    {
        var cases = new[]
        {
            "{\"opencliVersion\":\"1.0.0-alpha.14\",\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"t\",\"binary\":\"b\",\"version\":\"1\"}}",
            "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"t\",\"binary\":\"b\",\"version\":\"1\"},\"commands\":{\"tool\":{\"description\":\"a\",\"description\":\"b\"}}}",
            "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"t\",\"binary\":\"b\",\"version\":\"1\"},\"commands\":{\"tool\":{\"flags\":[{\"name\":\"x\",\"name\":\"y\",\"type\":\"string\"}]}}}"
        };

        foreach (var input in cases)
        {
            var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", input));
            Assert.Equal("DUPLICATE_JSON_KEY", error.Code);
        }
    }

    [Fact]
    public void DuplicateYamlMappingKeysAreRejectedAtEveryLevelWithStableCode()
    {
        var cases = new[]
        {
            """
            opencliVersion: 1.0.0-alpha.14
            opencliVersion: 1.0.0-alpha.14
            info: {title: Tool, binary: tool, version: '1'}
            """,
            """
            opencliVersion: 1.0.0-alpha.14
            info: {title: Tool, binary: tool, version: '1'}
            commands:
              tool:
                description: first
                description: second
            """,
            """
            opencliVersion: 1.0.0-alpha.14
            info: {title: Tool, binary: tool, version: '1'}
            commands:
              tool:
                flags:
                  - name: first
                    name: second
                    type: string
            """
        };

        foreach (var input in cases)
        {
            var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", input));
            Assert.Equal("DUPLICATE_YAML_KEY", error.Code);
        }
    }

    [Fact]
    public void JsonDepthLimitAcceptsExactDepthAndRejectsOneOverWithStableCode()
    {
        const int maxDepth = 4;

        Normalizer.Normalize("opencli", OpenCliDocumentWithNestedObjectDepth(maxDepth - 1), new NormalizationLimits(MaxDepth: maxDepth));

        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize(
            "opencli",
            OpenCliDocumentWithNestedObjectDepth(maxDepth),
            new NormalizationLimits(MaxDepth: maxDepth)));

        Assert.Equal("DEPTH_LIMIT", error.Code);
    }

    [Fact]
    public void OpenCliRejectsFlagOnlyFieldsOnArgumentsAndRequiresFlagType()
    {
        var argumentDefault = OpenCliDocument("{\"commands\":{\"tool\":{\"args\":[{\"name\":\"value\",\"default\":\"x\"}]}}}");
        var argumentSource = OpenCliDocument("{\"commands\":{\"tool\":{\"args\":[{\"name\":\"value\",\"alternativeSources\":[{\"type\":\"$ENV\",\"property\":\"VALUE\"}]}]}}}");
        var missingFlagType = OpenCliDocument("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\"}]}}}");

        Assert.Equal("OPENCLI_ARGUMENT_FIELD", Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", argumentDefault)).Code);
        Assert.Equal("OPENCLI_ARGUMENT_FIELD", Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", argumentSource)).Code);
        Assert.Equal("OPENCLI_FLAG_TYPE", Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", missingFlagType)).Code);
    }

    [Fact]
    public void OpenCliArgumentTypeRemainsOptionalPerAlpha14Schema()
    {
        var input = OpenCliDocument("{\"commands\":{\"tool run <value>\":{\"args\":[{\"name\":\"value\"}]}}}");

        var argument = Normalizer.Normalize("opencli", input).Root.Subcommands.Single().Arguments.Single();

        Assert.Null(argument.Type);
    }

    [Fact]
    public void OpenCliPreservesScalarChoiceValuesAndRejectsMalformedChoices()
    {
        var input = OpenCliDocument("""
        {
          "commands": {
            "tool choose [flags]": {
              "flags": [{
                "name": "value",
                "type": "string",
                "choices": [
                  {"value": "text"},
                  {"value": 7},
                  {"value": 7.5},
                  {"value": true}
                ]
              }]
            }
          }
        }
        """);

        var option = Normalizer.Normalize("opencli", input).Root.Subcommands.Single().Options.Single();
        Assert.Equal(["string:text", "number:7", "number:7.5", "boolean:1"], option.AllowedValues.Select(value => value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => "string:" + value.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => "number:" + value.ToJsonString(),
            System.Text.Json.JsonValueKind.True => "boolean:1",
            _ => "unexpected"
        }));

        var malformed = input.Replace("{\"value\":true}", "{\"value\":{\"nested\":true}}", StringComparison.Ordinal);
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", malformed));
        Assert.Equal("OPENCLI_CHOICE", error.Code);
    }

    [Theory]
    [InlineData("{\"unexpected\":true}", "OPENCLI_UNKNOWN_FIELD")]
    [InlineData("{\"commands\":{\"tool\":{\"unexpected\":true}}}", "OPENCLI_UNKNOWN_FIELD")]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":[\"string\"]}]}}}", "OPENCLI_FLAG_TYPE")]
    [InlineData("{\"commands\":{\"tool\":{\"args\":[{\"name\":\"value\",\"minItems\":-1}]}}}", "OPENCLI_ARITY")]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"string\",\"minItems\":2,\"maxItems\":1}]}}}", "OPENCLI_ARITY")]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"string\",\"alternativeSources\":[]}]}}}", "OPENCLI_DEFAULT_SOURCES")]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"string\",\"$ref\":\"https://example.invalid/schema\"}]}}}", "OPENCLI_REMOTE_REFERENCE")]
    [InlineData("{\"include\":\"https://example.invalid/schema\"}", "OPENCLI_REMOTE_REFERENCE")]
    public void OpenCliSchemaViolationsFailClosed(string commands, string expectedCode)
    {
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", OpenCliDocument(commands)));
        Assert.Equal(expectedCode, error.Code);
    }

    [Theory]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"region\",\"type\":\"string\"},{\"name\":\"region\",\"type\":\"string\"}]}}}")]
    [InlineData("{\"commands\":{\"tool run <region> <region>\":{\"args\":[{\"name\":\"region\"},{\"name\":\"region\"}]}}}")]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"region\",\"type\":\"string\"},{\"name\":\"--region\",\"type\":\"string\"}]}}}")]
    [InlineData("{\"global\":{\"flags\":[{\"name\":\"region\",\"type\":\"string\"}]},\"commands\":{\"tool\":{\"flags\":[{\"name\":\"--region\",\"type\":\"string\"}]}}}")]
    public void DuplicateNormalizedParameterNamesFailClosed(string commands)
    {
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", OpenCliDocument(commands)));

        Assert.Equal("OPENCLI_DUPLICATE_PARAMETER", error.Code);
    }

    [Fact]
    public void InformationalMetadataUrlsArePreservedVerbatimAndNeverResolved()
    {
        const string contactUrl = "https://metadata.example.invalid/contact?source=OpenCLI%2F1.0.0-alpha.14";
        const string licenseUrl = "https://metadata.example.invalid/license";
        const string installUrl = "https://metadata.example.invalid/install?channel=stable";
        var document = JsonNode.Parse(OpenCliDocument("{\"commands\":{}}"))!.AsObject();
        document["info"] = new JsonObject
        {
            ["title"] = "Tool",
            ["binary"] = "tool",
            ["version"] = "1",
            ["contact"] = new JsonObject { ["url"] = contactUrl },
            ["license"] = new JsonObject { ["name"] = "MIT", ["url"] = licenseUrl }
        };
        document["install"] = new JsonArray(new JsonObject
        {
            ["name"] = "download",
            ["url"] = installUrl
        });

        var manifest = Normalizer.Normalize("opencli", document.ToJsonString());
        Assert.Equal(contactUrl, manifest.Info.Contact!.Url);
        Assert.Equal(licenseUrl, manifest.Info.License!.Url);
        Assert.Equal(installUrl, manifest.Info.Install.Single().Url);

        var serialized = Normalizer.Serialize(manifest);
        Assert.Contains($"\"Url\": \"{contactUrl}\"", serialized, StringComparison.Ordinal);
        Assert.Contains($"\"Url\": \"{licenseUrl}\"", serialized, StringComparison.Ordinal);
        Assert.Contains($"\"Url\": \"{installUrl}\"", serialized, StringComparison.Ordinal);
        Assert.Equal(serialized, Normalizer.Serialize(CanonicalManifestReader.Read(serialized)));
    }

    [Fact]
    public void EquivalentNumericDefaultsHaveByteIdenticalCanonicalOutput()
    {
        var integer = OpenCliDocument("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"retries\",\"type\":\"number\",\"default\":7}]}}}");
        var decimalValue = OpenCliDocument("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"retries\",\"type\":\"number\",\"default\":7.0}]}}}");

        Assert.Equal(
            Normalizer.Serialize(Normalizer.Normalize("opencli", integer)),
            Normalizer.Serialize(Normalizer.Normalize("opencli", decimalValue)));
    }

    [Fact]
    public void JsonAndYamlPreserveExactNumericDefaultsAcrossPrecisionAndMagnitude()
    {
        const string json = """
        {
          "commands": {
            "tool": {
              "flags": [
                {"name":"large-a","type":"number","default":16777216.0},
                {"name":"large-b","type":"number","default":16777217.0},
                {"name":"fraction","type":"number","default":0.123456789012345678901234567890},
                {"name":"small","type":"number","default":1e-300},
                {"name":"huge","type":"number","default":1e300}
              ]
            }
          }
        }
        """;
        const string yaml = """
        opencliVersion: 1.0.0-alpha.14
        info:
          title: Tool
          binary: tool
          version: '1'
        commands:
          tool:
            flags:
              - name: large-a
                type: number
                default: 16777216.0
              - name: large-b
                type: number
                default: 16777217.0
              - name: fraction
                type: number
                default: 0.123456789012345678901234567890
              - name: small
                type: number
                default: 1e-300
              - name: huge
                type: number
                default: 1e300
        """;

        var jsonManifest = Normalizer.Normalize("opencli", OpenCliDocument(json));
        var yamlManifest = Normalizer.Normalize("opencli", yaml);

        Assert.Equal(Normalizer.Serialize(jsonManifest), Normalizer.Serialize(yamlManifest));
        var defaults = yamlManifest.Root.Options.ToDictionary(option => option.Name, option => option.DefaultValue!.ToJsonString());
        Assert.Equal("16777216", defaults["--large-a"]);
        Assert.Equal("16777217", defaults["--large-b"]);
        Assert.NotEqual(defaults["--large-a"], defaults["--large-b"]);
        Assert.Equal("0.12345678901234567890123456789", defaults["--fraction"]);
        Assert.Equal("1e-300", defaults["--small"]);
        Assert.Equal("1e300", defaults["--huge"]);
    }

    [Fact]
    public void CommandKeysMustUseInfoBinaryAndKindIsPreserved()
    {
        var mismatch = OpenCliDocument("{\"commands\":{\"other run\":{}}}");
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", mismatch));
        Assert.Equal("OPENCLI_COMMAND_KEY", error.Code);

        var document = OpenCliDocument("{\"commands\":{\"tool run\":{\"kind\":\"group\"}}}");
        var command = Normalizer.Normalize("opencli", document).Root.Subcommands.Single();
        Assert.Equal("group", command.Kind);
    }

    [Fact]
    public void OpenCliPreservesGlobalFlagsSeparatelyFromRootLocalOptions()
    {
        var input = OpenCliDocument("""
        {
          "global": {"flags": [{"name": "debug", "type": "boolean"}, {"name": "timeout", "type": "integer"}]},
          "commands": {"tool run [flags]": {}}
        }
        """);

        var manifest = Normalizer.Normalize("opencli", input);
        Assert.Equal(["--debug", "--timeout"], manifest.GlobalOptions.Select(option => option.Name));
        Assert.Empty(manifest.Root.Options);

        var withoutTimeout = input.Replace(",{\"name\":\"timeout\",\"type\":\"integer\"}", String.Empty, StringComparison.Ordinal);
        var changed = Normalizer.Normalize("opencli", withoutTimeout);
        Assert.NotEqual(Normalizer.Serialize(manifest), Normalizer.Serialize(changed));
        Assert.DoesNotContain(changed.GlobalOptions, option => option.Name == "--timeout");
    }

    [Fact]
    public void OpenCliRootAliasesArePreservedAndAliasRemovalChangesCanonicalBytes()
    {
        var withAlias = OpenCliDocument("""
        {"commands":{"tool":{"aliases":["t"]}}}
        """);
        var withoutAlias = OpenCliDocument("""
        {"commands":{"tool":{}}}
        """);

        var aliasedManifest = Normalizer.Normalize("opencli", withAlias);
        var unaliasedManifest = Normalizer.Normalize("opencli", withoutAlias);

        Assert.Equal(["t"], aliasedManifest.Root.Aliases);
        Assert.Empty(unaliasedManifest.Root.Aliases);
        Assert.NotEqual(Normalizer.Serialize(aliasedManifest), Normalizer.Serialize(unaliasedManifest));
    }

    [Fact]
    public void OpenCliExplicitNullContainersAndNonScalarDefaultsFailAsBoundedAdapterErrors()
    {
        var cases = new[]
        {
            ("{\"commands\":null}", "OPENCLI_COMMANDS"),
            ("{\"global\":{\"flags\":null}}", "OPENCLI_COLLECTION"),
            ("{\"commands\":{\"tool\":{\"flags\":null}}}", "OPENCLI_COLLECTION"),
            ("{\"commands\":{\"tool\":{\"args\":null}}}", "OPENCLI_COLLECTION"),
            ("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"string\",\"default\":null}]}}}", "OPENCLI_DEFAULT"),
            ("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"string\",\"default\":{\"nested\":true}}]}}}", "OPENCLI_DEFAULT")
        };

        foreach (var (commands, expectedCode) in cases)
        {
            var exception = Record.Exception(() => Normalizer.Normalize("opencli", OpenCliDocument(commands)));
            var error = Assert.IsType<NormalizationException>(exception);
            Assert.Equal(expectedCode, error.Code);
        }
    }

    [Fact]
    public void OpenCliYamlAnchorsAndAliasesAreRejectedInFlowForms()
    {
        var cases = new[]
        {
            """
            opencliVersion: 1.0.0-alpha.14
            info: {title: Tool, binary: tool, version: '1'}
            commands: {tool: {flags: [&flag {name: value, type: string}]}}
            """,
            """
            opencliVersion: 1.0.0-alpha.14
            info: {title: Tool, binary: tool, version: '1'}
            commands: {tool: {flags: [&flag {name: value, type: string}, *flag]}}
            """,
            """
            opencliVersion: 1.0.0-alpha.14
            info: {title: Tool, binary: tool, version: '1'}
            commands: {tool: {flags: &level1 [*flag, *flag]}}
            flag: &flag {name: value, type: string}
            """,
            """
            opencliVersion: 1.0.0-alpha.14
            info: {title: Tool, binary: tool, version: '1'}
            commands: {tool: &command {flags: []}}
            """
        };

        foreach (var input in cases)
        {
            var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", input));
            Assert.Equal("YAML_ALIASES_UNSUPPORTED", error.Code);
            Assert.DoesNotContain("pr" + "obe", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void OpenCliArgumentDeclarationOrderIsCanonicalContract()
    {
        var first = OpenCliDocument("""
        {"commands":{"tool run <first> <second>":{"args":[{"name":"first"},{"name":"second"}]}}}
        """);
        var second = OpenCliDocument("""
        {"commands":{"tool run <first> <second>":{"args":[{"name":"second"},{"name":"first"}]}}}
        """);

        var firstManifest = Normalizer.Normalize("opencli", first);
        var secondManifest = Normalizer.Normalize("opencli", second);
        Assert.Equal(["first", "second"], firstManifest.Root.Subcommands.Single().Arguments.Select(argument => argument.Name));
        Assert.Equal(["second", "first"], secondManifest.Root.Subcommands.Single().Arguments.Select(argument => argument.Name));
        Assert.NotEqual(Normalizer.Serialize(firstManifest), Normalizer.Serialize(secondManifest));
    }
    [Fact]
    public void OpenCliNestedCommandPathCollisionsFailClosed()
    {
        var input = OpenCliDocument("""
        {"commands":{"tool run <first>":{},"tool run <second>":{}}}
        """);

        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", input));

        Assert.Equal("OPENCLI_DUPLICATE_COMMAND_PATH", error.Code);
    }

    [Fact]
    public void OpenCliRootCommandPathCollisionsFailClosed()
    {
        var input = OpenCliDocument("""
        {"commands":{"tool":{},"tool <arg>":{}}}
        """);

        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", input));

        Assert.Equal("OPENCLI_DUPLICATE_COMMAND_PATH", error.Code);
    }

    [Fact]
    public void OpenCliArgumentRequiredDefaultMatchesExplicitFalse()
    {
        var omitted = OpenCliDocument("""
        {"commands":{"tool run <value>":{"args":[{"name":"value"}]}}}
        """);
        var explicitFalse = OpenCliDocument("""
        {"commands":{"tool run <value>":{"args":[{"name":"value","required":false}]}}}
        """);

        var omittedManifest = Normalizer.Normalize("opencli", omitted);
        var explicitManifest = Normalizer.Normalize("opencli", explicitFalse);
        Assert.False(omittedManifest.Root.Subcommands.Single().Arguments.Single().Required);
        Assert.Equal(Normalizer.Serialize(omittedManifest), Normalizer.Serialize(explicitManifest));
    }

    [Fact]
    public void OpenCliAlternativeDefaultSourcesAreCanonicalAndOrdered()
    {
        var first = OpenCliDocument("""
        {"global":{"config":{"json":"~/.config/tool/config.json"}},"commands":{"tool run [flags]":{"flags":[{"name":"output","type":"string","default":"text","alternativeSources":[{"type":"$ENV","property":"OUTPUT"},{"type":"$FILE","property":"$.output"}]}]}}}
        """);
        var second = first.Replace("OUTPUT", "OTHER_OUTPUT", StringComparison.Ordinal);

        var firstManifest = Normalizer.Normalize("opencli", first);
        var secondManifest = Normalizer.Normalize("opencli", second);
        var sources = firstManifest.Root.Subcommands.Single().Options.Single().AlternativeSources;
        Assert.Equal(["$ENV", "$FILE"], sources.Select(source => source.Type));
        Assert.NotEqual(Normalizer.Serialize(firstManifest), Normalizer.Serialize(secondManifest));
    }

    [Fact]
    public void RegressionFixtureCoversNeutralRegressionConstructs()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "regression-fixtures.json")));
        var command = manifest.Root.Subcommands.Single();

        Assert.Equal(["--debug", "--output-format"], manifest.GlobalOptions.Select(option => option.Name));
        Assert.Equal(["environment", "region"], command.Arguments.Select(argument => argument.Name));
        Assert.False(command.Arguments[1].Required);
        Assert.Equal(["$ENV", "$FILE"], command.Options.Single(option => option.Name == "--retries").AlternativeSources.Select(source => source.Type));
        Assert.Equal(["text", "7", "7.5", "true"], command.Options.Single(option => option.Name == "--choice").AllowedValues.Select(value => value.ToJsonString().Trim('"')));
    }

    [Fact]
    public void OfficialGlobalFlagsFixtureNormalizesInheritedOptionsAndSources()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "globalflags-cli.ocs.yaml")));

        Assert.Contains(manifest.GlobalOptions, option => option.Name == "--debug");
        var outputFormat = manifest.GlobalOptions.Single(option => option.Name == "--output-format");
        Assert.Equal(["$ENV", "$FILE"], outputFormat.AlternativeSources.Select(source => source.Type));
        Assert.Equal(["json", "text"], outputFormat.AllowedValues.Select(value => value.GetValue<string>()));
    }

    [Fact]
    public void FixRound10FixturePreservesInvocationAndFileSourceContract()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "fix-round10.yaml")));

        Assert.Equal("tool", manifest.Info.Binary);
        Assert.Equal(["json", "yaml"], manifest.GlobalConfig!.FileSources.Select(source => source.Format));
        Assert.Equal("action", manifest.Root.Kind);
        Assert.Equal("\"16777217\"", manifest.Root.Options.Single(option => option.Name == "--mode").DefaultValue!.ToJsonString());
    }

    [Fact]
    public void OfficialPetstoreFixtureNormalizesNestedCommandsAndSources()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "petstore-cli.ocs.json")));

        Assert.Contains(manifest.GlobalOptions, option => option.Name == "--help");
        var login = manifest.Root.Subcommands.Single(command => command.Path == "root / user / login");
        Assert.Equal(["$ENV", "$FILE"], login.Options.Single(option => option.Name == "--username").AlternativeSources.Select(source => source.Type));
        Assert.Contains(manifest.Root.Subcommands, command => command.Path == "root / pet / upload-image");
    }

    [Fact]
    public void TaggedAlpha14CommandKeysStripAllModifierGrammar()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "petstore-cli.ocs.yaml")));

        var paths = manifest.Root.Subcommands.Select(command => command.Path).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("root / list", paths);
        Assert.Contains("root / pet", paths);
        Assert.Contains("root / pet / upload-image", paths);
        Assert.Contains("root / store / order", paths);
        Assert.DoesNotContain(paths, path => path.Contains("--", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.Contains('<') || path.Contains('[') || path.Contains('{'));
        Assert.DoesNotContain(paths, path => path.EndsWith("arguments", StringComparison.Ordinal));
        Assert.Equal(24, paths.Count);
    }

    [Fact]
    public void GlobalFlagCommandKeysStopBeforeInlineFlagModifiers()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "globalflags-cli.ocs.yaml")));

        Assert.Equal(["root / echo", "root / greet", "root / ping", "root / send"], manifest.Root.Subcommands.Select(command => command.Path).OrderBy(path => path, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("{\"commands\":{\"tool {command}\":{\"kind\":\"group\",\"args\":[{\"name\":\"bad\"}]}}}", "OPENCLI_GROUP_COMMAND")]
    [InlineData("{\"commands\":{\"tool {command}\":{\"kind\":\"group\",\"flags\":[{\"name\":\"bad\",\"type\":\"string\"}]}}}", "OPENCLI_GROUP_COMMAND")]
    [InlineData("{\"commands\":{\"tool\":{\"args\":[{\"name\":\"optional\"},{\"name\":\"required\",\"required\":true}]}}}", "OPENCLI_ARGUMENT_ORDER")]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"values\",\"type\":\"string\",\"variadic\":true,\"required\":true}]}}}", "OPENCLI_VARIADIC")]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"string\"},{\"name\":\"other\",\"type\":\"string\",\"aliases\":[\"value\"]}]}}}", "OPENCLI_DUPLICATE_PARAMETER")]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"string\",\"alternativeSources\":[{\"type\":\"$FILE\",\"property\":\"$.value\"}]}]}}}", "OPENCLI_DEFAULT_SOURCE")]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"integer\",\"default\":1.5}]}}}", "OPENCLI_DEFAULT")]
    [InlineData("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"boolean\",\"default\":1}]}}}", "OPENCLI_DEFAULT")]
    public void TaggedAlpha14LogicalValidationRejectsCompleteRuleFamilies(string body, string expectedCode)
    {
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", OpenCliDocument(body)));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void OpaqueExtensionsMayContainReferenceLookingMetadata()
    {
        var manifest = Normalizer.Normalize("opencli", OpenCliDocument("""
            {"x-tool":{"$ref":"https://example.invalid/metadata","include":"offline"},"commands":{"tool":{"x-command":{"$dynamicRef":"metadata"}}}}
            """));

        Assert.Equal("root", manifest.Root.Path);
    }

    [Fact]
    public void ExactNumericDefaultsRemainValidBeyondDecimalRangeAndRoundTrip()
    {
        const string integer = "100000000000000000000000000000000000000000000000000";
        var json = OpenCliDocument("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"integer\",\"type\":\"integer\",\"default\":" + integer + "},{\"name\":\"tiny\",\"type\":\"number\",\"default\":1e-1000},{\"name\":\"negativeZero\",\"type\":\"number\",\"default\":-0.0}]}}}");
        var manifest = Normalizer.Normalize("opencli", json);
        var values = manifest.Root.Options.ToDictionary(option => option.Name, StringComparer.Ordinal);

        Assert.Equal("1e50", values["--integer"].DefaultValue!.ToJsonString());
        Assert.Equal("1e-1000", values["--tiny"].DefaultValue!.ToJsonString());
        Assert.Equal("0", values["--negativeZero"].DefaultValue!.ToJsonString());
        Assert.Empty(CompatibilityAnalyzer.Compare(manifest, manifest).Findings);
        Assert.Equal(Normalizer.Serialize(manifest), Normalizer.Serialize(CanonicalManifestReader.Read(Normalizer.Serialize(manifest))));
    }

    [Theory]
    [InlineData("numeric-integer-domain-fraction.json")]
    [InlineData("numeric-integer-domain-fraction.yaml")]
    public void IntegerChoicesMustBeRepresentableByTheDeclaredType(string fixture)
    {
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", fixture))));

        Assert.Equal("OPENCLI_CHOICE", error.Code);
    }

    [Fact]
    public void CanonicalManifestReaderRejectsNonIntegralIntegerChoices()
    {
        var manifest = Normalizer.Normalize("opencli", OpenCliDocument("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"integer\",\"choices\":[{\"value\":1}]}]}}}"));
        var document = JsonNode.Parse(Normalizer.Serialize(manifest))!.AsObject();
        document["Root"]!["Options"]![0]!["Choices"]![0]!["Value"] = JsonValue.Create(1.25);
        document["Root"]!["Options"]![0]!["AllowedValues"]![0] = JsonValue.Create(1.25);

        var error = Assert.Throws<NormalizationException>(() => CanonicalManifestReader.Read(document.ToJsonString()));

        Assert.Equal("OPENCLI_CHOICE", error.Code);
    }

    [Fact]
    public void ExactIntegralChoicesRemainReflexiveOutsideDecimalRange()
    {
        var manifest = Normalizer.Normalize("opencli", OpenCliDocument("{\"commands\":{\"tool\":{\"flags\":[{\"name\":\"value\",\"type\":\"integer\",\"choices\":[{\"value\":1e1000},{\"value\":-0.0}]}]}}}"));

        Assert.Empty(CompatibilityAnalyzer.Compare(manifest, manifest).Findings);
        Assert.Empty(CompatibilityAnalyzer.Compare(CanonicalManifestReader.Read(Normalizer.Serialize(manifest)), manifest).Findings);
    }

    [Fact]
    public void CanonicalManifestReaderRejectsEveryHostileSourceImpossibleState()
    {
        var mutations = new (string Name, Action<JsonObject> Mutate)[]
        {
            ("duplicate-file-format", document => ((JsonArray)document["GlobalConfig"]!["FileSources"]!).Add(((JsonArray)document["GlobalConfig"]!["FileSources"]!)[0]!.DeepClone())),
            ("empty-file-path", document => document["GlobalConfig"]!["FileSources"]![0]!["Path"] = ""),
            ("duplicate-global-exit-code", document => ((JsonArray)document["GlobalExitCodes"]!).Add(((JsonArray)document["GlobalExitCodes"]!)[0]!.DeepClone())),
            ("duplicate-command-exit-code", document => ((JsonArray)document["Root"]!["ExitCodes"]!).Add(((JsonArray)document["Root"]!["ExitCodes"]!)[0]!.DeepClone())),
            ("missing-info-title", document => document["Info"]!["Title"] = null),
            ("invalid-command-path", document => document["Root"]!["Subcommands"]![0]!["Path"] = "root /"),
            ("invalid-source-type", document => document["GlobalOptions"]![0]!["AlternativeSources"]![0]!["Type"] = "$BAD"),
            ("empty-source-property", document => document["GlobalOptions"]![0]!["AlternativeSources"]![0]!["Property"] = ""),
            ("file-source-without-config", document => document["GlobalConfig"] = null),
            ("argument-default", document => document["Root"]!["Subcommands"]![0]!["Arguments"]![0]!["DefaultValue"] = "not-allowed"),
            ("unrepresentable-status", document => document["Root"]!["Status"] = "DEPRECATED"),
            ("invalid-arity", document => document["GlobalOptions"]![0]!["ArityMinimum"] = 2),
            ("invalid-option-name", document => document["GlobalOptions"]![0]!["Name"] = "verbose"),
            ("invalid-option-alias", document => document["GlobalOptions"]![0]!["Aliases"]![0] = "bad alias"),
            ("contradictory-domains", document => document["GlobalOptions"]![0]!["AllowedValues"]![0] = "other"),
            ("group-local-option", document => { document["Root"]!["Kind"] = "group"; }),
            ("invalid-exit-status", document => document["GlobalExitCodes"]![0]!["Status"] = "DEPRECATED")
        };

        foreach (var (name, mutate) in mutations)
        {
            var baseline = JsonNode.Parse(Normalizer.Serialize(Normalizer.Normalize("opencli", HostileCanonicalSeed())))!.AsObject();
            mutate(baseline);

            var error = Record.Exception(() => CanonicalManifestReader.Read(baseline.ToJsonString()));

            Assert.True(error is NormalizationException, name);
            var normalizationError = (NormalizationException)error!;
            Assert.NotEqual("UNEXPECTED_ERROR", normalizationError.Code);
            Assert.False(string.IsNullOrWhiteSpace(normalizationError.Message), name);
        }
    }

    [Theory]
    [InlineData("number", "\"1\"")]
    [InlineData("integer", "1.5")]
    [InlineData("boolean", "1")]
    public void ChoicesMustMatchTheirDeclaredType(string type, string value)
    {
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", OpenCliDocument($"{{\"commands\":{{\"tool\":{{\"flags\":[{{\"name\":\"value\",\"type\":\"{type}\",\"choices\":[{{\"value\":{value}}}]}}]}}}}}}")));

        Assert.Equal("OPENCLI_CHOICE", error.Code);
    }

    [Theory]
    [InlineData("example-cli.json")]
    [InlineData("example-cli-reordered.json")]
    [InlineData("example-cli.yaml")]
    [InlineData("fix-round10.yaml")]
    [InlineData("fix-round17-scope-and-trie.json")]
    [InlineData("global-config-order-a.json")]
    [InlineData("global-config-order-a.yaml")]
    [InlineData("global-config-order-b.json")]
    [InlineData("global-config-order-b.yaml")]
    [InlineData("globalflags-cli.ocs.yaml")]
    [InlineData("numeric-defaults-exponent.json")]
    [InlineData("numeric-defaults-extreme.json")]
    [InlineData("numeric-defaults-plain.json")]
    [InlineData("numeric-defaults-yaml.json")]
    [InlineData("numeric-defaults-yaml.yaml")]
    [InlineData("petstore-cli.ocs.json")]
    [InlineData("petstore-cli.ocs.yaml")]
    [InlineData("pleasantries-cli.ocs.yaml")]
    [InlineData("regression-fixtures.json")]
    [InlineData("valid-argument-passthrough.json")]
    [InlineData("valid-argument-passthrough-false.json")]
    public void ValidFixturesRemainReflexiveAfterCanonicalRoundTrip(string fixture)
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", fixture)));
        var roundTrip = CanonicalManifestReader.Read(Normalizer.Serialize(manifest));

        Assert.Empty(CompatibilityAnalyzer.Compare(manifest, manifest).Findings);
        Assert.Empty(CompatibilityAnalyzer.Compare(roundTrip, roundTrip).Findings);
    }

    [Theory]
    [InlineData("{\"commands\":{\"tool one\":{\"aliases\":[\"shared\"]},\"tool two\":{\"aliases\":[\"shared\"]}}}", "OPENCLI_DUPLICATE_INVOCATION")]
    [InlineData("{\"commands\":{\"tool one\":{\"aliases\":[\"two\"]},\"tool two\":{}}}", "OPENCLI_DUPLICATE_INVOCATION")]
    [InlineData("{\"commands\":{\"tool parent\":{\"aliases\":[\"p\"]},\"tool parent child\":{},\"tool p child\":{}}}", "OPENCLI_DUPLICATE_INVOCATION")]
    [InlineData("{\"global\":{\"flags\":[{\"name\":\"one\",\"type\":\"string\",\"aliases\":[\"shared\"]},{\"name\":\"two\",\"type\":\"string\",\"aliases\":[\"shared\"]}]},\"commands\":{\"tool\":{}}}", "OPENCLI_DUPLICATE_PARAMETER")]
    public void AmbiguousAcceptedInvocationsFailDuringNormalization(string body, string expectedCode)
    {
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", OpenCliDocument(body)));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void CanonicalManifestReaderRejectsAmbiguousAcceptedOptionNames()
    {
        var manifest = Normalizer.Normalize("opencli", OpenCliDocument("""
            {"commands":{"tool":{"flags":[{"name":"one","type":"string"},{"name":"two","type":"string"}]}}}
            """));
        var document = JsonNode.Parse(Normalizer.Serialize(manifest))!.AsObject();
        var options = document["Root"]!["Options"]!.AsArray();
        options[0]!["Aliases"] = new JsonArray("shared");
        options[1]!["Aliases"] = new JsonArray("shared");

        var error = Assert.Throws<NormalizationException>(() => CanonicalManifestReader.Read(document.ToJsonString()));

        Assert.Equal("OPENCLI_DUPLICATE_PARAMETER", error.Code);
    }

    [Fact]
    public void CanonicalManifestReaderRejectsAmbiguousCommandAliases()
    {
        var manifest = Normalizer.Normalize("opencli", OpenCliDocument("""
            {"commands":{"tool one":{},"tool two":{}}}
            """));
        var document = JsonNode.Parse(Normalizer.Serialize(manifest))!.AsObject();
        var commands = document["Root"]!["Subcommands"]!.AsArray();
        commands[0]!["Aliases"] = new JsonArray("shared");
        commands[1]!["Aliases"] = new JsonArray("shared");

        var error = Assert.Throws<NormalizationException>(() => CanonicalManifestReader.Read(document.ToJsonString()));

        Assert.Equal("OPENCLI_DUPLICATE_INVOCATION", error.Code);
    }

    [Fact]
    public void UnexpectedAdapterFailureIsBounded()
    {
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"t\",\"binary\":\"b\",\"version\":\"1\"},\"commands\":{\"tool\":{\"flags\":[{\"name\":\"x\",\"type\":{}}]}}}"));
        Assert.Equal("OPENCLI_FLAG_TYPE", error.Code);
    }

    [Fact]
    public void NonVariadicArgumentsRejectItemBounds()
    {
        var document = OpenCliDocument("""
            {"commands":{"tool":{"args":[{"name":"value","minItems":1}]}}}
            """);

        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", document));

        Assert.Equal("OPENCLI_ARITY", error.Code);
    }

    [Theory]
    [InlineData("[{\"name\":\"rest\",\"variadic\":true},{\"name\":\"later\"}]")]
    [InlineData("[{\"name\":\"first\",\"variadic\":true},{\"name\":\"second\",\"variadic\":true}]")]
    public void VariadicArgumentsMustBeLastAndUnique(string arguments)
    {
        var document = OpenCliDocument($"{{\"commands\":{{\"tool\":{{\"args\":{arguments}}}}}}}");

        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", document));

        Assert.Equal("OPENCLI_VARIADIC", error.Code);
    }

    [Fact]
    public void LastVariadicArgumentPreservesItsBounds()
    {
        var document = OpenCliDocument("""
            {"commands":{"tool":{"args":[{"name":"rest","variadic":true,"minItems":1,"maxItems":3}]}}}
            """);

        var manifest = Normalizer.Normalize("opencli", document);
        var argument = manifest.Root.Arguments.Single();

        Assert.True(argument.Variadic);
        Assert.Equal(1, argument.ArityMinimum);
        Assert.Equal(3, argument.ArityMaximum);
    }

    private static string OpenCliDocument(string commandsAndOptionalProperties)
    {
        var document = JsonNode.Parse(commandsAndOptionalProperties)!.AsObject();
        document["opencliVersion"] = Normalizer.OpenCliVersion;
        document["info"] = new JsonObject
        {
            ["title"] = "Tool",
            ["binary"] = "tool",
            ["version"] = "1"
        };
        return document.ToJsonString();
    }

    private static string HostileCanonicalSeed() => OpenCliDocument("""
        {
          "global": {
            "config": {"json": "config.json"},
            "exitCodes": [{"code": 0, "status": "OK", "summary": "ok"}],
            "flags": [{"name": "verbose", "type": "string", "aliases": ["v"], "alternativeSources": [{"type": "$ENV", "property": "VERBOSE"}, {"type": "$FILE", "property": "$.verbose"}], "choices": [{"value": "yes"}]}]
          },
          "commands": {
            "tool": {"exitCodes": [{"code": 0, "status": "OK", "summary": "ok"}], "flags": [{"name": "local", "type": "boolean"}]},
            "tool run <target>": {"args": [{"name": "target", "type": "string"}]}
          }
        }
        """);

    private static string NumericYamlDocument(string scalar) => $$"""
        opencliVersion: 1.0.0-alpha.14
        info: {title: Tool, binary: tool, version: '1'}
        commands:
          tool:
            flags:
              - name: value
                type: number
                default: {{scalar}}
        """;

    private static string OpenCliDocumentWithNestedObjectDepth(int nestedObjectDepth)
    {
        var document = JsonNode.Parse(OpenCliDocument("{}"))!.AsObject();
        var current = document;
        for (var index = 0; index < nestedObjectDepth; index++)
        {
            var nested = new JsonObject();
            current["x-depth"] = nested;
            current = nested;
        }

        current["value"] = "ok";
        return document.ToJsonString();
    }

    private static string ParameterCollectionDocument(string collection, int count)
    {
        JsonNode values = collection switch
        {
            "flags" or "args" => new JsonArray(Enumerable.Range(0, count).Select(index => (JsonNode)new JsonObject
            {
                ["name"] = $"value{index}",
                ["type"] = "string"
            }).ToArray()),
            "choices" => new JsonArray(Enumerable.Range(0, count).Select(index => (JsonNode)new JsonObject
            {
                ["value"] = $"value{index}"
            }).ToArray()),
            "aliases" => new JsonArray(Enumerable.Range(0, count).Select(index => (JsonNode)$"-v{index}").ToArray()),
            "alternativeSources" => new JsonArray(Enumerable.Range(0, count).Select(index => (JsonNode)new JsonObject
            {
                ["type"] = "$ENV",
                ["property"] = $"VALUE{index}"
            }).ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(collection))
        };

        JsonObject command;
        if (collection is "flags" or "args")
        {
            command = new JsonObject { [collection] = values };
        }
        else
        {
            var parameter = new JsonObject
            {
                ["name"] = "value",
                ["type"] = "string",
                [collection] = values
            };
            command = new JsonObject { ["flags"] = new JsonArray(parameter) };
        }

        return OpenCliDocument(new JsonObject
        {
            ["commands"] = new JsonObject { ["tool"] = command }
        }.ToJsonString());
    }

    private static string Fixture(params string[] parts)
    {
        return Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", .. parts]));
    }
}
