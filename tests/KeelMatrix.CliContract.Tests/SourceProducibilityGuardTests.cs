using System.Text.Json.Nodes;
using KeelMatrix.CliContract.Core;
using Xunit;

namespace KeelMatrix.CliContract.Tests;

public sealed class SourceProducibilityGuardTests
{
    private static readonly string[] EdgeValues =
    [
        "",
        " ",
        "---",
        " mixed\t whitespace ",
        "é",
        "!@#",
        new string('x', 4_096)
    ];

    private static readonly SourceFieldCase[] SourceFields =
    [
        Required("opencliVersion", (document, value) => document["opencliVersion"] = value, _ => false),
        Required("info.title", (document, value) => Info(document)["title"] = value),
        Optional("info.summary", (document, value) => Info(document)["summary"] = value),
        Optional("info.description", (document, value) => Info(document)["description"] = value),
        Required("info.binary", (document, value) => { Info(document)["binary"] = value; document.Remove("commands"); }),
        Required("info.version", (document, value) => Info(document)["version"] = value),
        Required("info.license.name", (document, value) => License(document)["name"] = value),
        Optional("info.license.spdxId", (document, value) => License(document)["spdxId"] = value),
        Optional("info.license.url", (document, value) => License(document)["url"] = value),
        Optional("info.contact.name", (document, value) => Contact(document)["name"] = value),
        Optional("info.contact.email", (document, value) => Contact(document)["email"] = value),
        Optional("info.contact.url", (document, value) => Contact(document)["url"] = value),
        Required("install.name", (document, value) => Install(document)["name"] = value),
        Optional("install.command", (document, value) => Install(document)["command"] = value),
        Optional("install.url", (document, value) => Install(document)["url"] = value),
        Optional("install.description", (document, value) => Install(document)["description"] = value),
        NonWhitespace("global.config.json", (document, value) => Config(document)["json"] = value),
        NonWhitespace("global.config.toml", (document, value) => Config(document)["toml"] = value),
        NonWhitespace("global.config.yaml", (document, value) => Config(document)["yaml"] = value),
        Domain("global.exitCodes.code", (document, value) => ExitCodes(document, "global")[0]! ["code"] = value),
        Domain("global.exitCodes.status", (document, value) => ExitCodes(document)[0]! ["status"] = value),
        Required("global.exitCodes.summary", (document, value) => ExitCodes(document)[0]! ["summary"] = value),
        Optional("global.exitCodes.description", (document, value) => ExitCodes(document)[0]! ["description"] = value),
        NonEmpty("root.aliases", (document, value) => Command(document, "tool")["aliases"] = new JsonArray(value)),
        Optional("root.summary", (document, value) => Command(document, "tool")["summary"] = value),
        Optional("root.description", (document, value) => Command(document, "tool")["description"] = value),
        Domain("root.hidden", (document, value) => Command(document, "tool")["hidden"] = value),
        Domain("root.kind", (document, value) => Command(document, "tool")["kind"] = value),
        Domain("root.exitCodes.code", (document, value) => ExitCodes(document, "root")[0]! ["code"] = value),
        Domain("root.exitCodes.status", (document, value) => ExitCodes(document, "root")[0]! ["status"] = value),
        Required("root.exitCodes.summary", (document, value) => ExitCodes(document, "root")[0]! ["summary"] = value),
        Optional("root.exitCodes.description", (document, value) => ExitCodes(document, "root")[0]! ["description"] = value),
        Optional("root.examples.title", (document, value) => Examples(document, "tool")[0]! ["title"] = value),
        Required("root.examples.content", (document, value) => Examples(document, "tool")[0]! ["content"] = value),
        Required("global.flag.name", (document, value) => GlobalFlag(document)["name"] = value),
        NonEmpty("global.flag.aliases", (document, value) => GlobalFlag(document)["aliases"] = new JsonArray(value)),
        Domain("global.flag.type", (document, value) => GlobalFlag(document)["type"] = value),
        Domain("global.flag.required", (document, value) => GlobalFlag(document)["required"] = value),
        Domain("global.flag.variadic", (document, value) => GlobalFlag(document)["variadic"] = value),
        Domain("global.flag.minItems", (document, value) => GlobalFlag(document)["minItems"] = value),
        Domain("global.flag.maxItems", (document, value) => GlobalFlag(document)["maxItems"] = value),
        Optional("global.flag.summary", (document, value) => GlobalFlag(document)["summary"] = value),
        Optional("global.flag.description", (document, value) => GlobalFlag(document)["description"] = value),
        Optional("global.flag.hint", (document, value) => GlobalFlag(document)["hint"] = value),
        Domain("global.flag.hidden", (document, value) => GlobalFlag(document)["hidden"] = value),
        Optional("global.flag.default", (document, value) => GlobalFlag(document)["default"] = value),
        Domain("global.flag.alternativeSources.type", (document, value) => Sources(document)[0]! ["type"] = value),
        Required("global.flag.alternativeSources.property", (document, value) => Sources(document)[0]! ["property"] = value),
        Optional("global.flag.choices.value", (document, value) => Choices(document)[0]! ["value"] = value),
        Optional("global.flag.choices.description", (document, value) => Choices(document)[0]! ["description"] = value),
        Required("root.argument.name", (document, value) => RootArgument(document)["name"] = value),
        Domain("root.argument.type", (document, value) => RootArgument(document)["type"] = value),
        Domain("root.argument.required", (document, value) => RootArgument(document)["required"] = value),
        Domain("root.argument.variadic", (document, value) => RootArgument(document)["variadic"] = value),
        Domain("root.argument.minItems", (document, value) => RootArgument(document)["minItems"] = value),
        Domain("root.argument.maxItems", (document, value) => RootArgument(document)["maxItems"] = value),
        Optional("root.argument.summary", (document, value) => RootArgument(document)["summary"] = value),
        Optional("root.argument.description", (document, value) => RootArgument(document)["description"] = value),
        Optional("root.argument.passthrough", (document, value) => RootArgument(document)["passthrough"] = value, _ => false)
    ];

    [Fact]
    public void Alpha14SourceFieldEdgeMatrixRemainsReflexiveAndBounded()
    {
        foreach (var field in SourceFields)
        {
            foreach (var value in EdgeValues)
            {
                var document = field.Create(value);
                var error = Record.Exception(() => NormalizeAndRead(document));
                if (field.Accepts(value))
                {
                    Assert.Null(error);
                }
                else
                {
                    Assert.IsType<NormalizationException>(error);
                }
            }
        }
    }

    private static CanonicalManifest NormalizeAndRead(JsonObject document)
    {
        var manifest = Normalizer.Normalize("opencli", document.ToJsonString());
        var serialized = Normalizer.Serialize(manifest);
        var roundTrip = CanonicalManifestReader.Read(serialized);
        Assert.Equal(serialized, Normalizer.Serialize(roundTrip));
        Assert.Empty(CompatibilityAnalyzer.Compare(roundTrip, roundTrip).Findings);
        return roundTrip;
    }

    private static SourceFieldCase Required(string name, Action<JsonObject, string> set, Func<string, bool>? accepts = null) =>
        new(name, set, accepts ?? (value => value.Length > 0));

    private static SourceFieldCase NonEmpty(string name, Action<JsonObject, string> set) =>
        new(name, set, value => value.Length > 0);

    private static SourceFieldCase NonWhitespace(string name, Action<JsonObject, string> set) =>
        new(name, set, value => !string.IsNullOrWhiteSpace(value));

    private static SourceFieldCase Optional(string name, Action<JsonObject, string> set, Func<string, bool>? accepts = null) =>
        new(name, set, accepts ?? (_ => true));

    private static SourceFieldCase Domain(string name, Action<JsonObject, string> set) =>
        new(name, set, _ => false);

    private static JsonObject BaseDocument() => JsonNode.Parse("""
        {
          "opencliVersion": "1.0.0-alpha.14",
          "info": {
            "title": "Tool",
            "summary": "Summary",
            "description": "Description",
            "binary": "tool",
            "version": "1",
            "license": { "name": "MIT", "spdxId": "MIT", "url": "https://example.invalid/license" },
            "contact": { "name": "Support", "email": "support@example.invalid", "url": "https://example.invalid/contact" }
          },
          "install": [{ "name": "download", "command": "tool install", "url": "https://example.invalid/install", "description": "Install" }],
          "global": {
            "config": { "json": "config.json", "toml": "config.toml", "yaml": "config.yaml" },
            "exitCodes": [{ "code": 0, "status": "OK", "summary": "ok", "description": "success" }],
            "flags": [{
              "name": "global-option",
              "aliases": ["global-alias"],
              "type": "string",
              "summary": "Option summary",
              "description": "Option description",
              "hint": "value",
              "default": "default",
              "choices": [{ "value": "choice", "description": "Choice" }],
              "alternativeSources": [{ "type": "$ENV", "property": "GLOBAL_OPTION" }]
            }]
          },
          "commands": {
            "tool": {
              "aliases": ["t"],
              "summary": "Root summary",
              "description": "Root description",
              "kind": "action",
              "hidden": false,
              "exitCodes": [{ "code": 0, "status": "OK", "summary": "ok", "description": "success" }],
              "examples": [{ "title": "Example", "content": "tool" }],
              "args": [{ "name": "root-argument", "type": "string", "summary": "Argument summary", "description": "Argument description", "required": false, "variadic": false }]
            },
            "tool run": {}
          }
        }
        """)!.AsObject();

    private static JsonObject Info(JsonObject document) => document["info"]!.AsObject();
    private static JsonObject License(JsonObject document) => Info(document)["license"]!.AsObject();
    private static JsonObject Contact(JsonObject document) => Info(document)["contact"]!.AsObject();
    private static JsonObject Install(JsonObject document) => document["install"]!.AsArray()[0]!.AsObject();
    private static JsonObject Config(JsonObject document) => document["global"]!["config"]!.AsObject();
    private static JsonArray ExitCodes(JsonObject document, string scope = "global") => scope == "global"
        ? document["global"]!["exitCodes"]!.AsArray()
        : Command(document, "tool")["exitCodes"]!.AsArray();
    private static JsonObject GlobalFlag(JsonObject document) => document["global"]!["flags"]!.AsArray()[0]!.AsObject();
    private static JsonArray Sources(JsonObject document) => GlobalFlag(document)["alternativeSources"]!.AsArray();
    private static JsonArray Choices(JsonObject document) => GlobalFlag(document)["choices"]!.AsArray();
    private static JsonObject Command(JsonObject document, string key) => document["commands"]![key]!.AsObject();
    private static JsonArray Examples(JsonObject document, string key) => Command(document, key)["examples"]!.AsArray();
    private static JsonObject RootArgument(JsonObject document) => Command(document, "tool")["args"]!.AsArray()[0]!.AsObject();

    private sealed record SourceFieldCase(string Name, Action<JsonObject, string> Set, Func<string, bool> Accepts)
    {
        public JsonObject Create(string value)
        {
            var document = (JsonNode.Parse(BaseDocument().ToJsonString())!).AsObject();
            Set(document, value);
            return document;
        }
    }
}
