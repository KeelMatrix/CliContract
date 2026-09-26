using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

internal sealed class SourceFieldDescriptor
{
    public SourceFieldDescriptor(
        string name,
        string property,
        Action<JsonObject, string> setSource,
        Func<string, bool> accepts)
    {
        Name = name;
        Property = property;
        SetSource = setSource;
        Accepts = accepts;
    }

    public string Name { get; }

    public string Property { get; }

    public Action<JsonObject, string> SetSource { get; }

    public Func<string, bool> Accepts { get; }

    public void Set(JsonObject target, object? value, SourceProjectionTrace? trace = null)
    {
        if (value is null)
        {
            target.Remove(Property);
            return;
        }

        target[Property] = value is JsonNode node ? node.DeepClone() : JsonValue.Create(value);
        trace?.Record(this);
    }
}

internal sealed record SourceCommandFields(
    SourceFieldDescriptor Aliases,
    SourceFieldDescriptor Summary,
    SourceFieldDescriptor Description,
    SourceFieldDescriptor Hidden,
    SourceFieldDescriptor Kind)
{
    public IReadOnlyList<SourceFieldDescriptor> All => [Aliases, Summary, Description, Hidden, Kind];
}

internal sealed record SourceExitCodeFields(
    SourceFieldDescriptor Code,
    SourceFieldDescriptor Status,
    SourceFieldDescriptor Summary,
    SourceFieldDescriptor Description)
{
    public IReadOnlyList<SourceFieldDescriptor> All => [Code, Status, Summary, Description];
}

internal sealed record SourceExampleFields(
    SourceFieldDescriptor Title,
    SourceFieldDescriptor Content)
{
    public IReadOnlyList<SourceFieldDescriptor> All => [Title, Content];
}

internal sealed record SourceParameterFields(
    SourceFieldDescriptor Name,
    SourceFieldDescriptor? Aliases,
    SourceFieldDescriptor Summary,
    SourceFieldDescriptor Description,
    SourceFieldDescriptor Type,
    SourceFieldDescriptor Required,
    SourceFieldDescriptor Variadic,
    SourceFieldDescriptor Minimum,
    SourceFieldDescriptor Maximum,
    SourceFieldDescriptor? Hint,
    SourceFieldDescriptor? Hidden,
    SourceFieldDescriptor? DefaultValue,
    SourceFieldDescriptor? Passthrough)
{
    public IReadOnlyList<SourceFieldDescriptor> All => new SourceFieldDescriptor?[]
    {
        Name, Aliases, Summary, Description, Type, Required, Variadic, Minimum, Maximum,
        Hint, Hidden, DefaultValue, Passthrough
    }.Where(field => field is not null).Select(field => field!).ToArray();
}

internal sealed record SourceChoiceFields(
    SourceFieldDescriptor Value,
    SourceFieldDescriptor Description)
{
    public IReadOnlyList<SourceFieldDescriptor> All => [Value, Description];
}

internal sealed record SourceAlternativeSourceFields(
    SourceFieldDescriptor Type,
    SourceFieldDescriptor Property)
{
    public IReadOnlyList<SourceFieldDescriptor> All => [Type, Property];
}

internal static class SourceContractFields
{
    private static readonly List<SourceFieldDescriptor> Registry = [];

    public static readonly SourceFieldDescriptor OpenCliVersion =
        Field("opencliVersion", "opencliVersion", (document, value) => document["opencliVersion"] = value, _ => false);

    public static readonly SourceInfoFields Info = new(
        Field("info.title", "title", (document, value) => InfoObject(document)["title"] = value, RequiredValue),
        Field("info.summary", "summary", (document, value) => InfoObject(document)["summary"] = value, AnyValue),
        Field("info.description", "description", (document, value) => InfoObject(document)["description"] = value, AnyValue),
        Field("info.binary", "binary", (document, value) =>
        {
            InfoObject(document)["binary"] = value;
            document.Remove("commands");
        }, RequiredValue),
        Field("info.version", "version", (document, value) => InfoObject(document)["version"] = value, RequiredValue),
        Field("info.license.name", "name", (document, value) => LicenseObject(document)["name"] = value, RequiredValue),
        Field("info.license.spdxId", "spdxId", (document, value) => LicenseObject(document)["spdxId"] = value, AnyValue),
        Field("info.license.url", "url", (document, value) => LicenseObject(document)["url"] = value, AnyValue),
        Field("info.contact.name", "name", (document, value) => ContactObject(document)["name"] = value, AnyValue),
        Field("info.contact.email", "email", (document, value) => ContactObject(document)["email"] = value, AnyValue),
        Field("info.contact.url", "url", (document, value) => ContactObject(document)["url"] = value, AnyValue));

    public static readonly SourceInstallFields Install = new(
        Field("install.name", "name", (document, value) => InstallObject(document)["name"] = value, RequiredValue),
        Field("install.command", "command", (document, value) => InstallObject(document)["command"] = value, AnyValue),
        Field("install.url", "url", (document, value) => InstallObject(document)["url"] = value, AnyValue),
        Field("install.description", "description", (document, value) => InstallObject(document)["description"] = value, AnyValue));

    public static readonly SourceGlobalFields Global = new(
        new SourceFileConfigFields(
            Field("global.config.json", "json", (document, value) => ConfigObject(document)["json"] = value, NonWhitespaceValue),
            Field("global.config.toml", "toml", (document, value) => ConfigObject(document)["toml"] = value, NonWhitespaceValue),
            Field("global.config.yaml", "yaml", (document, value) => ConfigObject(document)["yaml"] = value, NonWhitespaceValue)),
        new SourceExitCodeFields(
            Field("global.exitCodes.code", "code", (document, value) => ExitCodeObject(document, "global")["code"] = value, _ => false),
            Field("global.exitCodes.status", "status", (document, value) => ExitCodeObject(document, "global")["status"] = value, _ => false),
            Field("global.exitCodes.summary", "summary", (document, value) => ExitCodeObject(document, "global")["summary"] = value, RequiredValue),
            Field("global.exitCodes.description", "description", (document, value) => ExitCodeObject(document, "global")["description"] = value, AnyValue)),
        new SourceParameterFields(
            Field("global.flag.name", "name", (document, value) => ParameterObject(document, "global", "flag")["name"] = value, RequiredValue),
            Field("global.flag.aliases", "aliases", (document, value) => ParameterObject(document, "global", "flag")["aliases"] = Strings(value), NonEmptyValue),
            Field("global.flag.summary", "summary", (document, value) => ParameterObject(document, "global", "flag")["summary"] = value, AnyValue),
            Field("global.flag.description", "description", (document, value) => ParameterObject(document, "global", "flag")["description"] = value, AnyValue),
            Field("global.flag.type", "type", (document, value) => ParameterObject(document, "global", "flag")["type"] = value, _ => false),
            Field("global.flag.required", "required", (document, value) => ParameterObject(document, "global", "flag")["required"] = value, _ => false),
            Field("global.flag.variadic", "variadic", (document, value) => ParameterObject(document, "global", "flag")["variadic"] = value, _ => false),
            Field("global.flag.minItems", "minItems", (document, value) => ParameterObject(document, "global", "flag")["minItems"] = value, _ => false),
            Field("global.flag.maxItems", "maxItems", (document, value) => ParameterObject(document, "global", "flag")["maxItems"] = value, _ => false),
            Field("global.flag.hint", "hint", (document, value) => ParameterObject(document, "global", "flag")["hint"] = value, AnyValue),
            Field("global.flag.hidden", "hidden", (document, value) => ParameterObject(document, "global", "flag")["hidden"] = value, _ => false),
            Field("global.flag.default", "default", (document, value) => ParameterObject(document, "global", "flag")["default"] = value, AnyValue),
            null));

    public static readonly SourceCommandFields RootCommand = new(
        Field("root.command.aliases", "aliases", (document, value) => CommandObject(document, "tool")["aliases"] = Strings(value), NonEmptyValue),
        Field("root.command.summary", "summary", (document, value) => CommandObject(document, "tool")["summary"] = value, AnyValue),
        Field("root.command.description", "description", (document, value) => CommandObject(document, "tool")["description"] = value, AnyValue),
        Field("root.command.hidden", "hidden", (document, value) => CommandObject(document, "tool")["hidden"] = value, _ => false),
        Field("root.command.kind", "kind", (document, value) => CommandObject(document, "tool")["kind"] = value, _ => false));

    public static readonly SourceExitCodeFields RootExitCodes = ExitCodes("root", "tool");

    public static readonly SourceExampleFields RootExamples = Examples("root", "tool");

    public static readonly SourceParameterFields RootArgument = Parameters("root.argument", "root", "argument", option: false);

    public static readonly SourceParameterFields RootFlag = Parameters("root.flag", "root", "flag", option: true);

    public static readonly SourceCommandFields Command = new(
        Field("command.aliases", "aliases", (document, value) => CommandObject(document, "tool run")["aliases"] = Strings(value), NonEmptyValue),
        Field("command.summary", "summary", (document, value) => CommandObject(document, "tool run")["summary"] = value, AnyValue),
        Field("command.description", "description", (document, value) => CommandObject(document, "tool run")["description"] = value, AnyValue),
        Field("command.hidden", "hidden", (document, value) => CommandObject(document, "tool run")["hidden"] = value, _ => false),
        Field("command.kind", "kind", (document, value) => CommandObject(document, "tool run")["kind"] = value, _ => false));

    public static readonly SourceExitCodeFields CommandExitCodes = ExitCodes("command", "tool run");

    public static readonly SourceExampleFields CommandExamples = Examples("command", "tool run");

    public static readonly SourceParameterFields CommandArgument = Parameters("command.argument", "command", "argument", option: false);

    public static readonly SourceParameterFields CommandFlag = Parameters("command.flag", "command", "flag", option: true);

    public static readonly SourceChoiceFields GlobalChoice = Choices("global.flag.choices", "global", "flag");
    public static readonly SourceChoiceFields RootArgumentChoice = Choices("root.argument.choices", "root", "argument");
    public static readonly SourceChoiceFields RootFlagChoice = Choices("root.flag.choices", "root", "flag");
    public static readonly SourceChoiceFields CommandArgumentChoice = Choices("command.argument.choices", "command", "argument");
    public static readonly SourceChoiceFields CommandFlagChoice = Choices("command.flag.choices", "command", "flag");

    public static readonly SourceAlternativeSourceFields GlobalAlternativeSource = AlternativeSources("global.flag.alternativeSources", "global");
    public static readonly SourceAlternativeSourceFields RootFlagAlternativeSource = AlternativeSources("root.flag.alternativeSources", "root");
    public static readonly SourceAlternativeSourceFields CommandFlagAlternativeSource = AlternativeSources("command.flag.alternativeSources", "command");

    public static IReadOnlyList<SourceFieldDescriptor> ExpectedFields => Registry;

    private static SourceFieldDescriptor Field(string name, string property, Action<JsonObject, string> setSource, Func<string, bool> accepts)
    {
        var descriptor = new SourceFieldDescriptor(name, property, setSource, accepts);
        Registry.Add(descriptor);
        return descriptor;
    }

    private static SourceExitCodeFields ExitCodes(string scope, string key) => new(
        Field($"{scope}.exitCodes.code", "code", (document, value) => ExitCodeObject(document, key)["code"] = value, _ => false),
        Field($"{scope}.exitCodes.status", "status", (document, value) => ExitCodeObject(document, key)["status"] = value, _ => false),
        Field($"{scope}.exitCodes.summary", "summary", (document, value) => ExitCodeObject(document, key)["summary"] = value, RequiredValue),
        Field($"{scope}.exitCodes.description", "description", (document, value) => ExitCodeObject(document, key)["description"] = value, AnyValue));

    private static SourceExampleFields Examples(string scope, string key) => new(
        Field($"{scope}.examples.title", "title", (document, value) => ExampleObject(document, key)["title"] = value, AnyValue),
        Field($"{scope}.examples.content", "content", (document, value) => ExampleObject(document, key)["content"] = value, RequiredValue));

    private static SourceParameterFields Parameters(string scope, string commandScope, string kind, bool option) => new(
        Field($"{scope}.name", "name", (document, value) => ParameterObject(document, commandScope, kind)["name"] = value, RequiredValue),
        option ? Field($"{scope}.aliases", "aliases", (document, value) => ParameterObject(document, commandScope, kind)["aliases"] = Strings(value), NonEmptyValue) : null,
        Field($"{scope}.summary", "summary", (document, value) => ParameterObject(document, commandScope, kind)["summary"] = value, AnyValue),
        Field($"{scope}.description", "description", (document, value) => ParameterObject(document, commandScope, kind)["description"] = value, AnyValue),
        Field($"{scope}.type", "type", (document, value) => ParameterObject(document, commandScope, kind)["type"] = value, _ => false),
        Field($"{scope}.required", "required", (document, value) => ParameterObject(document, commandScope, kind)["required"] = value, _ => false),
        Field($"{scope}.variadic", "variadic", (document, value) => ParameterObject(document, commandScope, kind)["variadic"] = value, _ => false),
        Field($"{scope}.minItems", "minItems", (document, value) => ParameterObject(document, commandScope, kind)["minItems"] = value, _ => false),
        Field($"{scope}.maxItems", "maxItems", (document, value) => ParameterObject(document, commandScope, kind)["maxItems"] = value, _ => false),
        option ? Field($"{scope}.hint", "hint", (document, value) => ParameterObject(document, commandScope, kind)["hint"] = value, AnyValue) : null,
        option ? Field($"{scope}.hidden", "hidden", (document, value) => ParameterObject(document, commandScope, kind)["hidden"] = value, _ => false) : null,
        option ? Field($"{scope}.default", "default", (document, value) => ParameterObject(document, commandScope, kind)["default"] = value, AnyValue) : null,
        !option ? Field($"{scope}.passthrough", "passthrough", (document, value) => ParameterObject(document, commandScope, kind)["passthrough"] = value, _ => false) : null);

    private static SourceChoiceFields Choices(string scope, string commandScope, string kind) => new(
        Field($"{scope}.value", "value", (document, value) => ChoiceObject(document, commandScope, kind)["value"] = value, AnyValue),
        Field($"{scope}.description", "description", (document, value) => ChoiceObject(document, commandScope, kind)["description"] = value, AnyValue));

    private static SourceAlternativeSourceFields AlternativeSources(string scope, string parameterScope) => new(
        Field($"{scope}.type", "type", (document, value) => AlternativeSourceObject(document, parameterScope)["type"] = value, _ => false),
        Field($"{scope}.property", "property", (document, value) => AlternativeSourceObject(document, parameterScope)["property"] = value, RequiredValue));

    private static JsonObject InfoObject(JsonObject document) => document["info"]!.AsObject();
    private static JsonObject LicenseObject(JsonObject document) => InfoObject(document)["license"]!.AsObject();
    private static JsonObject ContactObject(JsonObject document) => InfoObject(document)["contact"]!.AsObject();
    private static JsonObject InstallObject(JsonObject document) => document["install"]!.AsArray()[0]!.AsObject();
    private static JsonObject ConfigObject(JsonObject document) => document["global"]!["config"]!.AsObject();
    private static JsonObject CommandObject(JsonObject document, string key) => document["commands"]![key]!.AsObject();

    private static JsonObject ExitCodeObject(JsonObject document, string scope) => scope == "global"
        ? document["global"]!["exitCodes"]!.AsArray()[0]!.AsObject()
        : CommandObject(document, scope)["exitCodes"]!.AsArray()[0]!.AsObject();

    private static JsonObject ExampleObject(JsonObject document, string key) => CommandObject(document, key)["examples"]!.AsArray()[0]!.AsObject();

    private static JsonObject ParameterObject(JsonObject document, string scope, string kind)
    {
        if (scope == "global") return document["global"]!["flags"]!.AsArray()[0]!.AsObject();
        return CommandObject(document, scope == "root" ? "tool" : "tool run")[kind == "flag" ? "flags" : "args"]!.AsArray()[0]!.AsObject();
    }

    private static JsonObject ChoiceObject(JsonObject document, string scope, string kind) =>
        ParameterObject(document, scope, kind)["choices"]!.AsArray()[0]!.AsObject();

    private static JsonObject AlternativeSourceObject(JsonObject document, string scope) =>
        ParameterObject(document, scope, "flag")["alternativeSources"]!.AsArray()[0]!.AsObject();

    private static JsonArray Strings(string value) => new(value);

    private static bool RequiredValue(string value) => value.Length > 0;
    private static bool NonEmptyValue(string value) => value.Length > 0;
    private static bool NonWhitespaceValue(string value) => !string.IsNullOrWhiteSpace(value);
    private static bool AnyValue(string _) => true;
}

internal sealed record SourceInfoFields(
    SourceFieldDescriptor Title,
    SourceFieldDescriptor Summary,
    SourceFieldDescriptor Description,
    SourceFieldDescriptor Binary,
    SourceFieldDescriptor Version,
    SourceFieldDescriptor LicenseName,
    SourceFieldDescriptor LicenseSpdxId,
    SourceFieldDescriptor LicenseUrl,
    SourceFieldDescriptor ContactName,
    SourceFieldDescriptor ContactEmail,
    SourceFieldDescriptor ContactUrl)
{
    public IReadOnlyList<SourceFieldDescriptor> All =>
    [Title, Summary, Description, Binary, Version, LicenseName, LicenseSpdxId, LicenseUrl, ContactName, ContactEmail, ContactUrl];
}

internal sealed record SourceInstallFields(
    SourceFieldDescriptor Name,
    SourceFieldDescriptor Command,
    SourceFieldDescriptor Url,
    SourceFieldDescriptor Description)
{
    public IReadOnlyList<SourceFieldDescriptor> All => [Name, Command, Url, Description];
}

internal sealed record SourceFileConfigFields(
    SourceFieldDescriptor Json,
    SourceFieldDescriptor Toml,
    SourceFieldDescriptor Yaml)
{
    public IReadOnlyList<SourceFieldDescriptor> All => [Json, Toml, Yaml];
}

internal sealed record SourceGlobalFields(
    SourceFileConfigFields Config,
    SourceExitCodeFields ExitCodes,
    SourceParameterFields Flag);
