using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

public static class CanonicalManifestReader
{
    public const int SupportedSchemaVersion = 2;

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
        catch (JsonException exception) when (IsJsonDepthLimit(exception))
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
        EnsureProperties(value, ["SchemaVersion", "Adapter", "SourceVersion", "Info", "GlobalExitCodes", "GlobalConfig", "GlobalOptions", "Root"], "manifest");
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

        var root = ParseCommand(RequireObject(value["Root"], "INVALID_BASELINE"), limits);
        var manifest = new CanonicalManifest
        {
            SchemaVersion = schemaVersion,
            Adapter = adapter,
            SourceVersion = sourceVersion,
            Info = value.ContainsKey("Info") ? ParseInfo(value["Info"], limits) : new(),
            GlobalExitCodes = ReadExitCodes(value["GlobalExitCodes"], limits),
            GlobalConfig = value.ContainsKey("GlobalConfig") && value["GlobalConfig"] is not null ? ParseGlobalConfig(value["GlobalConfig"], limits) : null,
            GlobalOptions = ReadParameters(value["GlobalOptions"], true, limits).Cast<CanonicalOption>().ToArray(),
            Root = root
        };
        CanonicalInvariantValidator.Validate(manifest, limits);
        return manifest;
    }

    private static CanonicalGlobalConfig ParseGlobalConfig(JsonNode? node, NormalizationLimits limits)
    {
        var value = RequireObject(node, "INVALID_BASELINE");
        EnsureProperties(value, ["FileSources"], "global config");
        var sources = value["FileSources"] as JsonArray ?? throw new NormalizationException("INVALID_BASELINE", "Canonical file sources must be an array.");
        CheckCollection(sources.Count, limits);
        return new CanonicalGlobalConfig
        {
            FileSources = sources.Select(item =>
            {
                var source = RequireObject(item, "INVALID_BASELINE");
                EnsureProperties(source, ["Format", "Path"], "file source");
                return new CanonicalFileSource
                {
                    Format = RequiredString(source, "Format", limits),
                    Path = RequiredString(source, "Path", limits)
                };
            }).ToArray()
        };
    }

    private static CanonicalInfo ParseInfo(JsonNode? node, NormalizationLimits limits)
    {
        var value = RequireObject(node, "INVALID_BASELINE");
        EnsureProperties(value, ["Title", "Summary", "Description", "Binary", "Version", "License", "Contact", "Install"], "info");
        return new CanonicalInfo
        {
            Title = RequiredString(value, "Title", limits),
            Summary = ReadNullableString(value, "Summary", limits),
            Description = ReadNullableString(value, "Description", limits),
            Binary = RequiredString(value, "Binary", limits),
            Version = RequiredString(value, "Version", limits),
            License = value.ContainsKey("License") && value["License"] is not null ? ParseLicense(value["License"], limits) : null,
            Contact = value.ContainsKey("Contact") && value["Contact"] is not null ? ParseContact(value["Contact"], limits) : null,
            Install = value.ContainsKey("Install") ? ReadInstalls(value["Install"], limits) : []
        };
    }

    private static CanonicalLicense ParseLicense(JsonNode? node, NormalizationLimits limits)
    {
        var value = RequireObject(node, "INVALID_BASELINE");
        EnsureProperties(value, ["Name", "SpdxId", "Url"], "license");
        return new CanonicalLicense
        {
            Name = RequiredString(value, "Name", limits),
            SpdxId = ReadNullableString(value, "SpdxId", limits),
            Url = ReadNullableString(value, "Url", limits)
        };
    }

    private static CanonicalContact ParseContact(JsonNode? node, NormalizationLimits limits)
    {
        var value = RequireObject(node, "INVALID_BASELINE");
        EnsureProperties(value, ["Name", "Email", "Url"], "contact");
        return new CanonicalContact
        {
            Name = ReadNullableString(value, "Name", limits),
            Email = ReadNullableString(value, "Email", limits),
            Url = ReadNullableString(value, "Url", limits)
        };
    }

    private static CanonicalInstall[] ReadInstalls(JsonNode? node, NormalizationLimits limits)
    {
        var array = node as JsonArray ?? throw new NormalizationException("INVALID_BASELINE", "Canonical install metadata must be an array.");
        CheckCollection(array.Count, limits);
        return array.Select(item => ParseInstall(item, limits)).ToArray();
    }

    private static CanonicalInstall ParseInstall(JsonNode? node, NormalizationLimits limits)
    {
        var value = RequireObject(node, "INVALID_BASELINE");
        EnsureProperties(value, ["Name", "Command", "Url", "Description"], "install");
        return new CanonicalInstall
        {
            Name = RequiredString(value, "Name", limits),
            Command = ReadNullableString(value, "Command", limits),
            Url = ReadNullableString(value, "Url", limits),
            Description = ReadNullableString(value, "Description", limits)
        };
    }

    private static CanonicalCommand ParseCommand(JsonObject value, NormalizationLimits limits)
    {
        EnsureProperties(value, ["Path", "Kind", "Aliases", "Summary", "Description", "Status", "Hidden", "ExitCodes", "Examples", "Arguments", "Options", "Subcommands"], "command");
        var kind = ReadNullableString(value, "Kind", limits);
        if (kind is not null and not ("action" or "group"))
        {
            throw new NormalizationException("INVALID_BASELINE", "A canonical command kind must be action or group.");
        }

        return new CanonicalCommand
        {
            Path = RequiredString(value, "Path", limits),
            Kind = kind,
            Aliases = ReadStrings(value["Aliases"], limits),
            Summary = ReadNullableString(value, "Summary", limits),
            Description = ReadNullableString(value, "Description", limits),
            Status = ReadNullableString(value, "Status", limits),
            Hidden = ReadNullableBool(value, "Hidden") ?? false,
            ExitCodes = ReadExitCodes(value["ExitCodes"], limits),
            Examples = ReadExamples(value["Examples"], limits),
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
                ? ["Name", "Aliases", "Summary", "Description", "Type", "Required", "ArityMinimum", "ArityMaximum", "Variadic", "Hint", "Hidden", "AllowedValues", "Choices", "DefaultValue", "AlternativeSources", "Status"]
                : ["Name", "Summary", "Description", "Type", "Required", "ArityMinimum", "ArityMaximum", "Variadic", "Hint", "Hidden", "AllowedValues", "Choices", "DefaultValue", "AlternativeSources", "Passthrough", "Status"];
            EnsureProperties(value, properties, "parameter");
            var common = new ParameterParts(
                RequiredString(value, "Name", limits),
                ReadNullableString(value, "Summary", limits),
                ReadNullableString(value, "Description", limits),
                ReadNullableString(value, "Type", limits),
                ReadNullableBool(value, "Required"),
                ReadNullableInt(value, "ArityMinimum"),
                ReadNullableInt(value, "ArityMaximum"),
                ReadNullableBool(value, "Variadic") ?? false,
                ReadNullableString(value, "Hint", limits),
                ReadNullableBool(value, "Hidden") ?? false,
                ReadScalarArray(value["AllowedValues"], limits),
                ReadChoices(value["Choices"], limits),
                ReadNullableScalar(value, "DefaultValue"),
                ReadSources(value["AlternativeSources"], limits),
                option ? false : ReadNullableBool(value, "Passthrough") ?? false,
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
                    Variadic = common.Variadic,
                    Hint = common.Hint,
                    Hidden = common.Hidden,
                    AllowedValues = common.AllowedValues,
                    Choices = common.Choices,
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
                    Variadic = common.Variadic,
                    Hint = common.Hint,
                    Hidden = common.Hidden,
                    AllowedValues = common.AllowedValues,
                    Choices = common.Choices,
                    DefaultValue = common.DefaultValue,
                    AlternativeSources = common.Sources,
                    Passthrough = common.Passthrough,
                    Status = common.Status
                };
            }
        }
    }

    private static CanonicalExitCode[] ReadExitCodes(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return [];
        var array = node as JsonArray ?? throw new NormalizationException("INVALID_BASELINE", "Canonical exit codes must be an array.");
        CheckCollection(array.Count, limits);
        return array.Select(item =>
        {
            var value = RequireObject(item, "INVALID_BASELINE");
            EnsureProperties(value, ["Code", "Status", "Summary", "Description"], "exit code");
            return new CanonicalExitCode
            {
                Code = RequiredInt(value, "Code"),
                Status = RequiredString(value, "Status", limits),
                Summary = RequiredString(value, "Summary", limits),
                Description = ReadNullableString(value, "Description", limits)
            };
        }).ToArray();
    }

    private static CanonicalExample[] ReadExamples(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return [];
        var array = node as JsonArray ?? throw new NormalizationException("INVALID_BASELINE", "Canonical examples must be an array.");
        CheckCollection(array.Count, limits);
        return array.Select(item =>
        {
            var value = RequireObject(item, "INVALID_BASELINE");
            EnsureProperties(value, ["Title", "Content"], "example");
            return new CanonicalExample
            {
                Title = ReadNullableString(value, "Title", limits),
                Content = RequiredString(value, "Content", limits)
            };
        }).ToArray();
    }

    private static CanonicalChoice[] ReadChoices(JsonNode? node, NormalizationLimits limits)
    {
        if (node is null) return [];
        var array = node as JsonArray ?? throw new NormalizationException("INVALID_BASELINE", "Canonical choices must be an array.");
        CheckCollection(array.Count, limits);
        return array.Select(item =>
        {
            var value = RequireObject(item, "INVALID_BASELINE");
            EnsureProperties(value, ["Value", "Description"], "choice");
            return new CanonicalChoice
            {
                Value = ReadRequiredScalar(value["Value"], limits),
                Description = ReadNullableString(value, "Description", limits)
            };
        }).ToArray();
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

    private static JsonNode ReadRequiredScalar(JsonNode? node, NormalizationLimits limits)
    {
        if (node is not JsonValue || node.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
        {
            throw new NormalizationException("INVALID_BASELINE", "A canonical scalar is invalid.");
        }

        if (node.GetValueKind() == JsonValueKind.String) _ = Bounded(node.GetValue<string>(), limits);
        return Normalizer.CanonicalizeScalar(node);
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

    private static bool IsJsonDepthLimit(JsonException exception) =>
        exception.Message.Contains("maximum depth", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("maximum configured depth", StringComparison.OrdinalIgnoreCase);

    private sealed class Counter { public int Value; }
    private sealed record ParameterParts(string Name, string? Summary, string? Description, string? Type, bool? Required, int? Minimum, int? Maximum, bool Variadic, string? Hint, bool Hidden, JsonNode[] AllowedValues, CanonicalChoice[] Choices, JsonNode? DefaultValue, CanonicalAlternativeSource[] Sources, bool Passthrough, string? Status);
}
