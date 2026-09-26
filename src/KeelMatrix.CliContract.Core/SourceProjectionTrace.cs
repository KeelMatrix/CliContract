using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

internal sealed class SourceProjectionTrace
{
    private static readonly IReadOnlySet<string> RootFields = SetOf("opencliVersion", "info", "install", "commands", "global");
    private static readonly IReadOnlySet<string> InfoFields = SetOf("title", "summary", "description", "binary", "version", "license", "contact");
    private static readonly IReadOnlySet<string> LicenseFields = SetOf("name", "spdxId", "url");
    private static readonly IReadOnlySet<string> ContactFields = SetOf("name", "email", "url");
    private static readonly IReadOnlySet<string> InstallFields = SetOf("name", "command", "url", "description");
    private static readonly IReadOnlySet<string> GlobalFields = SetOf("config", "exitCodes", "flags");
    private static readonly IReadOnlySet<string> ConfigFields = SetOf("json", "toml", "yaml");
    private static readonly IReadOnlySet<string> CommandFields = SetOf("aliases", "summary", "description", "hidden", "kind", "exitCodes", "examples", "args", "flags");
    private static readonly IReadOnlySet<string> ExitCodeFields = SetOf("code", "status", "summary", "description");
    private static readonly IReadOnlySet<string> ExampleFields = SetOf("title", "content");
    private static readonly IReadOnlySet<string> ParameterFields = SetOf("name", "aliases", "summary", "description", "type", "required", "variadic", "minItems", "maxItems", "hint", "hidden", "choices", "default", "alternativeSources", "passthrough");
    private static readonly IReadOnlySet<string> ChoiceFields = SetOf("value", "description");
    private static readonly IReadOnlySet<string> AlternativeSourceFields = SetOf("type", "property");

    private readonly HashSet<string> fields = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Fields => fields;

    public void Record(SourceFieldDescriptor field) => fields.Add(field.Name);

    public void ObserveOutput(JsonObject document)
    {
        InspectObject(document, "root", RootFields, (property, value, path) =>
        {
            if (property == "info") InspectInfo(value, path);
            if (property == "install") InspectArray(value, path, InstallFields);
            if (property == "commands") InspectCommands(value, path);
            if (property == "global") InspectGlobal(value, path);
        });
    }

    private void InspectInfo(JsonNode? value, string path)
    {
        InspectObject(value, path, InfoFields, (property, child, childPath) =>
        {
            if (property == "license") InspectObject(child, childPath, LicenseFields);
            if (property == "contact") InspectObject(child, childPath, ContactFields);
        });
    }

    private void InspectGlobal(JsonNode? value, string path)
    {
        InspectObject(value, path, GlobalFields, (property, child, childPath) =>
        {
            if (property == "config") InspectObject(child, childPath, ConfigFields);
            if (property == "exitCodes") InspectArray(child, childPath, ExitCodeFields);
            if (property == "flags") InspectArray(child, childPath, ParameterFields, InspectParameter);
        });
    }

    private void InspectCommands(JsonNode? value, string path)
    {
        if (value is not JsonObject commands)
        {
            return;
        }

        foreach (var command in commands)
        {
            InspectObject(command.Value, path + "." + command.Key, CommandFields, (property, child, childPath) =>
            {
                if (property == "exitCodes") InspectArray(child, childPath, ExitCodeFields);
                if (property == "examples") InspectArray(child, childPath, ExampleFields);
                if (property == "args") InspectArray(child, childPath, ParameterFields, InspectParameter);
                if (property == "flags") InspectArray(child, childPath, ParameterFields, InspectParameter);
            });
        }
    }

    private void InspectParameter(JsonObject parameter, string path)
    {
        foreach (var property in parameter)
        {
            if (property.Key == "choices")
            {
                InspectArray(property.Value, path + ".choices", ChoiceFields);
            }
            else if (property.Key == "alternativeSources")
            {
                InspectArray(property.Value, path + ".alternativeSources", AlternativeSourceFields);
            }
        }
    }

    private void InspectArray(JsonNode? value, string path, IReadOnlySet<string> fields, Action<JsonObject, string>? inspect = null)
    {
        if (value is not JsonArray array)
        {
            return;
        }

        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is JsonObject item)
            {
                var itemPath = $"{path}[{index}]";
                InspectObject(item, itemPath, fields);
                inspect?.Invoke(item, itemPath);
            }
        }
    }

    private void InspectObject(
        JsonNode? value,
        string path,
        IReadOnlySet<string> allowed,
        Action<string, JsonNode?, string>? inspectKnown = null)
    {
        if (value is not JsonObject objectValue)
        {
            return;
        }

        foreach (var property in objectValue)
        {
            var propertyPath = path + "." + property.Key;
            if (!allowed.Contains(property.Key))
            {
                fields.Add(propertyPath);
                continue;
            }

            inspectKnown?.Invoke(property.Key, property.Value, propertyPath);
        }
    }

    private static HashSet<string> SetOf(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);
}
