using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KeelMatrix.CliContract.Core;

public sealed record NormalizationLimits(
    int MaxInputBytes = 2 * 1024 * 1024,
    int MaxNodes = 20_000,
    int MaxDepth = 64,
    int MaxStringLength = 16_384,
    int MaxCollectionItems = 2_000);

public sealed class NormalizationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class Normalizer
{
    public const string OpenCliVersion = "1.0.0-alpha.14";
    public const string DotnetSchemaContract = "dotnet-cli-schema-v1-observed";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null
    };

    public static CanonicalManifest Normalize(string adapter, string input, NormalizationLimits? limits = null)
    {
        try
        {
            var bounded = limits ?? new NormalizationLimits();
            if (Encoding.UTF8.GetByteCount(input) > bounded.MaxInputBytes)
            {
                throw new NormalizationException("INPUT_TOO_LARGE", "The input description exceeds the configured size limit in UTF-8 bytes.");
            }

            return adapter.ToLowerInvariant() switch
            {
                "opencli" => NormalizeOpenCli(ParseOpenCliDocument(input, bounded), bounded),
                "dotnet" => NormalizeDotnet(ParseJson(input, bounded, "DOTNET_JSON"), bounded),
                _ => throw new NormalizationException("UNSUPPORTED_ADAPTER", "The requested input adapter is not supported by this probe.")
            };
        }
        catch (NormalizationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new NormalizationException("NORMALIZATION_ERROR", "The input description could not be normalized.");
        }
    }

    public static string Serialize(CanonicalManifest manifest)
    {
        return JsonSerializer.Serialize(manifest, JsonOptions) + "\n";
    }

    private static JsonNode ParseOpenCliDocument(string input, NormalizationLimits limits)
    {
        var first = input.TrimStart();
        if (first.StartsWith('{') || first.StartsWith('['))
        {
            return ParseJson(input, limits, "OPENCLI_JSON");
        }

        RejectYamlAnchorsAndAliases(input, limits);

        try
        {
            var yaml = new DeserializerBuilder()
                .WithDuplicateKeyChecking()
                .WithAttemptingUnquotedStringTypeDeserialization()
                .Build()
                .Deserialize<object>(input);
            var json = ConvertYamlValue(yaml, 0, limits, new Counter());
            if (json is null)
            {
                throw new NormalizationException("MALFORMED_YAML", "The YAML document is empty.");
            }

            return json;
        }
        catch (NormalizationException)
        {
            throw;
        }
        catch (YamlException exception) when (exception.Message.StartsWith("Encountered duplicate key ", StringComparison.Ordinal))
        {
            throw new NormalizationException("DUPLICATE_YAML_KEY", "The YAML document contains a duplicate key.");
        }
        catch (Exception)
        {
            throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        }
    }

    private static void RejectYamlAnchorsAndAliases(string input, NormalizationLimits limits)
    {
        try
        {
            var parser = new Parser(new StringReader(input));
            var depth = 0;
            var counter = new Counter();
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case AnchorAlias:
                        throw new NormalizationException("YAML_ALIASES_UNSUPPORTED", "YAML anchors and aliases are not accepted by the bounded probe.");
                    case MappingStart mapping:
                        CheckYamlNode(mapping, depth, limits, counter);
                        depth++;
                        break;
                    case SequenceStart sequence:
                        CheckYamlNode(sequence, depth, limits, counter);
                        depth++;
                        break;
                    case Scalar scalar:
                        CheckYamlNode(scalar, depth, limits, counter);
                        break;
                    case MappingEnd:
                    case SequenceEnd:
                        if (--depth < 0)
                        {
                            throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
                        }

                        break;
                }
            }

            if (depth != 0)
            {
                throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
            }
        }
        catch (NormalizationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        }
    }

    private static void CheckYamlNode(NodeEvent node, int depth, NormalizationLimits limits, Counter counter)
    {
        CheckNode(depth, limits, counter);
        if (!node.Anchor.IsEmpty)
        {
            throw new NormalizationException("YAML_ALIASES_UNSUPPORTED", "YAML anchors and aliases are not accepted by the bounded probe.");
        }
    }

    private static JsonNode ParseJson(string input, NormalizationLimits limits, string code)
    {
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                MaxDepth = limits.MaxDepth == int.MaxValue ? int.MaxValue : limits.MaxDepth + 1,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var counter = new Counter();
            return ConvertJsonElement(document.RootElement, 0, limits, counter)
                ?? throw new NormalizationException("MALFORMED_JSON", "The JSON document is empty.");
        }
        catch (NormalizationException)
        {
            throw;
        }
        catch (JsonException exception) when (exception.Message.Contains("maximum depth", StringComparison.OrdinalIgnoreCase))
        {
            throw new NormalizationException("DEPTH_LIMIT", "The schema exceeds the configured nesting limit.");
        }
        catch (JsonException)
        {
            throw new NormalizationException(code == "DOTNET_JSON" ? "MALFORMED_DOTNET_JSON" : "MALFORMED_JSON", "The JSON document could not be parsed.");
        }
    }

    private static JsonNode? ConvertJsonElement(JsonElement value, int depth, NormalizationLimits limits, Counter counter)
    {
        CheckNode(depth, limits, counter);
        return value.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(value, depth, limits, counter),
            JsonValueKind.Array => ConvertArray(value, depth, limits, counter),
            JsonValueKind.String => BoundedString(JsonValue.Create(value.GetString() ?? string.Empty), limits),
            JsonValueKind.Number => JsonNode.Parse(value.GetRawText()),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            JsonValueKind.Null => null,
            _ => throw new NormalizationException("UNSUPPORTED_JSON", "The JSON value kind is not supported.")
        };
    }

    private static JsonObject ConvertObject(JsonElement value, int depth, NormalizationLimits limits, Counter counter)
    {
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length > limits.MaxCollectionItems)
        {
            throw new NormalizationException("COLLECTION_TOO_LARGE", "A JSON object exceeds the configured property limit.");
        }

        var result = new JsonObject();
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (!propertyNames.Add(property.Name))
            {
                throw new NormalizationException("DUPLICATE_JSON_KEY", "The JSON document contains a duplicate object key.");
            }

            BoundedString(property.Name, limits);
            result[property.Name] = ConvertJsonElement(property.Value, depth + 1, limits, counter);
        }

        return result;
    }

    private static JsonArray ConvertArray(JsonElement value, int depth, NormalizationLimits limits, Counter counter)
    {
        if (value.GetArrayLength() > limits.MaxCollectionItems)
        {
            throw new NormalizationException("COLLECTION_TOO_LARGE", "A JSON collection exceeds the configured item limit.");
        }

        var result = new JsonArray();
        foreach (var item in value.EnumerateArray())
        {
            result.Add(ConvertJsonElement(item, depth + 1, limits, counter));
        }

        return result;
    }

    private static JsonNode? ConvertYamlValue(object? value, int depth, NormalizationLimits limits, Counter counter)
    {
        CheckNode(depth, limits, counter);
        if (value is null)
        {
            return null;
        }

        if (value is IDictionary dictionary)
        {
            if (dictionary.Count > limits.MaxCollectionItems)
            {
                throw new NormalizationException("COLLECTION_TOO_LARGE", "A YAML mapping exceeds the configured item limit.");
            }

            var result = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = BoundedString(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, limits);
                if (result.ContainsKey(key))
                {
                    throw new NormalizationException("DUPLICATE_YAML_KEY", "The YAML document contains a duplicate key.");
                }

                result[key] = ConvertYamlValue(entry.Value, depth + 1, limits, counter);
            }

            return result;
        }

        if (value is IEnumerable sequence and not string)
        {
            var result = new JsonArray();
            var count = 0;
            foreach (var item in sequence)
            {
                if (++count > limits.MaxCollectionItems)
                {
                    throw new NormalizationException("COLLECTION_TOO_LARGE", "A YAML sequence exceeds the configured item limit.");
                }

                result.Add(ConvertYamlValue(item, depth + 1, limits, counter));
            }

            return result;
        }

        return value switch
        {
            bool boolean => JsonValue.Create(boolean),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => JsonNode.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)!,
            _ => BoundedString(JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty), limits)
        };
    }

    private static CanonicalManifest NormalizeOpenCli(JsonNode document, NormalizationLimits limits)
    {
        var root = RequireObject(document, "OPENCLI_ROOT");
        var version = RequiredString(root, "opencliVersion", "OPENCLI_VERSION");
        if (!string.Equals(version, OpenCliVersion, StringComparison.Ordinal))
        {
            throw new NormalizationException("UNSUPPORTED_OPENCLI_VERSION", "Only OpenCLI 1.0.0-alpha.14 is accepted by this probe.");
        }

        var info = RequireObject(root["info"], "OPENCLI_INFO");
        _ = RequiredString(info, "title", "OPENCLI_INFO");
        _ = RequiredString(info, "binary", "OPENCLI_INFO");
        _ = RequiredString(info, "version", "OPENCLI_INFO");
        var commandsNode = OptionalProperty(root, "commands", "OPENCLI_COMMANDS");
        var commands = commandsNode is null ? [] : RequireObject(commandsNode, "OPENCLI_COMMANDS");
        var normalized = new List<CanonicalCommand>();
        foreach (var property in commands.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            normalized.Add(NormalizeOpenCliCommand(property.Key, RequireObject(property.Value, "OPENCLI_COMMAND"), limits));
        }

        var globalNode = OptionalProperty(root, "global", "OPENCLI_GLOBAL");
        var global = globalNode is null ? null : RequireObject(globalNode, "OPENCLI_GLOBAL");
        var globalOptions = global is null
            ? Array.Empty<CanonicalOption>()
            : ReadOpenCliParameters(OptionalProperty(global, "flags", "OPENCLI_COLLECTION"), true, limits).Cast<CanonicalOption>().ToArray();
        var rootCommand = normalized.FirstOrDefault(c => c.Path == "root");

        return new CanonicalManifest
        {
            Adapter = "opencli",
            SourceVersion = version,
            Root = new CanonicalCommand
            {
                Path = "root",
                Subcommands = normalized.Where(c => c.Path != "root").OrderBy(c => c.Path, StringComparer.Ordinal).ToArray(),
                Arguments = rootCommand?.Arguments ?? [],
                Options = globalOptions.Concat(rootCommand?.Options ?? []).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray(),
                Aliases = rootCommand?.Aliases ?? [],
                Summary = rootCommand?.Summary,
                Description = rootCommand?.Description
            }
        };
    }

    private static CanonicalCommand NormalizeOpenCliCommand(string key, JsonObject value, NormalizationLimits limits)
    {
        var path = ParseOpenCliPath(key);
        var aliases = Strings(OptionalProperty(value, "aliases", "COLLECTION_TYPE"), limits).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var arguments = ReadOpenCliParameters(OptionalProperty(value, "args", "OPENCLI_COLLECTION"), false, limits).Cast<CanonicalArgument>().ToArray();
        var options = ReadOpenCliParameters(OptionalProperty(value, "flags", "OPENCLI_COLLECTION"), true, limits).Cast<CanonicalOption>().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        return new CanonicalCommand
        {
            Path = path,
            Aliases = aliases,
            Summary = OptionalString(value, "summary", limits),
            Description = OptionalString(value, "description", limits),
            Status = null,
            Arguments = arguments,
            Options = options
        };
    }

    private static IEnumerable<CanonicalParameter> ReadOpenCliParameters(JsonNode? node, bool option, NormalizationLimits limits)
    {
        if (node is null)
        {
            yield break;
        }

        var array = node as JsonArray ?? throw new NormalizationException("OPENCLI_COLLECTION", "OpenCLI args and flags must be arrays.");
        if (array.Count > limits.MaxCollectionItems)
        {
            throw new NormalizationException("COLLECTION_TOO_LARGE", "An OpenCLI parameter collection exceeds the configured item limit.");
        }

        foreach (var item in array)
        {
            var parameter = RequireObject(item, "OPENCLI_PARAMETER");
            var name = RequiredString(parameter, "name", "OPENCLI_PARAMETER");
            if (!option && (parameter.ContainsKey("default") || parameter.ContainsKey("alternativeSources")))
            {
                throw new NormalizationException("OPENCLI_ARGUMENT_FIELD", "OpenCLI arguments do not support default or alternativeSources fields.");
            }

            var required = OptionalBoolean(parameter, "required") ?? false;
            var variadic = OptionalBoolean(parameter, "variadic") ?? false;
            var minimum = variadic ? OptionalInt(parameter, "minItems") ?? (required == true ? 1 : 0) : required == true ? 1 : 0;
            var maximum = variadic ? OptionalInt(parameter, "maxItems") : 1;
            var choices = ReadChoices(OptionalProperty(parameter, "choices", "OPENCLI_CHOICES"), limits);
            var type = option
                ? RequiredString(parameter, "type", "OPENCLI_FLAG_TYPE", limits)
                : OptionalString(parameter, "type", limits);
            var alternativeSources = option
                ? ReadAlternativeSources(OptionalProperty(parameter, "alternativeSources", "OPENCLI_DEFAULT_SOURCES"), limits)
                : [];
            var common = new ParameterValues(
                name,
                OptionalString(parameter, "summary", limits),
                OptionalString(parameter, "description", limits),
                type,
                required,
                minimum,
                maximum,
                choices,
                option ? OptionalScalar(parameter, "default", limits) : null,
                alternativeSources,
                null);
            if (option)
            {
                yield return new CanonicalOption
                {
                    Name = "--" + name.TrimStart('-'),
                    Aliases = Strings(parameter["aliases"], limits).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    Summary = common.Summary,
                    Description = common.Description,
                    Type = common.Type,
                    Required = common.Required,
                    ArityMinimum = common.Minimum,
                    ArityMaximum = common.Maximum,
                    AllowedValues = common.AllowedValues,
                    DefaultValue = common.DefaultValue,
                    AlternativeSources = common.AlternativeSources,
                    Status = common.Status
                };
            }
            else
            {
                yield return new CanonicalArgument
                {
                    Name = name,
                    Summary = common.Summary,
                    Description = common.Description,
                    Type = common.Type,
                    Required = common.Required,
                    ArityMinimum = common.Minimum,
                    ArityMaximum = common.Maximum,
                    AllowedValues = common.AllowedValues,
                    DefaultValue = common.DefaultValue,
                    AlternativeSources = common.AlternativeSources,
                    Status = common.Status
                };
            }
        }
    }

    private static CanonicalManifest NormalizeDotnet(JsonNode document, NormalizationLimits limits)
    {
        var root = RequireObject(document, "DOTNET_ROOT");
        var name = RequiredString(root, "name", "DOTNET_ROOT");
        var version = RequiredString(root, "version", "DOTNET_ROOT");
        var rootCommand = NormalizeDotnetCommand("root", root, limits);
        return new CanonicalManifest { Adapter = "dotnet", SourceVersion = version, Root = rootCommand };
    }

    private static CanonicalCommand NormalizeDotnetCommand(string path, JsonObject value, NormalizationLimits limits)
    {
        var arguments = ReadDotnetParameters(value["arguments"], false, limits).Cast<CanonicalArgument>().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var options = ReadDotnetParameters(value["options"], true, limits).Cast<CanonicalOption>().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var children = new List<CanonicalCommand>();
        if (value["subcommands"] is JsonObject subcommands)
        {
            foreach (var child in subcommands.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                children.Add(NormalizeDotnetCommand(path == "root" ? "root / " + child.Key : path + " / " + child.Key, RequireObject(child.Value, "DOTNET_COMMAND"), limits));
            }
        }

        return new CanonicalCommand
        {
            Path = path,
            Summary = OptionalString(value, "description", limits),
            Description = OptionalString(value, "description", limits),
            Arguments = arguments,
            Options = options,
            Subcommands = children.ToArray()
        };
    }

    private static IEnumerable<CanonicalParameter> ReadDotnetParameters(JsonNode? node, bool option, NormalizationLimits limits)
    {
        if (node is null)
        {
            yield break;
        }

        var map = node as JsonObject ?? throw new NormalizationException("DOTNET_COLLECTION", "The .NET CLI-schema parameter collection must be an object.");
        foreach (var property in map.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var value = RequireObject(property.Value, "DOTNET_PARAMETER");
            var arity = RequireObject(value["arity"], "DOTNET_ARITY");
            var minimum = OptionalInt(arity, "minimum");
            var maximum = OptionalInt(arity, "maximum");
            var required = option ? OptionalBoolean(value, "required") : minimum is not null ? minimum > 0 : null;
            var common = new ParameterValues(
                option ? property.Key.TrimStart('-') : property.Key,
                null,
                OptionalString(value, "description", limits),
                OptionalString(value, "valueType", limits),
                required,
                minimum,
                maximum,
                [],
                OptionalBoolean(value, "hasDefaultValue") == true ? value["defaultValue"]?.DeepClone() : null,
                [],
                null);
            if (option)
            {
                yield return new CanonicalOption
                {
                    Name = property.Key.StartsWith("--", StringComparison.Ordinal) ? property.Key : "--" + property.Key,
                    Aliases = Strings(value["aliases"], limits).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    Summary = common.Summary,
                    Description = common.Description,
                    Type = common.Type,
                    Required = common.Required,
                    ArityMinimum = common.Minimum,
                    ArityMaximum = common.Maximum,
                    AllowedValues = common.AllowedValues,
                    DefaultValue = common.DefaultValue,
                    AlternativeSources = common.AlternativeSources,
                    Status = common.Status
                };
            }
            else
            {
                yield return new CanonicalArgument
                {
                    Name = common.Name,
                    Summary = common.Summary,
                    Description = common.Description,
                    Type = common.Type,
                    Required = common.Required,
                    ArityMinimum = common.Minimum,
                    ArityMaximum = common.Maximum,
                    AllowedValues = common.AllowedValues,
                    DefaultValue = common.DefaultValue,
                    AlternativeSources = common.AlternativeSources,
                    Status = common.Status
                };
            }
        }
    }

    private static string ParseOpenCliPath(string key)
    {
        var tokens = key.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !(token.StartsWith('<') && token.EndsWith('>')))
            .Where(token => !(token.StartsWith('{') && token.EndsWith('}')))
            .Where(token => !(token.StartsWith('[') && token.EndsWith(']')))
            .ToArray();
        if (tokens.Length == 0)
        {
            throw new NormalizationException("OPENCLI_COMMAND_KEY", "An OpenCLI command key must contain a command name.");
        }

        return tokens.Length == 1 ? "root" : "root / " + string.Join(" / ", tokens.Skip(1));
    }

    private static JsonObject RequireObject(JsonNode? node, string code)
    {
        return node as JsonObject ?? throw new NormalizationException(code, "The input schema has an object where an object was required.");
    }

    private static JsonNode? OptionalProperty(JsonObject objectNode, string property, string code)
    {
        if (!objectNode.ContainsKey(property))
        {
            return null;
        }

        return objectNode[property] ?? throw new NormalizationException(code, $"The '{property}' field must not be null.");
    }

    private static string RequiredString(JsonObject objectNode, string property, string code, NormalizationLimits? limits = null)
    {
        var node = objectNode[property];
        if (node is not JsonValue || node.GetValueKind() != JsonValueKind.String)
        {
            throw new NormalizationException(code, $"The required '{property}' field is missing or invalid.");
        }

        var value = node.GetValue<string>();
        if (value.Length == 0)
        {
            throw new NormalizationException(code, $"The required '{property}' field is missing or invalid.");
        }

        return limits is null ? value : BoundedString(value, limits);
    }

    private static string? OptionalString(JsonObject objectNode, string property, NormalizationLimits limits)
    {
        if (!objectNode.ContainsKey(property))
        {
            return null;
        }

        var node = objectNode[property] ?? throw new NormalizationException("INVALID_STRING", $"The '{property}' field must be a string.");
        if (node is not JsonValue || node.GetValueKind() != JsonValueKind.String)
        {
            throw new NormalizationException("INVALID_STRING", $"The '{property}' field must be a string.");
        }

        return BoundedString(node.GetValue<string>(), limits);
    }

    private static string BoundedString(string value, NormalizationLimits limits)
    {
        if (value.Length > limits.MaxStringLength)
        {
            throw new NormalizationException("STRING_TOO_LARGE", "A schema string exceeds the configured length limit.");
        }

        return value;
    }

    private static JsonNode BoundedString(JsonNode node, NormalizationLimits limits)
    {
        _ = BoundedString(node.GetValue<string>(), limits);
        return node;
    }

    private static string[] Strings(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return [];
        var array = node as JsonArray ?? throw new NormalizationException("COLLECTION_TYPE", "The aliases field must be an array.");
        if (array.Count > limits.MaxCollectionItems) throw new NormalizationException("COLLECTION_TOO_LARGE", "An alias collection exceeds the configured item limit.");
        return array.Select(item =>
        {
            if (item is not JsonValue || item.GetValueKind() != JsonValueKind.String)
            {
                throw new NormalizationException("INVALID_STRING", "The aliases field must contain only strings.");
            }

            return BoundedString(item.GetValue<string>(), limits);
        }).ToArray();
    }

    private static bool? OptionalBoolean(JsonObject objectNode, string property)
    {
        if (!objectNode.ContainsKey(property))
        {
            return null;
        }

        var node = objectNode[property] ?? throw new NormalizationException("INVALID_BOOLEAN", $"The '{property}' field must be a boolean.");
        if (node is not JsonValue || (node.GetValueKind() != JsonValueKind.True && node.GetValueKind() != JsonValueKind.False))
        {
            throw new NormalizationException("INVALID_BOOLEAN", $"The '{property}' field must be a boolean.");
        }

        return node.GetValue<bool>();
    }

    private static int? OptionalInt(JsonObject objectNode, string property)
    {
        if (!objectNode.ContainsKey(property)) return null;
        var node = objectNode[property] ?? throw new NormalizationException("INVALID_INTEGER", $"The '{property}' field must be an integer.");
        if (node is not JsonValue jsonValue || node.GetValueKind() != JsonValueKind.Number || !jsonValue.TryGetValue<int>(out var value))
        {
            throw new NormalizationException("INVALID_INTEGER", $"The '{property}' field must be an integer.");
        }

        return value;
    }

    private static JsonNode? OptionalScalar(JsonObject objectNode, string property, NormalizationLimits limits)
    {
        if (!objectNode.ContainsKey(property))
        {
            return null;
        }

        var node = objectNode[property];
        if (node is not JsonValue || node.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
        {
            throw new NormalizationException("OPENCLI_DEFAULT", "The default field must be a string, number, or boolean.");
        }

        if (node.GetValueKind() == JsonValueKind.String)
        {
            _ = BoundedString(node.GetValue<string>(), limits);
        }

        return node.DeepClone();
    }

    private static JsonNode[] ReadChoices(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null)
        {
            return [];
        }

        var array = node as JsonArray ?? throw new NormalizationException("OPENCLI_CHOICES", "The choices field must be an array.");
        if (array.Count > limits.MaxCollectionItems)
        {
            throw new NormalizationException("COLLECTION_TOO_LARGE", "A choice collection exceeds the configured item limit.");
        }

        return array.Select(item =>
            RequiredScalar(RequireObject(item, "OPENCLI_CHOICE"), "value", "OPENCLI_CHOICE", limits))
            .OrderBy(ScalarSortKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static CanonicalAlternativeSource[] ReadAlternativeSources(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null)
        {
            return [];
        }

        var array = node as JsonArray ?? throw new NormalizationException("OPENCLI_DEFAULT_SOURCES", "The alternativeSources field must be an array.");
        if (array.Count > limits.MaxCollectionItems)
        {
            throw new NormalizationException("COLLECTION_TOO_LARGE", "A default-source collection exceeds the configured item limit.");
        }

        return array.Select(item =>
        {
            var source = RequireObject(item, "OPENCLI_DEFAULT_SOURCE");
            var type = RequiredString(source, "type", "OPENCLI_DEFAULT_SOURCE", limits);
            if (type is not "$ENV" and not "$FILE")
            {
                throw new NormalizationException("OPENCLI_DEFAULT_SOURCE", "The alternative source type must be $ENV or $FILE.");
            }

            return new CanonicalAlternativeSource
            {
                Type = type,
                Property = RequiredString(source, "property", "OPENCLI_DEFAULT_SOURCE", limits)
            };
        }).ToArray();
    }

    private static JsonNode RequiredScalar(JsonObject objectNode, string property, string code, NormalizationLimits limits)
    {
        var node = objectNode[property];
        if (node is not JsonValue || node.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
        {
            throw new NormalizationException(code, $"The required '{property}' field must be a string, number, or boolean.");
        }

        if (node.GetValueKind() == JsonValueKind.String)
        {
            _ = BoundedString(node.GetValue<string>(), limits);
        }

        return node.DeepClone();
    }

    private static string ScalarSortKey(JsonNode node)
    {
        return node.GetValueKind() switch
        {
            JsonValueKind.String => "0:string:" + node.GetValue<string>(),
            JsonValueKind.Number => "1:number:" + node.ToJsonString(),
            JsonValueKind.False => "2:boolean:0",
            JsonValueKind.True => "2:boolean:1",
            _ => throw new NormalizationException("OPENCLI_CHOICE", "A choice value must be a scalar.")
        };
    }

    private static void CheckNode(int depth, NormalizationLimits limits, Counter counter)
    {
        if (depth > limits.MaxDepth) throw new NormalizationException("DEPTH_LIMIT", "The schema exceeds the configured nesting limit.");
        if (++counter.Value > limits.MaxNodes) throw new NormalizationException("NODE_LIMIT", "The schema exceeds the configured node limit.");
    }

    private sealed class Counter { public int Value; }
    private sealed record ParameterValues(string Name, string? Summary, string? Description, string? Type, bool? Required, int? Minimum, int? Maximum, JsonNode[] AllowedValues, JsonNode? DefaultValue, CanonicalAlternativeSource[] AlternativeSources, string? Status);
}
