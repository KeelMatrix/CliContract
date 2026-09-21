using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

public static class CanonicalManifestReader
{
    public const int SupportedSchemaVersion = 1;

    public static CanonicalManifest Read(string input, NormalizationLimits? limits = null)
    {
        var bounded = limits ?? new NormalizationLimits();
        if (Encoding.UTF8.GetByteCount(input) > bounded.MaxInputBytes)
        {
            throw new NormalizationException("INPUT_TOO_LARGE", "The canonical manifest exceeds the configured size limit in UTF-8 bytes.");
        }

        JsonNode document;
        try
        {
            using var parsed = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                MaxDepth = bounded.MaxDepth + 1,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            ValidateJsonDocument(parsed.RootElement, 0, bounded, new Counter());
            document = JsonNode.Parse(input) ?? throw new NormalizationException("INVALID_BASELINE", "The canonical manifest is empty.");
        }
        catch (NormalizationException)
        {
            throw;
        }
        catch (JsonException exception) when (exception.Message.Contains("maximum depth", StringComparison.OrdinalIgnoreCase))
        {
            throw new NormalizationException("DEPTH_LIMIT", "The canonical manifest exceeds the configured nesting limit.");
        }
        catch (JsonException)
        {
            throw new NormalizationException("INVALID_BASELINE", "The baseline is not valid canonical JSON.");
        }

        try
        {
            return ParseManifest(RequireObject(document, "INVALID_BASELINE"), bounded);
        }
        catch (NormalizationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new NormalizationException("INVALID_BASELINE", "The baseline is not a valid canonical manifest.");
        }
    }

    private static CanonicalManifest ParseManifest(JsonObject value, NormalizationLimits limits)
    {
        EnsureProperties(value, ["SchemaVersion", "Adapter", "SourceVersion", "Root"], "manifest");
        var schemaVersion = RequiredInt(value, "SchemaVersion");
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new NormalizationException("UNSUPPORTED_MANIFEST_VERSION", "The canonical manifest schema version is not supported.");
        }

        var adapter = RequiredString(value, "Adapter", limits);
        var sourceVersion = RequiredString(value, "SourceVersion", limits);
        if (!string.Equals(adapter, "opencli", StringComparison.Ordinal) || !string.Equals(sourceVersion, Normalizer.OpenCliVersion, StringComparison.Ordinal))
        {
            throw new NormalizationException("UNSUPPORTED_MANIFEST_SOURCE", "The canonical manifest source format is not supported.");
        }

        return new CanonicalManifest
        {
            SchemaVersion = schemaVersion,
            Adapter = adapter,
            SourceVersion = sourceVersion,
            Root = ParseCommand(RequireObject(value["Root"], "INVALID_BASELINE"), limits)
        };
    }

    private static CanonicalCommand ParseCommand(JsonObject value, NormalizationLimits limits)
    {
        EnsureProperties(value, ["Path", "Aliases", "Summary", "Description", "Status", "Arguments", "Options", "Subcommands"], "command");
        return new CanonicalCommand
        {
            Path = RequiredString(value, "Path", limits),
            Aliases = ReadStrings(value["Aliases"], limits),
            Summary = ReadNullableString(value, "Summary", limits),
            Description = ReadNullableString(value, "Description", limits),
            Status = ReadNullableString(value, "Status", limits),
            Arguments = ReadParameters(value["Arguments"], false, limits).Cast<CanonicalArgument>().ToArray(),
            Options = ReadParameters(value["Options"], true, limits).Cast<CanonicalOption>().ToArray(),
            Subcommands = ReadCommands(value["Subcommands"], limits)
        };
    }

    private static CanonicalCommand[] ReadCommands(JsonNode? node, NormalizationLimits limits)
    {
        var array = node as JsonArray ?? throw new NormalizationException("INVALID_BASELINE", "A canonical command collection must be an array.");
        CheckCollection(array.Count, limits);
        return array.Select(item => ParseCommand(RequireObject(item, "INVALID_BASELINE"), limits)).ToArray();
    }

    private static IEnumerable<CanonicalParameter> ReadParameters(JsonNode? node, bool option, NormalizationLimits limits)
    {
        var array = node as JsonArray ?? throw new NormalizationException("INVALID_BASELINE", "A canonical parameter collection must be an array.");
        CheckCollection(array.Count, limits);
        foreach (var item in array)
        {
            var value = RequireObject(item, "INVALID_BASELINE");
            string[] properties = option
                ? ["Name", "Aliases", "Summary", "Description", "Type", "Required", "ArityMinimum", "ArityMaximum", "AllowedValues", "DefaultValue", "AlternativeSources", "Status"]
                : ["Name", "Summary", "Description", "Type", "Required", "ArityMinimum", "ArityMaximum", "AllowedValues", "DefaultValue", "AlternativeSources", "Status"];
            EnsureProperties(value, properties, "parameter");
            var common = new ParameterParts(
                RequiredString(value, "Name", limits),
                ReadNullableString(value, "Summary", limits),
                ReadNullableString(value, "Description", limits),
                ReadNullableString(value, "Type", limits),
                ReadNullableBool(value, "Required"),
                ReadNullableInt(value, "ArityMinimum"),
                ReadNullableInt(value, "ArityMaximum"),
                ReadScalarArray(value["AllowedValues"], limits),
                ReadNullableScalar(value, "DefaultValue"),
                ReadSources(value["AlternativeSources"], limits),
                ReadNullableString(value, "Status", limits));

            if (option)
            {
                yield return new CanonicalOption
                {
                    Name = common.Name,
                    Aliases = ReadStrings(value["Aliases"], limits),
                    Summary = common.Summary,
                    Description = common.Description,
                    Type = common.Type,
                    Required = common.Required,
                    ArityMinimum = common.Minimum,
                    ArityMaximum = common.Maximum,
                    AllowedValues = common.AllowedValues,
                    DefaultValue = common.DefaultValue,
                    AlternativeSources = common.Sources,
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
                    AlternativeSources = common.Sources,
                    Status = common.Status
                };
            }
        }
    }

    private static CanonicalAlternativeSource[] ReadSources(JsonNode? node, NormalizationLimits limits)
    {
        var array = node as JsonArray ?? throw new NormalizationException("INVALID_BASELINE", "A canonical source collection must be an array.");
        CheckCollection(array.Count, limits);
        return array.Select(item =>
        {
            var value = RequireObject(item, "INVALID_BASELINE");
            EnsureProperties(value, ["Type", "Property"], "source");
            return new CanonicalAlternativeSource
            {
                Type = RequiredString(value, "Type", limits),
                Property = RequiredString(value, "Property", limits)
            };
        }).ToArray();
    }

    private static JsonNode[] ReadScalarArray(JsonNode? node, NormalizationLimits limits)
    {
        var array = node as JsonArray ?? throw new NormalizationException("INVALID_BASELINE", "AllowedValues must be an array.");
        CheckCollection(array.Count, limits);
        return array.Select(item =>
        {
            if (item is not JsonValue || item.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            {
                throw new NormalizationException("INVALID_BASELINE", "AllowedValues must contain scalar values.");
            }

            if (item.GetValueKind() == JsonValueKind.String)
            {
                _ = Bounded(item.GetValue<string>(), limits);
            }

            return Normalizer.CanonicalizeScalar(item);
        }).ToArray();
    }

    private static JsonNode? ReadNullableScalar(JsonObject value, string property)
    {
        if (!value.ContainsKey(property) || value[property] is null) return null;
        return Normalizer.CanonicalizeScalar(value[property]!);
    }

    private static string[] ReadStrings(JsonNode? node, NormalizationLimits limits)
    {
        var array = node as JsonArray ?? throw new NormalizationException("INVALID_BASELINE", "A canonical string collection must be an array.");
        CheckCollection(array.Count, limits);
        return array.Select(item =>
        {
            if (item is not JsonValue || item.GetValueKind() != JsonValueKind.String)
            {
                throw new NormalizationException("INVALID_BASELINE", "A canonical string collection must contain strings.");
            }

            return Bounded(item.GetValue<string>(), limits);
        }).ToArray();
    }

    private static string RequiredString(JsonObject value, string property, NormalizationLimits limits)
    {
        if (value[property] is not JsonValue node || node.GetValueKind() != JsonValueKind.String)
        {
            throw new NormalizationException("INVALID_BASELINE", "A required canonical string is missing or invalid.");
        }

        return Bounded(node.GetValue<string>(), limits);
    }

    private static string? ReadNullableString(JsonObject value, string property, NormalizationLimits limits)
    {
        if (!value.ContainsKey(property) || value[property] is null) return null;
        return RequiredString(value, property, limits);
    }

    private static bool? ReadNullableBool(JsonObject value, string property)
    {
        if (!value.ContainsKey(property) || value[property] is null) return null;
        if (value[property] is JsonValue node && (node.GetValueKind() is JsonValueKind.True or JsonValueKind.False)) return node.GetValue<bool>();
        throw new NormalizationException("INVALID_BASELINE", "A canonical boolean is invalid.");
    }

    private static int? ReadNullableInt(JsonObject value, string property)
    {
        if (!value.ContainsKey(property) || value[property] is null) return null;
        return RequiredInt(value, property);
    }

    private static int RequiredInt(JsonObject value, string property)
    {
        if (value[property] is JsonValue node && node.GetValueKind() == JsonValueKind.Number && node.TryGetValue<int>(out var result)) return result;
        throw new NormalizationException("INVALID_BASELINE", "A canonical integer is invalid.");
    }

    private static void EnsureProperties(JsonObject value, IReadOnlyCollection<string> allowed, string subject)
    {
        foreach (var property in value.Select(item => item.Key))
        {
            if (!allowed.Contains(property, StringComparer.Ordinal))
            {
                throw new NormalizationException("UNKNOWN_MANIFEST_FIELD", $"The canonical {subject} contains an unknown field.");
            }
        }
    }

    private static JsonObject RequireObject(JsonNode? node, string code) => node as JsonObject ?? throw new NormalizationException(code, "The canonical manifest requires an object.");

    private static string Bounded(string value, NormalizationLimits limits)
    {
        if (value.Length > limits.MaxStringLength) throw new NormalizationException("STRING_TOO_LARGE", "A canonical manifest string exceeds the configured length limit.");
        return value;
    }

    private static void CheckCollection(int count, NormalizationLimits limits)
    {
        if (count > limits.MaxCollectionItems) throw new NormalizationException("COLLECTION_TOO_LARGE", "A canonical manifest collection exceeds the configured item limit.");
    }

    private static void ValidateJsonDocument(JsonElement value, int depth, NormalizationLimits limits, Counter counter)
    {
        if (depth > limits.MaxDepth) throw new NormalizationException("DEPTH_LIMIT", "The canonical manifest exceeds the configured nesting limit.");
        if (++counter.Value > limits.MaxNodes) throw new NormalizationException("NODE_LIMIT", "The canonical manifest exceeds the configured node limit.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new NormalizationException("DUPLICATE_JSON_KEY", "The canonical manifest contains a duplicate object key.");
                if (property.Name.Length > limits.MaxStringLength) throw new NormalizationException("STRING_TOO_LARGE", "A canonical manifest string exceeds the configured length limit.");
                ValidateJsonDocument(property.Value, depth + 1, limits, counter);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            CheckCollection(value.GetArrayLength(), limits);
            foreach (var item in value.EnumerateArray()) ValidateJsonDocument(item, depth + 1, limits, counter);
        }
        else if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > limits.MaxStringLength)
        {
            throw new NormalizationException("STRING_TOO_LARGE", "A canonical manifest string exceeds the configured length limit.");
        }
    }

    private sealed class Counter { public int Value; }
    private sealed record ParameterParts(string Name, string? Summary, string? Description, string? Type, bool? Required, int? Minimum, int? Maximum, JsonNode[] AllowedValues, JsonNode? DefaultValue, CanonicalAlternativeSource[] Sources, string? Status);
}
