using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var bounded = limits ?? new NormalizationLimits();
        if (input.Length > bounded.MaxInputBytes)
        {
            throw new NormalizationException("INPUT_TOO_LARGE", "The input description exceeds the configured size limit.");
        }

        return adapter.ToLowerInvariant() switch
        {
            "opencli" => NormalizeOpenCli(ParseOpenCliDocument(input, bounded), bounded),
            "dotnet" => NormalizeDotnet(ParseJson(input, bounded, "DOTNET_JSON"), bounded),
            _ => throw new NormalizationException("UNSUPPORTED_ADAPTER", "The requested input adapter is not supported by this probe.")
        };
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

        if (System.Text.RegularExpressions.Regex.IsMatch(input, @"(?m)(^|\s)[&*][A-Za-z0-9_-]+"))
        {
            throw new NormalizationException("YAML_ALIASES_UNSUPPORTED", "YAML anchors and aliases are not accepted by the bounded probe.");
        }

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
        catch (Exception)
        {
            throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        }
    }

    private static JsonNode ParseJson(string input, NormalizationLimits limits, string code)
    {
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                MaxDepth = limits.MaxDepth,
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
        var result = new JsonObject();
        foreach (var property in value.EnumerateObject())
        {
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
        var commands = root["commands"] is null ? [] : RequireObject(root["commands"], "OPENCLI_COMMANDS");
        var normalized = new List<CanonicalCommand>();
        foreach (var property in commands.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            normalized.Add(NormalizeOpenCliCommand(property.Key, RequireObject(property.Value, "OPENCLI_COMMAND"), limits));
        }

        return new CanonicalManifest
        {
            Adapter = "opencli",
            SourceVersion = version,
            Root = new CanonicalCommand
            {
                Path = "root",
                Subcommands = normalized.Where(c => c.Path != "root").OrderBy(c => c.Path, StringComparer.Ordinal).ToArray(),
                Arguments = normalized.Where(c => c.Path == "root").SelectMany(c => c.Arguments).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray(),
                Options = normalized.Where(c => c.Path == "root").SelectMany(c => c.Options).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray(),
                Summary = normalized.FirstOrDefault(c => c.Path == "root")?.Summary,
                Description = normalized.FirstOrDefault(c => c.Path == "root")?.Description
            }
        };
    }

    private static CanonicalCommand NormalizeOpenCliCommand(string key, JsonObject value, NormalizationLimits limits)
    {
        var path = ParseOpenCliPath(key);
        var aliases = Strings(value["aliases"], limits).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var arguments = ReadOpenCliParameters(value["args"], false, limits).Cast<CanonicalArgument>().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var options = ReadOpenCliParameters(value["flags"], true, limits).Cast<CanonicalOption>().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
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
            var required = OptionalBoolean(parameter, "required");
            var variadic = OptionalBoolean(parameter, "variadic") ?? false;
            var minimum = variadic ? OptionalInt(parameter, "minItems") ?? (required == true ? 1 : 0) : required == true ? 1 : 0;
            var maximum = variadic ? OptionalInt(parameter, "maxItems") : 1;
            var choices = parameter["choices"] is JsonArray choicesArray
                ? choicesArray.Select(choice => choice is JsonObject obj ? RequiredString(obj, "value", "OPENCLI_CHOICE") : BoundedString(choice?.ToString() ?? string.Empty, limits)).OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : [];
            var type = parameter["type"]?.ToString();
            var common = new ParameterValues(
                name,
                OptionalString(parameter, "summary", limits),
                OptionalString(parameter, "description", limits),
                type,
                required,
                minimum,
                maximum,
                choices,
                parameter["default"]?.DeepClone(),
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

    private static string RequiredString(JsonObject objectNode, string property, string code)
    {
        return objectNode[property]?.GetValue<string>() is { } value && value.Length > 0
            ? value
            : throw new NormalizationException(code, $"The required '{property}' field is missing or invalid.");
    }

    private static string? OptionalString(JsonObject objectNode, string property, NormalizationLimits limits)
    {
        return objectNode[property] is null ? null : BoundedString(objectNode[property]!.GetValue<string>(), limits);
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
        return array.Select(item => BoundedString(item?.GetValue<string>() ?? string.Empty, limits)).ToArray();
    }

    private static bool? OptionalBoolean(JsonObject objectNode, string property)
    {
        return objectNode[property]?.GetValue<bool>();
    }

    private static int? OptionalInt(JsonObject objectNode, string property)
    {
        if (objectNode[property] is null || objectNode[property]!.GetValueKind() == JsonValueKind.Null) return null;
        return objectNode[property]!.GetValue<int>();
    }

    private static void CheckNode(int depth, NormalizationLimits limits, Counter counter)
    {
        if (depth > limits.MaxDepth) throw new NormalizationException("DEPTH_LIMIT", "The schema exceeds the configured nesting limit.");
        if (++counter.Value > limits.MaxNodes) throw new NormalizationException("NODE_LIMIT", "The schema exceeds the configured node limit.");
    }

    private sealed class Counter { public int Value; }
    private sealed record ParameterValues(string Name, string? Summary, string? Description, string? Type, bool? Required, int? Minimum, int? Maximum, string[] AllowedValues, JsonNode? DefaultValue, string? Status);
}
