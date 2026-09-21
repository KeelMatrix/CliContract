using System.Collections;
using System.Globalization;
using System.Numerics;
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
                return NormalizeOpenCli(document, bounded);
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
                        throw new NormalizationException("YAML_ALIASES_UNSUPPORTED", "YAML anchors and aliases are not accepted by the configured input policy.");
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
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => CanonicalizeNumber(Convert.ToString(value, CultureInfo.InvariantCulture)!),
            _ => BoundedString(JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty), limits)
        };
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
        var index = 0;
        var negative = raw.Length > 0 && raw[0] == '-';
        if (negative) index++;

        var integerStart = index;
        while (index < raw.Length && char.IsDigit(raw[index])) index++;
        if (index == integerStart)
        {
            throw new NormalizationException("OPENCLI_NUMBER", "A numeric value is invalid.");
        }

        var integerDigits = raw[integerStart..index];
        var fractionDigits = string.Empty;
        if (index < raw.Length && raw[index] == '.')
        {
            var fractionStart = ++index;
            while (index < raw.Length && char.IsDigit(raw[index])) index++;
            fractionDigits = raw[fractionStart..index];
            if (fractionDigits.Length == 0)
            {
                throw new NormalizationException("OPENCLI_NUMBER", "A numeric value is invalid.");
            }
        }

        var exponent = BigInteger.Zero;
        if (index < raw.Length && (raw[index] is 'e' or 'E'))
        {
            index++;
            var exponentNegative = index < raw.Length && raw[index] == '-';
            if (exponentNegative || (index < raw.Length && raw[index] == '+')) index++;
            var exponentStart = index;
            while (index < raw.Length && char.IsDigit(raw[index])) index++;
            if (index == exponentStart)
            {
                throw new NormalizationException("OPENCLI_NUMBER", "A numeric value is invalid.");
            }

            exponent = BigInteger.Parse(raw[exponentStart..index], CultureInfo.InvariantCulture);
            if (exponentNegative) exponent = -exponent;
        }

        if (index != raw.Length)
        {
            throw new NormalizationException("OPENCLI_NUMBER", "A numeric value is invalid.");
        }

        var digits = (integerDigits + fractionDigits).TrimStart('0');
        if (digits.Length == 0)
        {
            return JsonNode.Parse("0")!.AsValue();
        }

        var trailingZeroCount = digits.Length - digits.TrimEnd('0').Length;
        if (trailingZeroCount > 0)
        {
            digits = digits[..^trailingZeroCount];
        }

        var scale = exponent - fractionDigits.Length + trailingZeroCount;
        var decimalPoint = scale + digits.Length;
        var normalized = decimalPoint >= -28 && decimalPoint <= 29
            ? FormatPlainNumber(digits, decimalPoint, negative)
            : FormatScientificNumber(digits, decimalPoint, negative);

        return JsonNode.Parse(normalized)!.AsValue();
    }

    private static string FormatPlainNumber(string digits, BigInteger decimalPoint, bool negative)
    {
        var prefix = negative ? "-" : string.Empty;
        if (decimalPoint <= 0)
        {
            return prefix + "0." + new string('0', checked((int)-decimalPoint)) + digits;
        }

        if (decimalPoint >= digits.Length)
        {
            return prefix + digits + new string('0', checked((int)(decimalPoint - digits.Length)));
        }

        var point = checked((int)decimalPoint);
        return prefix + digits[..point] + "." + digits[point..];
    }

    private static string FormatScientificNumber(string digits, BigInteger decimalPoint, bool negative)
    {
        var prefix = negative ? "-" : string.Empty;
        var significand = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
        return prefix + significand + "e" + (decimalPoint - 1).ToString(CultureInfo.InvariantCulture);
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

        if (root.ContainsKey("commands"))
        {
            var commands = RequireCollection(root["commands"], "OPENCLI_COMMANDS", limits);
            foreach (var command in commands)
            {
                if (command.Value is null)
                {
                    throw new NormalizationException("OPENCLI_COMMAND", "An OpenCLI command must be an object.");
                }

                ValidateCommand(command.Value, limits);
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
        if (global.ContainsKey("flags"))
        {
            foreach (var flag in RequireArray(global["flags"], "OPENCLI_COLLECTION", limits))
            {
                ValidateParameter(flag, true, limits);
            }
        }
    }

    private static void ValidateCommand(JsonNode node, NormalizationLimits limits)
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
            foreach (var argument in RequireArray(command["args"], "OPENCLI_COLLECTION", limits))
            {
                ValidateParameter(argument, false, limits);
            }
        }

        if (command.ContainsKey("flags"))
        {
            foreach (var flag in RequireArray(command["flags"], "OPENCLI_COLLECTION", limits))
            {
                ValidateParameter(flag, true, limits);
            }
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

    private static void ValidateParameter(JsonNode? node, bool option, NormalizationLimits limits)
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
        ValidateChoices(parameter["choices"], limits);

        if (option)
        {
            if (parameter.ContainsKey("default")) ValidateScalar(parameter["default"], limits, "OPENCLI_DEFAULT");
            if (parameter.ContainsKey("alternativeSources")) ValidateAlternativeSources(parameter["alternativeSources"], limits);
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
        var minimum = ReadOptionalNonNegativeInt(parameter, "minItems", limits);
        var maximum = ReadOptionalNonNegativeInt(parameter, "maxItems", limits);
        if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
        {
            throw new NormalizationException("OPENCLI_ARITY", "minItems cannot be greater than maxItems.");
        }
    }

    private static void ValidateChoices(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return;
        foreach (var choice in RequireArray(node, "OPENCLI_CHOICES", limits))
        {
            ValidateObject(choice, "choice", limits, ["value", "description"], static (value, bounded) =>
            {
                if (!value.ContainsKey("value")) throw new NormalizationException("OPENCLI_CHOICE", "A choice value is required.");
                ValidateScalar(value["value"], bounded, "OPENCLI_CHOICE");
                ValidateOptionalString(value, "description", bounded, "OPENCLI_CHOICE");
            });
        }
    }

    private static void ValidateAlternativeSources(JsonNode? node, NormalizationLimits limits)
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
        }
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
        foreach (var property in new[] { "json", "toml", "yaml" })
        {
            ValidateOptionalString(config, property, limits, "OPENCLI_GLOBAL");
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
        _ = RequiredString(info, "binary", "OPENCLI_INFO");
        _ = RequiredString(info, "version", "OPENCLI_INFO");
        var commandsNode = OptionalProperty(root, "commands", "OPENCLI_COMMANDS");
        var commands = commandsNode is null ? [] : RequireObject(commandsNode, "OPENCLI_COMMANDS");
        var installNode = OptionalProperty(root, "install", "OPENCLI_INSTALL");
        var normalized = new List<CanonicalCommand>();
        foreach (var property in commands.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            normalized.Add(NormalizeOpenCliCommand(property.Key, RequireObject(property.Value, "OPENCLI_COMMAND"), limits));
        }

        EnsureUniqueCommandPaths(normalized);

        var globalNode = OptionalProperty(root, "global", "OPENCLI_GLOBAL");
        var global = globalNode is null ? null : RequireObject(globalNode, "OPENCLI_GLOBAL");
        var globalOptions = global is null
            ? Array.Empty<CanonicalOption>()
            : ReadOpenCliParameters(OptionalProperty(global, "flags", "OPENCLI_COLLECTION"), true, limits).Cast<CanonicalOption>().ToArray();
        var rootCommand = normalized.FirstOrDefault(c => c.Path == "root");
        var rootOptions = globalOptions
            .Concat(rootCommand?.Options ?? [])
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToArray();
        EnsureUniqueParameterNames(rootOptions, "option");

        return new CanonicalManifest
        {
            Adapter = "opencli",
            SourceVersion = version,
            Info = NormalizeInfo(info, installNode, limits),
            Root = new CanonicalCommand
            {
                Path = "root",
                Subcommands = normalized.Where(c => c.Path != "root").OrderBy(c => c.Path, StringComparer.Ordinal).ToArray(),
                Arguments = rootCommand?.Arguments ?? [],
                Options = rootOptions,
                Aliases = rootCommand?.Aliases ?? [],
                Summary = rootCommand?.Summary,
                Description = rootCommand?.Description
            }
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
                choices,
                option ? OptionalScalar(parameter, "default", limits) : null,
                alternativeSources,
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

        return CanonicalizeScalar(node);
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
