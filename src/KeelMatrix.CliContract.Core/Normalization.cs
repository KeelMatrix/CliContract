using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

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
    private static readonly string[] InfoTextProperties = ["title", "summary", "description", "binary", "version"];
    private static readonly string[] LicenseTextProperties = ["name", "spdxId", "url"];
    private static readonly string[] ContactTextProperties = ["name", "email", "url"];
    private static readonly string[] InstallTextProperties = ["name", "command", "url", "description"];
    private static readonly string[] ExitCodeTextProperties = ["status", "summary", "description"];
    private static readonly string[] ExitCodeStatuses = [
        "BAD_USER_INPUT_ERROR",
        "UNAUTHENTICATED_ERROR",
        "UNAUTHORIZED_ERROR",
        "CANCELED_ERROR",
        "INTERNAL_CLI_ERROR",
        "NOT_IMPLEMENTED_ERROR",
        "OK"];

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

            if (adapter.Equals("opencli", StringComparison.OrdinalIgnoreCase))
            {
                var document = ParseOpenCliDocument(input, bounded);
                ValidateOpenCliDocument(document, bounded);
                var manifest = NormalizeOpenCli(document, bounded);
                CanonicalInvariantValidator.Validate(manifest);
                return manifest;
            }

            throw new NormalizationException("UNSUPPORTED_ADAPTER", "The requested input adapter is not supported by this tool.");
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

        try
        {
            var json = ParseYaml(input, limits);
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

    private static JsonNode? ParseYaml(string input, NormalizationLimits limits)
    {
        var parser = new Parser(new StringReader(input));
        if (!parser.MoveNext() || parser.Current is not StreamStart)
        {
            throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        }

        if (!parser.MoveNext() || parser.Current is not DocumentStart)
        {
            throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        }

        if (!parser.MoveNext() || parser.Current is DocumentEnd)
        {
            throw new NormalizationException("MALFORMED_YAML", "The YAML document is empty.");
        }

        var root = ReadYamlNode(parser, 0, limits, new Counter());
        if (parser.Current is not DocumentEnd || !parser.MoveNext() || parser.Current is not StreamEnd)
        {
            throw new NormalizationException("MALFORMED_YAML", "Only one YAML document is accepted.");
        }

        return root;
    }

    private static JsonNode? ReadYamlNode(IParser parser, int depth, NormalizationLimits limits, Counter counter)
    {
        if (parser.Current is not ParsingEvent current)
        {
            throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        }

        if (current is AnchorAlias)
        {
            throw new NormalizationException("YAML_ALIASES_UNSUPPORTED", "YAML anchors and aliases are not accepted by the configured input policy.");
        }

        if (current is not NodeEvent node)
        {
            throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        }

        CheckYamlNode(node, depth, limits, counter);
        switch (node)
        {
            case Scalar scalar:
                var scalarValue = ConvertYamlScalar(scalar, limits);
                if (!parser.MoveNext()) throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
                return scalarValue;
            case MappingStart:
                return ReadYamlMapping(parser, depth, limits, counter);
            case SequenceStart:
                return ReadYamlSequence(parser, depth, limits, counter);
            default:
                throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        }
    }

    private static JsonObject ReadYamlMapping(IParser parser, int depth, NormalizationLimits limits, Counter counter)
    {
        var result = new JsonObject();
        if (!parser.MoveNext()) throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        while (parser.Current is not MappingEnd)
        {
            var keyNode = ReadYamlNode(parser, depth + 1, limits, counter);
            var key = keyNode is JsonValue keyValue && keyValue.GetValueKind() == JsonValueKind.String
                ? keyValue.GetValue<string>()
                : keyNode?.ToJsonString() ?? string.Empty;
            BoundedString(key, limits);
            if (result.ContainsKey(key)) throw new NormalizationException("DUPLICATE_YAML_KEY", "The YAML document contains a duplicate key.");

            var value = ReadYamlNode(parser, depth + 1, limits, counter);
            result[key] = value;
        }

        if (!parser.MoveNext()) throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        return result;
    }

    private static JsonArray ReadYamlSequence(IParser parser, int depth, NormalizationLimits limits, Counter counter)
    {
        var result = new JsonArray();
        if (!parser.MoveNext()) throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        while (parser.Current is not SequenceEnd)
        {
            if (result.Count >= limits.MaxCollectionItems) throw new NormalizationException("COLLECTION_TOO_LARGE", "A YAML sequence exceeds the configured item limit.");
            result.Add(ReadYamlNode(parser, depth + 1, limits, counter));
        }

        if (!parser.MoveNext()) throw new NormalizationException("MALFORMED_YAML", "The YAML document could not be parsed.");
        return result;
    }

    private static JsonNode? ConvertYamlScalar(Scalar scalar, NormalizationLimits limits)
    {
        var value = scalar.Value;
        var tag = scalar.Tag.IsEmpty ? string.Empty : scalar.Tag.Value;
        if (scalar.Tag.IsEmpty)
        {
            if (scalar.Style != ScalarStyle.Plain)
            {
                return BoundedString(JsonValue.Create(value), limits);
            }

            if (value is "~" or "null" or "Null" or "NULL") return null;
            if (value is "true" or "True" or "TRUE") return JsonValue.Create(true);
            if (value is "false" or "False" or "FALSE") return JsonValue.Create(false);

            return TryCanonicalizeYamlNumber(value, out var number)
                ? number
                : BoundedString(JsonValue.Create(value), limits);
        }

        return tag switch
        {
            "tag:yaml.org,2002:str" => BoundedString(JsonValue.Create(value), limits),
            "tag:yaml.org,2002:null" => null,
            "tag:yaml.org,2002:bool" => ConvertYamlBoolean(value),
            "tag:yaml.org,2002:int" => CanonicalizeYamlInteger(value),
            "tag:yaml.org,2002:float" => CanonicalizeYamlFloat(value),
            _ => throw new NormalizationException("YAML_TAG_UNSUPPORTED", "The YAML scalar tag is not supported.")
        };
    }

    private static JsonValue ConvertYamlBoolean(string value) => value switch
    {
        "true" or "True" or "TRUE" => JsonValue.Create(true),
        "false" or "False" or "FALSE" => JsonValue.Create(false),
        _ => throw new NormalizationException("YAML_BOOLEAN", "A YAML boolean value is invalid.")
    };

    private static bool TryCanonicalizeYamlNumber(string value, out JsonValue number)
    {
        try
        {
            if (LooksLikeYamlInteger(value))
            {
                number = CanonicalizeYamlInteger(value);
                return true;
            }

            if (LooksLikeYamlFloat(value))
            {
                number = CanonicalizeYamlFloat(value);
                return true;
            }
        }
        catch (NormalizationException)
        {
            // An untagged scalar that is not a supported finite number remains a string.
        }

        number = null!;
        return false;
    }

    private static JsonValue CanonicalizeYamlInteger(string raw)
    {
        var normalized = RemoveYamlSeparators(raw);
        var negative = normalized.StartsWith('-');
        var unsigned = normalized.TrimStart('-', '+');
        var numberBase = 10;
        if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            numberBase = 16;
            unsigned = unsigned[2..];
        }
        else if (unsigned.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            numberBase = 8;
            unsigned = unsigned[2..];
        }
        else if (unsigned.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            numberBase = 2;
            unsigned = unsigned[2..];
        }

        if (unsigned.Length == 0 || unsigned.Any(character => DigitValue(character) < 0 || DigitValue(character) >= numberBase))
        {
            throw new NormalizationException("OPENCLI_NUMBER", "A numeric value is invalid.");
        }

        var value = BigInteger.Zero;
        foreach (var character in unsigned)
        {
            value = value * numberBase + DigitValue(character);
        }

        if (negative) value = -value;
        var canonical = value.ToString(CultureInfo.InvariantCulture);
        if (!ExactNumber.TryParse(canonical, out var exact))
        {
            throw new NormalizationException("OPENCLI_NUMBER", "A numeric value is invalid.");
        }

        return JsonNode.Parse(exact.ToCanonicalString())!.AsValue();
    }

    private static JsonValue CanonicalizeYamlFloat(string raw)
    {
        if (!LooksLikeYamlFloat(raw))
        {
            throw new NormalizationException("OPENCLI_NUMBER", "A numeric value is invalid.");
        }

        var normalized = RemoveYamlSeparators(raw);
        var signLength = normalized[0] is '+' or '-' ? 1 : 0;
        var exponentOffset = normalized[signLength..].IndexOfAny(['e', 'E']);
        var exponentIndex = exponentOffset < 0 ? normalized.Length : signLength + exponentOffset;
        var sign = normalized[..signLength];
        var mantissa = normalized[signLength..exponentIndex];
        var exponent = normalized[exponentIndex..];
        if (mantissa.StartsWith('.'))
        {
            mantissa = "0" + mantissa;
        }
        else if (mantissa.EndsWith('.'))
        {
            mantissa += "0";
        }

        normalized = sign + mantissa + exponent;
        return CanonicalizeNumber(normalized.TrimStart('+'));
    }

    private static bool LooksLikeYamlInteger(string raw)
    {
        var value = RemoveYamlSeparators(raw);
        var unsigned = value.TrimStart('-', '+');
        if (unsigned.Length == 0) return false;
        if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return unsigned.Length > 2 && unsigned[2..].All(character => DigitValue(character) is >= 0 and < 16);
        if (unsigned.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) return unsigned.Length > 2 && unsigned[2..].All(character => DigitValue(character) is >= 0 and < 8);
        if (unsigned.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return unsigned.Length > 2 && unsigned[2..].All(character => DigitValue(character) is >= 0 and < 2);
        return unsigned.All(char.IsDigit);
    }

    private static bool LooksLikeYamlFloat(string raw)
    {
        var value = raw.Trim();
        var unsigned = value.TrimStart('-', '+');
        if (unsigned.Equals(".inf", StringComparison.OrdinalIgnoreCase) || unsigned.Equals(".nan", StringComparison.OrdinalIgnoreCase)) return false;

        var exponentIndex = unsigned.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex >= 0 ? unsigned[..exponentIndex] : unsigned;
        if (exponentIndex >= 0 && !IsYamlDecimalDigits(unsigned[(exponentIndex + 1)..].TrimStart('-', '+'))) return false;
        if (mantissa.Contains('.'))
        {
            var parts = mantissa.Split('.', 2);
            return (parts[0].Length > 0 && IsYamlDecimalDigits(parts[0]) || parts[1].Length > 0 && IsYamlDecimalDigits(parts[1])) &&
                (parts[0].Length == 0 || IsYamlDecimalDigits(parts[0])) && (parts[1].Length == 0 || IsYamlDecimalDigits(parts[1]));
        }

        return exponentIndex >= 0 && IsYamlDecimalDigits(mantissa);
    }

    private static bool IsYamlDecimalDigits(string value) =>
        value.Length > 0 && RemoveYamlSeparators(value).All(char.IsDigit);

    private static string RemoveYamlSeparators(string value)
    {
        if (value.Length == 0 || value[0] == '_' || value[^1] == '_' || value.Contains("__", StringComparison.Ordinal))
        {
            throw new NormalizationException("OPENCLI_NUMBER", "A numeric value is invalid.");
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '_' && (index == 0 || index == value.Length - 1 || !char.IsLetterOrDigit(value[index - 1]) || !char.IsLetterOrDigit(value[index + 1])))
            {
                throw new NormalizationException("OPENCLI_NUMBER", "A numeric value is invalid.");
            }
        }

        return value.Replace("_", string.Empty, StringComparison.Ordinal);
    }

    private static int DigitValue(char character) =>
        character is >= '0' and <= '9' ? character - '0' :
        character is >= 'a' and <= 'f' ? character - 'a' + 10 :
        character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;

    private static void CheckYamlNode(NodeEvent node, int depth, NormalizationLimits limits, Counter counter)
    {
        CheckNode(depth, limits, counter);
        if (!node.Anchor.IsEmpty)
        {
            throw new NormalizationException("YAML_ALIASES_UNSUPPORTED", "YAML anchors and aliases are not accepted by the configured input policy.");
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
        catch (JsonException exception) when (IsJsonDepthLimit(exception))
        {
            throw new NormalizationException("DEPTH_LIMIT", "The schema exceeds the configured nesting limit.");
        }
        catch (JsonException)
        {
            throw new NormalizationException("MALFORMED_JSON", "The JSON document could not be parsed.");
        }
    }

    private static bool IsJsonDepthLimit(JsonException exception) =>
        exception.Message.Contains("maximum depth", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("maximum configured depth", StringComparison.OrdinalIgnoreCase);

    private static JsonNode? ConvertJsonElement(JsonElement value, int depth, NormalizationLimits limits, Counter counter)
    {
        CheckNode(depth, limits, counter);
        return value.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(value, depth, limits, counter),
            JsonValueKind.Array => ConvertArray(value, depth, limits, counter),
            JsonValueKind.String => BoundedString(JsonValue.Create(value.GetString() ?? string.Empty), limits),
            JsonValueKind.Number => CanonicalizeNumber(value.GetRawText()),
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

    internal static JsonNode CanonicalizeScalar(JsonNode node)
    {
        return node.GetValueKind() switch
        {
            JsonValueKind.Number => CanonicalizeNumber(node.ToJsonString()),
            JsonValueKind.String or JsonValueKind.True or JsonValueKind.False => node.DeepClone(),
            _ => throw new NormalizationException("OPENCLI_SCALAR", "A scalar value must be a string, number, or boolean.")
        };
    }

    private static JsonValue CanonicalizeNumber(string raw)
    {
        if (!ExactNumber.TryParse(raw, out var number))
        {
            throw new NormalizationException("OPENCLI_NUMBER", "A numeric value is invalid.");
        }

        return JsonNode.Parse(number.ToCanonicalString())!.AsValue();
    }

    private static void ValidateOpenCliDocument(JsonNode document, NormalizationLimits limits)
    {
        RejectReferences(document, limits);
        var root = RequireObject(document, "OPENCLI_ROOT");
        EnsureOpenCliProperties(root, "root", "opencliVersion", "info", "install", "global", "commands");
        ValidateOptionalString(root, "opencliVersion", limits, "OPENCLI_VERSION");
        ValidateInfo(root["info"], limits);
        ValidateInstall(root["install"], limits);
        ValidateGlobal(root["global"], limits);
        var definedConfigFiles = DefinedConfigFiles(root["global"], limits);

        if (root.ContainsKey("commands"))
        {
            var commands = RequireCollection(root["commands"], "OPENCLI_COMMANDS", limits);
            foreach (var command in commands)
            {
                if (command.Value is null)
                {
                    throw new NormalizationException("OPENCLI_COMMAND", "An OpenCLI command must be an object.");
                }

                ValidateCommand(command.Value, limits, definedConfigFiles);
            }
        }
    }

    private static void ValidateInfo(JsonNode? node, NormalizationLimits limits)
    {
        var info = RequireObject(node, "OPENCLI_INFO");
        EnsureOpenCliProperties(info, "info", "title", "summary", "description", "license", "contact", "binary", "version");
        foreach (var property in InfoTextProperties)
        {
            ValidateOptionalString(info, property, limits, "OPENCLI_INFO");
        }

        if (info.ContainsKey("license"))
        {
            ValidateObject(info["license"], "license", limits, ["name", "spdxId", "url"], static (value, bounded) =>
            {
                RequireProperty(value, "name", "OPENCLI_INFO");
                foreach (var property in LicenseTextProperties)
                {
                    ValidateOptionalString(value, property, bounded, "OPENCLI_INFO");
                }
            });
        }

        if (info.ContainsKey("contact"))
        {
            ValidateObject(info["contact"], "contact", limits, ["name", "email", "url"], static (value, bounded) =>
            {
                if (!value.ContainsKey("name") && !value.ContainsKey("email") && !value.ContainsKey("url"))
                {
                    throw new NormalizationException("OPENCLI_INFO", "A contact must contain a name, email, or URL.");
                }

                foreach (var property in ContactTextProperties)
                {
                    ValidateOptionalString(value, property, bounded, "OPENCLI_INFO");
                }
            });
        }
    }

    private static void ValidateInstall(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return;
        var install = RequireArray(node, "OPENCLI_INSTALL", limits);
        foreach (var item in install)
        {
            ValidateObject(item, "install", limits, ["name", "command", "url", "description"], static (value, bounded) =>
            {
                RequireProperty(value, "name", "OPENCLI_INSTALL");
                if (!value.ContainsKey("command") && !value.ContainsKey("url"))
                {
                    throw new NormalizationException("OPENCLI_INSTALL", "An install method must contain a command or URL.");
                }

                foreach (var property in InstallTextProperties)
                {
                    ValidateOptionalString(value, property, bounded, "OPENCLI_INSTALL");
                }
            });
        }
    }

    private static void ValidateGlobal(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return;
        var global = RequireObject(node, "OPENCLI_GLOBAL");
        EnsureOpenCliProperties(global, "global", "exitCodes", "config", "flags");
        ValidateExitCodes(global["exitCodes"], limits);
        ValidateConfig(global["config"], limits);
        var definedConfigFiles = DefinedConfigFiles(global, limits);
        if (global.ContainsKey("flags"))
        {
            foreach (var flag in RequireArray(global["flags"], "OPENCLI_COLLECTION", limits))
            {
                ValidateParameter(flag, true, limits, definedConfigFiles);
            }
        }
    }

    private static void ValidateCommand(JsonNode node, NormalizationLimits limits, IReadOnlySet<string> definedConfigFiles)
    {
        var command = RequireObject(node, "OPENCLI_COMMAND");
        EnsureOpenCliProperties(command, "command", "summary", "description", "aliases", "args", "flags", "hidden", "kind", "exitCodes", "examples");
        ValidateOptionalString(command, "summary", limits, "OPENCLI_COMMAND");
        ValidateOptionalString(command, "description", limits, "OPENCLI_COMMAND");
        ValidateStringArray(command["aliases"], "OPENCLI_ALIASES", limits);
        ValidateExitCodes(command["exitCodes"], limits);
        ValidateOptionalBoolean(command, "hidden", "OPENCLI_COMMAND");
        if (command.TryGetPropertyValue("kind", out var kind) && kind is not null)
        {
            var value = RequiredStringValue(kind, "OPENCLI_COMMAND");
            if (value is not ("action" or "group"))
            {
                throw new NormalizationException("OPENCLI_COMMAND", "The command kind must be action or group.");
            }
        }

        if (command.ContainsKey("args"))
        {
            ValidateArguments(RequireArray(command["args"], "OPENCLI_COLLECTION", limits), limits);
        }

        if (command.ContainsKey("flags"))
        {
            foreach (var flag in RequireArray(command["flags"], "OPENCLI_COLLECTION", limits))
            {
                ValidateParameter(flag, true, limits, definedConfigFiles);
            }
        }

        var kindValue = OptionalString(command, "kind", limits);
        if (kindValue == "group" && ((command["args"] as JsonArray)?.Count > 0 || (command["flags"] as JsonArray)?.Count > 0))
        {
            throw new NormalizationException("OPENCLI_GROUP_COMMAND", "A group command cannot declare command-local arguments or flags.");
        }

        if (command.TryGetPropertyValue("examples", out var examples) && examples is not null)
        {
            foreach (var example in RequireArray(examples, "OPENCLI_COMMAND", limits))
            {
                ValidateObject(example, "example", limits, ["title", "content"], static (value, bounded) =>
                {
                    RequireProperty(value, "content", "OPENCLI_COMMAND");
                    ValidateOptionalString(value, "title", bounded, "OPENCLI_COMMAND");
                    ValidateOptionalString(value, "content", bounded, "OPENCLI_COMMAND");
                });
            }
        }
    }

    private static void ValidateParameter(JsonNode? node, bool option, NormalizationLimits limits, IReadOnlySet<string>? definedConfigFiles = null)
    {
        var parameter = RequireObject(node, "OPENCLI_PARAMETER");
        if (!option && (parameter.ContainsKey("default") || parameter.ContainsKey("alternativeSources")))
        {
            throw new NormalizationException("OPENCLI_ARGUMENT_FIELD", "OpenCLI arguments do not support default or alternativeSources fields.");
        }

        var allowed = option
            ? new[] { "name", "aliases", "type", "variadic", "minItems", "maxItems", "choices", "hint", "summary", "description", "required", "default", "alternativeSources", "hidden" }
            : new[] { "name", "type", "variadic", "minItems", "maxItems", "choices", "summary", "description", "required", "passthrough" };
        EnsureOpenCliProperties(parameter, option ? "flag" : "argument", allowed);
        _ = RequiredStringValue(parameter["name"], "OPENCLI_PARAMETER");
        ValidateType(parameter, limits, option);
        ValidateOptionalBoolean(parameter, "variadic", "OPENCLI_PARAMETER");
        ValidateOptionalBoolean(parameter, "required", "OPENCLI_PARAMETER");
        ValidateOptionalBoolean(parameter, "passthrough", "OPENCLI_PARAMETER");
        ValidateOptionalBoolean(parameter, "hidden", "OPENCLI_PARAMETER");
        ValidateOptionalString(parameter, "hint", limits, "OPENCLI_PARAMETER");
        ValidateOptionalString(parameter, "summary", limits, "OPENCLI_PARAMETER");
        ValidateOptionalString(parameter, "description", limits, "OPENCLI_PARAMETER");
        ValidateStringArray(parameter["aliases"], "OPENCLI_ALIASES", limits);
        ValidateArity(parameter, limits);
        var type = OptionalString(parameter, "type", limits);
        ValidateChoices(parameter["choices"], type, limits);

        if (option)
        {
            if (parameter.ContainsKey("default"))
            {
                ValidateScalar(parameter["default"], limits, "OPENCLI_DEFAULT");
                ValidateTypedDefault(parameter, type);
            }
            if (parameter.ContainsKey("alternativeSources")) ValidateAlternativeSources(parameter["alternativeSources"], limits, definedConfigFiles);
        }
    }

    private static void ValidateArguments(JsonArray arguments, NormalizationLimits limits)
    {
        var variadicCount = 0;
        var seenOptional = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = RequireObject(arguments[index], "OPENCLI_PARAMETER");
            ValidateParameter(argument, false, limits);
            var required = OptionalBoolean(argument, "required") ?? false;
            if (seenOptional && required)
            {
                throw new NormalizationException("OPENCLI_ARGUMENT_ORDER", "A required positional argument cannot follow an optional positional argument.");
            }
            seenOptional |= !required;
            var variadic = OptionalBoolean(argument, "variadic") ?? false;
            if (!variadic) continue;

            variadicCount++;
            if (variadicCount > 1 || index != arguments.Count - 1)
            {
                throw new NormalizationException("OPENCLI_VARIADIC", "Only one variadic positional argument is allowed and it must be last.");
            }
        }
    }

    private static void ValidateTypedDefault(JsonObject parameter, string? type)
    {
        var value = parameter["default"]!;
        var normalizedType = TypedDomain.NormalizeType(type);
        if (!TypedDomain.Accepts(normalizedType, value))
        {
            throw new NormalizationException("OPENCLI_DEFAULT", $"The default value is not representable by the declared {normalizedType} flag type.");
        }
    }

    private static void ValidateType(JsonObject parameter, NormalizationLimits limits, bool required)
    {
        if (!parameter.ContainsKey("type"))
        {
            if (required) throw new NormalizationException("OPENCLI_FLAG_TYPE", "The required 'type' field is missing or invalid.");
            return;
        }

        if (parameter["type"] is not JsonValue typeNode || typeNode.GetValueKind() != JsonValueKind.String)
        {
            throw new NormalizationException(required ? "OPENCLI_FLAG_TYPE" : "OPENCLI_TYPE", "The type field must be a string.");
        }

        var type = typeNode.GetValue<string>();
        if (type is not ("string" or "number" or "integer" or "boolean"))
        {
            throw new NormalizationException(required ? "OPENCLI_FLAG_TYPE" : "OPENCLI_TYPE", "The type field must be string, number, integer, or boolean.");
        }

        _ = limits;
    }

    private static void ValidateArity(JsonObject parameter, NormalizationLimits limits)
    {
        var variadic = OptionalBoolean(parameter, "variadic") ?? false;
        if (!variadic && (parameter.ContainsKey("minItems") || parameter.ContainsKey("maxItems")))
        {
            throw new NormalizationException("OPENCLI_ARITY", "minItems and maxItems apply only when variadic is true.");
        }

        var minimum = ReadOptionalNonNegativeInt(parameter, "minItems", limits);
        var maximum = ReadOptionalNonNegativeInt(parameter, "maxItems", limits);
        if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
        {
            throw new NormalizationException("OPENCLI_ARITY", "minItems cannot be greater than maxItems.");
        }
    }

    private static void ValidateChoices(JsonNode? node, string? type, NormalizationLimits limits)
    {
        if (node is null) return;
        foreach (var choice in RequireArray(node, "OPENCLI_CHOICES", limits))
        {
            ValidateObject(choice, "choice", limits, ["value", "description"], (value, bounded) =>
            {
                if (!value.ContainsKey("value")) throw new NormalizationException("OPENCLI_CHOICE", "A choice value is required.");
                ValidateScalar(value["value"], bounded, "OPENCLI_CHOICE");
                ValidateOptionalString(value, "description", bounded, "OPENCLI_CHOICE");
                if (!TypedDomain.Accepts(type, value["value"]!))
                {
                    throw new NormalizationException("OPENCLI_CHOICE", $"A choice value is not representable by the declared {TypedDomain.NormalizeType(type)} parameter type.");
                }
            });
        }
    }

    private static void ValidateAlternativeSources(JsonNode? node, NormalizationLimits limits, IReadOnlySet<string>? definedConfigFiles)
    {
        if (node is null) return;
        var sources = RequireArray(node, "OPENCLI_DEFAULT_SOURCES", limits);
        if (sources.Count == 0)
        {
            throw new NormalizationException("OPENCLI_DEFAULT_SOURCES", "alternativeSources must contain at least one source.");
        }

        foreach (var source in sources)
        {
            ValidateObject(source, "alternative source", limits, ["type", "property"], static (value, bounded) =>
            {
                var type = RequiredStringValue(value["type"], "OPENCLI_DEFAULT_SOURCE");
                if (type is not ("$ENV" or "$FILE"))
                {
                    throw new NormalizationException("OPENCLI_DEFAULT_SOURCE", "The alternative source type must be $ENV or $FILE.");
                }

                _ = RequiredStringValue(value["property"], "OPENCLI_DEFAULT_SOURCE");
                ValidateOptionalString(value, "property", bounded, "OPENCLI_DEFAULT_SOURCE");
            });

            var sourceType = RequiredStringValue(RequireObject(source, "OPENCLI_DEFAULT_SOURCE")["type"], "OPENCLI_DEFAULT_SOURCE");
            if (sourceType == "$FILE" && (definedConfigFiles is null || definedConfigFiles.Count == 0))
            {
                throw new NormalizationException("OPENCLI_DEFAULT_SOURCE", "A $FILE alternative source requires a global.config file source.");
            }
        }
    }

    private static HashSet<string> DefinedConfigFiles(JsonNode? globalNode, NormalizationLimits limits)
    {
        if (globalNode is not JsonObject global || !global.ContainsKey("config")) return new HashSet<string>(StringComparer.Ordinal);
        var config = RequireObject(global["config"], "OPENCLI_GLOBAL");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in new[] { "json", "toml", "yaml" })
        {
            if (OptionalString(config, property, limits) is { Length: > 0 }) result.Add(property);
        }

        return result;
    }

    private static void ValidateConfig(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return;
        var config = RequireObject(node, "OPENCLI_GLOBAL");
        if (config.Count == 0)
        {
            throw new NormalizationException("OPENCLI_GLOBAL", "The global config object must not be empty.");
        }

        EnsureOpenCliProperties(config, "config", "json", "toml", "yaml");
        var hasFileSource = false;
        foreach (var property in new[] { "json", "toml", "yaml" })
        {
            if (config.ContainsKey(property))
            {
                var path = RequiredString(config, property, "OPENCLI_GLOBAL", limits);
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new NormalizationException("OPENCLI_GLOBAL", "A global config file path must be nonempty.");
                }

                hasFileSource = true;
            }
        }

        if (!hasFileSource)
        {
            throw new NormalizationException("OPENCLI_GLOBAL", "The global config object must define at least one file source.");
        }
    }

    private static void ValidateExitCodes(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return;
        foreach (var item in RequireArray(node, "OPENCLI_GLOBAL", limits))
        {
            ValidateObject(item, "exit code", limits, ["code", "status", "summary", "description"], static (value, bounded) =>
            {
                RequireProperty(value, "code", "OPENCLI_EXIT_CODE");
                RequireProperty(value, "status", "OPENCLI_EXIT_CODE");
                RequireProperty(value, "summary", "OPENCLI_EXIT_CODE");
                if (value["code"] is not JsonValue code || code.GetValueKind() != JsonValueKind.Number || !TryGetInt(code, out _))
                {
                    throw new NormalizationException("OPENCLI_EXIT_CODE", "An exit code must be an integer.");
                }

                if (value["status"] is not JsonValue status || status.GetValueKind() != JsonValueKind.String ||
                    !ExitCodeStatuses.Contains(status.GetValue<string>(), StringComparer.Ordinal))
                {
                    throw new NormalizationException("OPENCLI_EXIT_CODE", "An exit code status is invalid.");
                }

                foreach (var property in ExitCodeTextProperties)
                {
                    ValidateOptionalString(value, property, bounded, "OPENCLI_GLOBAL");
                }
            });
        }
    }

    private static void RejectReferences(JsonNode node, NormalizationLimits limits)
    {
        var counter = new Counter();
        RejectReferences(node, 0, limits, counter);
    }

    private static void RejectReferences(JsonNode? node, int depth, NormalizationLimits limits, Counter counter)
    {
        if (node is null) return;
        CheckNode(depth, limits, counter);
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key.StartsWith("x-", StringComparison.Ordinal)) continue;
                if (property.Key is "$ref" or "$dynamicRef" or "$recursiveRef" or "include")
                {
                    throw new NormalizationException("OPENCLI_REMOTE_REFERENCE", "Remote schema references and includes are not supported.");
                }

                RejectReferences(property.Value, depth + 1, limits, counter);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                RejectReferences(item, depth + 1, limits, counter);
            }
        }
    }

    private static void EnsureOpenCliProperties(JsonObject value, string subject, params string[] allowed)
    {
        foreach (var property in value)
        {
            if (property.Key.StartsWith("x-", StringComparison.Ordinal)) continue;
            if (!allowed.Contains(property.Key, StringComparer.Ordinal))
            {
                throw new NormalizationException("OPENCLI_UNKNOWN_FIELD", $"The OpenCLI {subject} contains an unsupported field.");
            }
        }
    }

    private static void ValidateObject(JsonNode? node, string subject, NormalizationLimits limits, IReadOnlyCollection<string> allowed, Action<JsonObject, NormalizationLimits> validate)
    {
        var value = RequireObject(node, "OPENCLI_STRUCTURE");
        EnsureOpenCliProperties(value, subject, [.. allowed]);
        validate(value, limits);
    }

    private static JsonObject RequireObject(JsonNode? node, string code)
    {
        return node as JsonObject ?? throw new NormalizationException(code, "The input schema has an object where an object was required.");
    }

    private static JsonArray RequireArray(JsonNode? node, string code, NormalizationLimits limits)
    {
        var array = node as JsonArray ?? throw new NormalizationException(code, "The input schema has an array where an array was required.");
        if (array.Count > limits.MaxCollectionItems)
        {
            throw new NormalizationException("COLLECTION_TOO_LARGE", "An OpenCLI collection exceeds the configured item limit.");
        }

        return array;
    }

    private static JsonObject RequireCollection(JsonNode? node, string code, NormalizationLimits limits)
    {
        var value = RequireObject(node, code);
        if (value.Count > limits.MaxCollectionItems)
        {
            throw new NormalizationException("COLLECTION_TOO_LARGE", "An OpenCLI collection exceeds the configured item limit.");
        }

        return value;
    }

    private static void ValidateOptionalString(JsonObject value, string property, NormalizationLimits limits, string code)
    {
        if (!value.ContainsKey(property)) return;
        if (value[property] is not JsonValue node || node.GetValueKind() != JsonValueKind.String)
        {
            throw new NormalizationException(code, "A recognized OpenCLI string field is invalid.");
        }

        _ = BoundedString(node.GetValue<string>(), limits);
    }

    private static void RequireProperty(JsonObject value, string property, string code)
    {
        if (!value.ContainsKey(property))
        {
            throw new NormalizationException(code, $"The required '{property}' field is missing.");
        }
    }

    private static void ValidateOptionalBoolean(JsonObject value, string property, string code)
    {
        if (!value.ContainsKey(property)) return;
        if (value[property] is not JsonValue node || node.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new NormalizationException(code, "A recognized OpenCLI boolean field is invalid.");
        }
    }

    private static void ValidateStringArray(JsonNode? node, string code, NormalizationLimits limits)
    {
        if (node is null) return;
        foreach (var item in RequireArray(node, code, limits))
        {
            if (item is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            {
                throw new NormalizationException(code, "A recognized OpenCLI string collection is invalid.");
            }

            _ = BoundedString(value.GetValue<string>(), limits);
        }
    }

    private static void ValidateScalar(JsonNode? node, NormalizationLimits limits, string code)
    {
        if (node is not JsonValue value || value.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
        {
            throw new NormalizationException(code, "A recognized OpenCLI scalar field is invalid.");
        }

        if (value.GetValueKind() == JsonValueKind.String)
        {
            _ = BoundedString(value.GetValue<string>(), limits);
        }
    }

    private static int? ReadOptionalNonNegativeInt(JsonObject value, string property, NormalizationLimits limits)
    {
        if (!value.ContainsKey(property)) return null;
        if (value[property] is not JsonValue node || node.GetValueKind() != JsonValueKind.Number || !TryGetInt(node, out var result) || result < 0)
        {
            throw new NormalizationException("OPENCLI_ARITY", "minItems and maxItems must be non-negative integers.");
        }

        _ = limits;
        return result;
    }

    private static string RequiredStringValue(JsonNode? node, string code)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String || string.IsNullOrEmpty(value.GetValue<string>()))
        {
            throw new NormalizationException(code, "A required OpenCLI string field is missing or invalid.");
        }

        return value.GetValue<string>();
    }

    private static bool TryGetInt(JsonValue value, out int result)
    {
        if (value.TryGetValue<int>(out result)) return true;
        if (value.TryGetValue<decimal>(out var decimalValue) && decimal.Truncate(decimalValue) == decimalValue && decimalValue >= int.MinValue && decimalValue <= int.MaxValue)
        {
            result = (int)decimalValue;
            return true;
        }

        result = default;
        return false;
    }

    private static int RequiredInt(JsonObject value, string property, string code)
    {
        if (value[property] is JsonValue node && node.GetValueKind() == JsonValueKind.Number && TryGetInt(node, out var result))
        {
            return result;
        }

        throw new NormalizationException(code, $"The required '{property}' field must be an integer.");
    }

    private static CanonicalManifest NormalizeOpenCli(JsonNode document, NormalizationLimits limits)
    {
        var root = RequireObject(document, "OPENCLI_ROOT");
        var version = RequiredString(root, "opencliVersion", "OPENCLI_VERSION");
        if (!string.Equals(version, OpenCliVersion, StringComparison.Ordinal))
        {
            throw new NormalizationException("UNSUPPORTED_OPENCLI_VERSION", "Only OpenCLI 1.0.0-alpha.14 is accepted by this tool.");
        }

        var info = RequireObject(root["info"], "OPENCLI_INFO");
        _ = RequiredString(info, "title", "OPENCLI_INFO");
        var binary = RequiredString(info, "binary", "OPENCLI_INFO", limits);
        _ = RequiredString(info, "version", "OPENCLI_INFO");
        var commandsNode = OptionalProperty(root, "commands", "OPENCLI_COMMANDS");
        var commands = commandsNode is null ? [] : RequireObject(commandsNode, "OPENCLI_COMMANDS");
        var installNode = OptionalProperty(root, "install", "OPENCLI_INSTALL");
        var normalized = new List<CanonicalCommand>();
        foreach (var property in commands.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            normalized.Add(NormalizeOpenCliCommand(property.Key, RequireObject(property.Value, "OPENCLI_COMMAND"), binary, limits));
        }

        EnsureUniqueCommandPaths(normalized);

        var canonicalCommands = MaterializeCommandTrie(normalized);

        var globalNode = OptionalProperty(root, "global", "OPENCLI_GLOBAL");
        var global = globalNode is null ? null : RequireObject(globalNode, "OPENCLI_GLOBAL");
        var globalOptions = global is null
            ? Array.Empty<CanonicalOption>()
            : ReadOpenCliParameters(OptionalProperty(global, "flags", "OPENCLI_COLLECTION"), true, limits).Cast<CanonicalOption>().ToArray();
        var rootCommand = canonicalCommands.FirstOrDefault(c => c.Path == "root");
        var rootOptions = rootCommand?.Options ?? [];
        EnsureUniqueParameterNames(globalOptions, "global option");
        EnsureUniqueParameterNames(rootOptions, "option");

        return new CanonicalManifest
        {
            Adapter = "opencli",
            SourceVersion = version,
            Info = NormalizeInfo(info, installNode, limits),
            GlobalExitCodes = global is null ? [] : ReadExitCodes(OptionalProperty(global, "exitCodes", "OPENCLI_GLOBAL"), limits),
            Root = new CanonicalCommand
            {
                Path = "root",
                Kind = rootCommand?.Kind ?? "group",
                Subcommands = canonicalCommands.Where(c => c.Path != "root").OrderBy(c => c.Path, StringComparer.Ordinal).ToArray(),
                Arguments = rootCommand?.Arguments ?? [],
                Options = rootOptions,
                Aliases = rootCommand?.Aliases ?? [],
                Summary = rootCommand?.Summary,
                Description = rootCommand?.Description,
                Hidden = rootCommand?.Hidden ?? false,
                ExitCodes = rootCommand?.ExitCodes ?? [],
                Examples = rootCommand?.Examples ?? []
            },
            GlobalOptions = globalOptions.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray(),
            GlobalConfig = global is not null && global.ContainsKey("config")
                ? NormalizeGlobalConfig(RequireObject(global["config"], "OPENCLI_GLOBAL"), limits)
                : null
        };
    }

    private static CanonicalCommand[] MaterializeCommandTrie(IReadOnlyList<CanonicalCommand> explicitCommands)
    {
        var byPath = explicitCommands.ToDictionary(command => command.Path, StringComparer.Ordinal);
        foreach (var command in explicitCommands)
        {
            var segments = command.Path.Split(" / ", StringSplitOptions.None);
            for (var length = 1; length < segments.Length; length++)
            {
                var path = string.Join(" / ", segments[..length]);
                if (!byPath.ContainsKey(path))
                {
                    byPath[path] = new CanonicalCommand
                    {
                        Path = path,
                        Kind = "group"
                    };
                }
            }
        }

        if (!byPath.ContainsKey("root"))
        {
            byPath["root"] = new CanonicalCommand { Path = "root", Kind = "group" };
        }

        return byPath.Values.OrderBy(command => command.Path, StringComparer.Ordinal).ToArray();
    }

    private static CanonicalGlobalConfig NormalizeGlobalConfig(JsonObject config, NormalizationLimits limits)
    {
        return new CanonicalGlobalConfig
        {
            FileSources = config
                .Where(property => property.Key is "json" or "toml" or "yaml")
                .OrderBy(property => property.Key, StringComparer.Ordinal)
                .Select(property => new CanonicalFileSource
                {
                    Format = property.Key,
                    Path = BoundedString(property.Value?.GetValue<string>() ?? string.Empty, limits)
                })
                .ToArray()
        };
    }

    private static CanonicalInfo NormalizeInfo(JsonObject info, JsonNode? installNode, NormalizationLimits limits)
    {
        return new CanonicalInfo
        {
            Title = OptionalString(info, "title", limits),
            Summary = OptionalString(info, "summary", limits),
            Description = OptionalString(info, "description", limits),
            Binary = OptionalString(info, "binary", limits),
            Version = OptionalString(info, "version", limits),
            License = info.ContainsKey("license")
                ? NormalizeLicense(RequireObject(info["license"], "OPENCLI_INFO"), limits)
                : null,
            Contact = info.ContainsKey("contact")
                ? NormalizeContact(RequireObject(info["contact"], "OPENCLI_INFO"), limits)
                : null,
            Install = installNode is null
                ? []
                : RequireArray(installNode, "OPENCLI_INSTALL", limits)
                    .Select(item => NormalizeInstall(RequireObject(item, "OPENCLI_INSTALL"), limits))
                    .ToArray()
        };
    }

    private static CanonicalLicense NormalizeLicense(JsonObject value, NormalizationLimits limits)
    {
        return new CanonicalLicense
        {
            Name = RequiredString(value, "name", "OPENCLI_INFO", limits),
            SpdxId = OptionalString(value, "spdxId", limits),
            Url = OptionalString(value, "url", limits)
        };
    }

    private static CanonicalContact NormalizeContact(JsonObject value, NormalizationLimits limits)
    {
        return new CanonicalContact
        {
            Name = OptionalString(value, "name", limits),
            Email = OptionalString(value, "email", limits),
            Url = OptionalString(value, "url", limits)
        };
    }

    private static CanonicalInstall NormalizeInstall(JsonObject value, NormalizationLimits limits)
    {
        return new CanonicalInstall
        {
            Name = RequiredString(value, "name", "OPENCLI_INSTALL", limits),
            Command = OptionalString(value, "command", limits),
            Url = OptionalString(value, "url", limits),
            Description = OptionalString(value, "description", limits)
        };
    }

    private static CanonicalCommand NormalizeOpenCliCommand(string key, JsonObject value, string binary, NormalizationLimits limits)
    {
        var path = ParseOpenCliPath(key, binary);
        var aliases = Strings(OptionalProperty(value, "aliases", "COLLECTION_TYPE"), limits).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var arguments = ReadOpenCliParameters(OptionalProperty(value, "args", "OPENCLI_COLLECTION"), false, limits).Cast<CanonicalArgument>().ToArray();
        var options = ReadOpenCliParameters(OptionalProperty(value, "flags", "OPENCLI_COLLECTION"), true, limits).Cast<CanonicalOption>().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        return new CanonicalCommand
        {
            Path = path,
            Kind = OptionalString(value, "kind", limits) ?? "action",
            Aliases = aliases,
            Summary = OptionalString(value, "summary", limits),
            Description = OptionalString(value, "description", limits),
            Status = null,
            Hidden = OptionalBoolean(value, "hidden") ?? false,
            ExitCodes = ReadExitCodes(OptionalProperty(value, "exitCodes", "OPENCLI_COMMAND"), limits),
            Examples = ReadExamples(OptionalProperty(value, "examples", "OPENCLI_COMMAND"), limits),
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

        var names = new HashSet<string>(StringComparer.Ordinal);
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
            var passthrough = !option && (OptionalBoolean(parameter, "passthrough") ?? false);
            var minimum = variadic ? OptionalInt(parameter, "minItems") ?? (required == true ? 1 : 0) : required == true ? 1 : 0;
            var maximum = variadic ? OptionalInt(parameter, "maxItems") : 1;
            var choices = ReadChoices(OptionalProperty(parameter, "choices", "OPENCLI_CHOICES"), limits);
            var type = option
                ? RequiredString(parameter, "type", "OPENCLI_FLAG_TYPE", limits)
                : OptionalString(parameter, "type", limits);
            var alternativeSources = option
                ? ReadAlternativeSources(OptionalProperty(parameter, "alternativeSources", "OPENCLI_DEFAULT_SOURCES"), limits)
                : [];
            var normalizedName = option ? "--" + name.TrimStart('-') : name;
            if (!names.Add(normalizedName))
            {
                throw new NormalizationException("OPENCLI_DUPLICATE_PARAMETER", "An OpenCLI parameter collection contains duplicate normalized names.");
            }

            var common = new ParameterValues(
                name,
                OptionalString(parameter, "summary", limits),
                OptionalString(parameter, "description", limits),
                type,
                required,
                minimum,
                maximum,
                variadic,
                OptionalString(parameter, "hint", limits),
                OptionalBoolean(parameter, "hidden") ?? false,
                choices,
                option ? ReadTypedDefault(parameter, limits) : null,
                alternativeSources,
                passthrough,
                null);
            if (option)
            {
                yield return new CanonicalOption
                {
                    Name = normalizedName,
                    Aliases = Strings(parameter["aliases"], limits).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    Summary = common.Summary,
                    Description = common.Description,
                    Type = common.Type,
                    Required = common.Required,
                    ArityMinimum = common.Minimum,
                    ArityMaximum = common.Maximum,
                    Variadic = common.Variadic,
                    Hint = common.Hint,
                    Hidden = common.Hidden,
                    AllowedValues = common.AllowedValues,
                    Choices = common.Choices,
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
                    Variadic = common.Variadic,
                    Hint = common.Hint,
                    Hidden = common.Hidden,
                    AllowedValues = common.AllowedValues,
                    Choices = common.Choices,
                    DefaultValue = common.DefaultValue,
                    AlternativeSources = common.AlternativeSources,
                    Passthrough = common.Passthrough,
                    Status = common.Status
                };
            }
        }
    }

    private static void EnsureUniqueParameterNames<TParameter>(IEnumerable<TParameter> parameters, string kind)
        where TParameter : CanonicalParameter
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (!names.Add(parameter.Name))
            {
                throw new NormalizationException("OPENCLI_DUPLICATE_PARAMETER", $"An OpenCLI {kind} collection contains duplicate normalized names.");
            }
        }
    }

    private static string ParseOpenCliPath(string key, string binary)
    {
        var commandEnd = -1;
        for (var index = 0; index + 1 < key.Length; index++)
        {
            if (!char.IsWhiteSpace(key[index]) || key[index] is '\r' or '\n') continue;
            if (!char.IsAsciiLetter(key[index + 1]))
            {
                commandEnd = index;
                break;
            }
        }

        var commandLine = commandEnd < 0 ? key : key[..commandEnd];
        var tokens = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new NormalizationException("OPENCLI_COMMAND_KEY", "An OpenCLI command key must contain a command name.");
        }

        if (!string.Equals(tokens[0], binary, StringComparison.Ordinal))
        {
            throw new NormalizationException("OPENCLI_COMMAND_KEY", "An OpenCLI command key must begin with info.binary.");
        }

        return tokens.Length == 1 ? "root" : "root / " + string.Join(" / ", tokens.Skip(1));
    }

    private static JsonNode? ReadTypedDefault(JsonObject parameter, NormalizationLimits limits)
    {
        var value = OptionalScalar(parameter, "default", limits);
        if (value is null) return null;

        return OptionalString(parameter, "type", limits) switch
        {
            "string" => value.GetValueKind() switch
            {
                JsonValueKind.String => value.DeepClone(),
                JsonValueKind.Number => JsonValue.Create(value.ToJsonString()),
                JsonValueKind.True or JsonValueKind.False => JsonValue.Create(value.GetValue<bool>() ? "true" : "false"),
                _ => value.DeepClone()
            },
            _ => value.DeepClone()
        };
    }

    private static void EnsureUniqueCommandPaths(IEnumerable<CanonicalCommand> commands)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (!paths.Add(command.Path))
            {
                throw new NormalizationException(
                    "OPENCLI_DUPLICATE_COMMAND_PATH",
                    "OpenCLI command keys normalize to duplicate canonical command paths.");
            }
        }
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
        if (node is not JsonValue jsonValue || node.GetValueKind() != JsonValueKind.Number || !TryGetInt(jsonValue, out var value))
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

        return CanonicalizeScalar(node);
    }

    private static CanonicalChoice[] ReadChoices(JsonNode? node, NormalizationLimits limits)
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
            {
                var choice = RequireObject(item, "OPENCLI_CHOICE");
                return new CanonicalChoice
                {
                    Value = RequiredScalar(choice, "value", "OPENCLI_CHOICE", limits),
                    Description = OptionalString(choice, "description", limits)
                };
            })
            .OrderBy(choice => ScalarSortKey(choice.Value), StringComparer.Ordinal)
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

        return CanonicalizeScalar(node);
    }

    internal static string ScalarSortKey(JsonNode node)
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
    private static CanonicalExitCode[] ReadExitCodes(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return [];
        var array = RequireArray(node, "OPENCLI_GLOBAL", limits);
        var seen = new HashSet<int>();
        return array.Select(item =>
        {
            var value = RequireObject(item, "OPENCLI_EXIT_CODE");
            var code = RequiredInt(value, "code", "OPENCLI_EXIT_CODE");
            if (!seen.Add(code)) throw new NormalizationException("OPENCLI_EXIT_CODE", "Exit code values must be unique.");
            return new CanonicalExitCode
            {
                Code = code,
                Status = RequiredString(value, "status", "OPENCLI_EXIT_CODE", limits),
                Summary = RequiredString(value, "summary", "OPENCLI_EXIT_CODE", limits),
                Description = OptionalString(value, "description", limits)
            };
        }).OrderBy(exitCode => exitCode.Code).ToArray();
    }

    private static CanonicalExample[] ReadExamples(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return [];
        return RequireArray(node, "OPENCLI_COMMAND", limits)
            .Select(item =>
            {
                var value = RequireObject(item, "OPENCLI_COMMAND");
                return new CanonicalExample
                {
                    Title = OptionalString(value, "title", limits),
                    Content = RequiredString(value, "content", "OPENCLI_COMMAND", limits)
                };
            })
            .ToArray();
    }

    private sealed record ParameterValues(string Name, string? Summary, string? Description, string? Type, bool? Required, int? Minimum, int? Maximum, bool Variadic, string? Hint, bool Hidden, CanonicalChoice[] Choices, JsonNode? DefaultValue, CanonicalAlternativeSource[] AlternativeSources, bool Passthrough, string? Status)
    {
        public JsonNode[] AllowedValues => Choices.Select(choice => choice.Value).ToArray();
    }
}
