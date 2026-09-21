using System.Text.Json.Nodes;
using KeelMatrix.CliContract.Core;
using Xunit;

namespace KeelMatrix.CliContract.Tests;

public sealed class NormalizationTests
{
    [Fact]
    public void OpenCliCanonicalizesFixture()
    {
        var input = File.ReadAllText(Fixture("opencli", "phase0.json"));
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
    public void OpenCliSourceOrderAndLineEndingsDoNotChangeBytes()
    {
        var first = File.ReadAllText(Fixture("opencli", "phase0.json"));
        var second = File.ReadAllText(Fixture("opencli", "phase0-reordered.json"));
        Assert.Equal(Normalizer.Serialize(Normalizer.Normalize("opencli", first)), Normalizer.Serialize(Normalizer.Normalize("opencli", second.Replace("\n", "\r\n"))));

        var yaml = File.ReadAllText(Fixture("opencli", "phase0.yaml"));
        Assert.Equal(Normalizer.Serialize(Normalizer.Normalize("opencli", first)), Normalizer.Serialize(Normalizer.Normalize("opencli", yaml)));
    }

    [Fact]
    public void DotnetSchemaNormalizesNestedCommandsAndOptions()
    {
        var input = File.ReadAllText(Fixture("dotnet", "synthetic-minimal.json"));
        var manifest = Normalizer.Normalize("dotnet", input);
        var command = manifest.Root.Subcommands.Single(c => c.Path == "root / deploy").Subcommands.Single(c => c.Path == "root / deploy / region");

        Assert.Equal("10.0.401", manifest.SourceVersion);
        Assert.Equal("--format", command.Options.Single().Name);
        Assert.Equal(["-f"], command.Options.Single().Aliases);
        Assert.Equal(1, command.Options.Single().ArityMinimum);
    }

    [Fact]
    public void UnsupportedOpenCliVersionFailsClosed()
    {
        var input = File.ReadAllText(Fixture("opencli", "phase0.json"));
        var changed = input.Replace(Normalizer.OpenCliVersion, "1.0.0-alpha.13", StringComparison.Ordinal);
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", changed));
        Assert.Equal("UNSUPPORTED_OPENCLI_VERSION", error.Code);
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
        var input = "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"t\",\"binary\":\"b\",\"version\":\"1\"},\"commands\":{\"tool\":{\"description\":\"" + new string('é', 1_000) + "\"}}}";
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
            exactCommands[$"tool {index}"] = new JsonObject();
        }

        for (var index = 0; index < 4; index++)
        {
            overCommands[$"tool {index}"] = new JsonObject();
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
    public void DotnetObjectCollectionLimitAcceptsExactBoundaryAndRejectsOneOver()
    {
        var exact = DotnetParameterMapDocument(3);
        var over = DotnetParameterMapDocument(4);
        var limits = new NormalizationLimits(MaxCollectionItems: 3);

        Normalizer.Normalize("dotnet", exact, limits);

        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("dotnet", over, limits));
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

    [Fact]
    public void OpenCliPreservesGlobalFlagsAtRoot()
    {
        var input = OpenCliDocument("""
        {
          "global": {"flags": [{"name": "debug", "type": "boolean"}, {"name": "timeout", "type": "integer"}]},
          "commands": {"tool run [flags]": {}}
        }
        """);

        var manifest = Normalizer.Normalize("opencli", input);
        Assert.Equal(["--debug", "--timeout"], manifest.Root.Options.Select(option => option.Name));

        var withoutTimeout = input.Replace(",{\"name\":\"timeout\",\"type\":\"integer\"}", String.Empty, StringComparison.Ordinal);
        var changed = Normalizer.Normalize("opencli", withoutTimeout);
        Assert.NotEqual(Normalizer.Serialize(manifest), Normalizer.Serialize(changed));
        Assert.DoesNotContain(changed.Root.Options, option => option.Name == "--timeout");
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
        {"commands":{"tool run [flags]":{"flags":[{"name":"output","type":"string","default":"text","alternativeSources":[{"type":"$ENV","property":"OUTPUT"},{"type":"$FILE","property":"$.output"}]}]}}}
        """);
        var second = first.Replace("OUTPUT", "OTHER_OUTPUT", StringComparison.Ordinal);

        var firstManifest = Normalizer.Normalize("opencli", first);
        var secondManifest = Normalizer.Normalize("opencli", second);
        var sources = firstManifest.Root.Subcommands.Single().Options.Single().AlternativeSources;
        Assert.Equal(["$ENV", "$FILE"], sources.Select(source => source.Type));
        Assert.NotEqual(Normalizer.Serialize(firstManifest), Normalizer.Serialize(secondManifest));
    }

    [Fact]
    public void RegressionFixtureCoversPhase0FixRoundConstructs()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "phase0-regressions.json")));
        var command = manifest.Root.Subcommands.Single();

        Assert.Equal(["--debug", "--output-format"], manifest.Root.Options.Select(option => option.Name));
        Assert.Equal(["environment", "region"], command.Arguments.Select(argument => argument.Name));
        Assert.False(command.Arguments[1].Required);
        Assert.Equal(["$ENV", "$FILE"], command.Options.Single(option => option.Name == "--retries").AlternativeSources.Select(source => source.Type));
        Assert.Equal(["text", "7", "7.5", "true"], command.Options.Single(option => option.Name == "--choice").AllowedValues.Select(value => value.ToJsonString().Trim('"')));
    }

    [Fact]
    public void OfficialGlobalFlagsFixtureNormalizesRootOptionsAndSources()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "globalflags-cli.ocs.yaml")));

        Assert.Contains(manifest.Root.Options, option => option.Name == "--debug");
        var outputFormat = manifest.Root.Options.Single(option => option.Name == "--output-format");
        Assert.Equal(["$ENV", "$FILE"], outputFormat.AlternativeSources.Select(source => source.Type));
        Assert.Equal(["json", "text"], outputFormat.AllowedValues.Select(value => value.GetValue<string>()));
    }

    [Fact]
    public void OfficialPetstoreFixtureNormalizesNestedCommandsAndSources()
    {
        var manifest = Normalizer.Normalize("opencli", File.ReadAllText(Fixture("opencli", "petstore-cli.ocs.json")));

        Assert.Contains(manifest.Root.Options, option => option.Name == "--help");
        var login = manifest.Root.Subcommands.Single(command => command.Path == "root / user / login");
        Assert.Equal(["$ENV", "$FILE"], login.Options.Single(option => option.Name == "--username").AlternativeSources.Select(source => source.Type));
        Assert.Contains(manifest.Root.Subcommands, command => command.Path == "root / pet / upload-image");
    }

    [Fact]
    public void UnexpectedAdapterFailureIsBounded()
    {
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"t\",\"binary\":\"b\",\"version\":\"1\"},\"commands\":{\"tool\":{\"flags\":[{\"name\":\"x\",\"type\":{}}]}}}"));
        Assert.Equal("OPENCLI_FLAG_TYPE", error.Code);
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

    private static string OpenCliDocumentWithNestedObjectDepth(int nestedObjectDepth)
    {
        var document = JsonNode.Parse(OpenCliDocument("{}"))!.AsObject();
        var current = document;
        for (var index = 0; index < nestedObjectDepth; index++)
        {
            var nested = new JsonObject();
            current["ignored"] = nested;
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

    private static string DotnetParameterMapDocument(int count)
    {
        var options = new JsonObject();
        for (var index = 0; index < count; index++)
        {
            options[$"--value{index}"] = new JsonObject
            {
                ["arity"] = new JsonObject
                {
                    ["minimum"] = 0,
                    ["maximum"] = 1
                }
            };
        }

        return new JsonObject
        {
            ["name"] = "dotnet",
            ["version"] = "10.0.401",
            ["options"] = options
        }.ToJsonString();
    }

    private static string Fixture(params string[] parts)
    {
        return Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", .. parts]));
    }
}
