using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

internal static class SourceContractProjection
{
    public static JsonObject Create(CanonicalManifest manifest)
    {
        var root = new JsonObject
        {
            ["opencliVersion"] = manifest.SourceVersion,
            ["info"] = ProjectInfo(manifest.Info)
        };

        if (manifest.Info.Install.Length > 0)
        {
            root["install"] = new JsonArray(manifest.Info.Install.Select(ProjectInstall).ToArray());
        }

        var commands = new JsonObject();
        foreach (var command in Flatten(manifest.Root))
        {
            if (command.Path == "root" && !NeedsExplicitRoot(command)) continue;
            commands[CommandKey(manifest.Info.Binary!, command.Path)] = ProjectCommand(command);
        }

        if (commands.Count > 0) root["commands"] = commands;

        var global = new JsonObject();
        if (manifest.GlobalConfig is not null)
        {
            var config = new JsonObject();
            foreach (var source in manifest.GlobalConfig.FileSources)
            {
                config[source.Format] = source.Path;
            }

            global["config"] = config;
        }

        if (manifest.GlobalExitCodes.Length > 0)
        {
            global["exitCodes"] = new JsonArray(manifest.GlobalExitCodes.Select(ProjectExitCode).ToArray());
        }

        if (manifest.GlobalOptions.Length > 0)
        {
            global["flags"] = new JsonArray(manifest.GlobalOptions.Select(option => ProjectParameter(option, option: true)).ToArray());
        }

        if (global.Count > 0) root["global"] = global;
        return root;
    }

    private static JsonObject ProjectInfo(CanonicalInfo info)
    {
        var result = new JsonObject
        {
            ["title"] = info.Title,
            ["binary"] = info.Binary,
            ["version"] = info.Version
        };
        AddOptional(result, "summary", info.Summary);
        AddOptional(result, "description", info.Description);

        if (info.License is not null)
        {
            var license = new JsonObject { ["name"] = info.License.Name };
            AddOptional(license, "spdxId", info.License.SpdxId);
            AddOptional(license, "url", info.License.Url);
            result["license"] = license;
        }

        if (info.Contact is not null)
        {
            var contact = new JsonObject();
            AddOptional(contact, "name", info.Contact.Name);
            AddOptional(contact, "email", info.Contact.Email);
            AddOptional(contact, "url", info.Contact.Url);
            result["contact"] = contact;
        }

        return result;
    }

    private static JsonObject ProjectInstall(CanonicalInstall install)
    {
        var result = new JsonObject { ["name"] = install.Name };
        AddOptional(result, "command", install.Command);
        AddOptional(result, "url", install.Url);
        AddOptional(result, "description", install.Description);
        return result;
    }

    private static JsonObject ProjectCommand(CanonicalCommand command)
    {
        var result = new JsonObject { ["kind"] = command.Kind };
        if (command.Aliases.Length > 0) result["aliases"] = Strings(command.Aliases);
        AddOptional(result, "summary", command.Summary);
        AddOptional(result, "description", command.Description);
        if (command.Hidden) result["hidden"] = true;
        if (command.ExitCodes.Length > 0) result["exitCodes"] = new JsonArray(command.ExitCodes.Select(ProjectExitCode).ToArray());
        if (command.Examples.Length > 0) result["examples"] = new JsonArray(command.Examples.Select(ProjectExample).ToArray());
        if (command.Arguments.Length > 0) result["args"] = new JsonArray(command.Arguments.Select(argument => ProjectParameter(argument, option: false)).ToArray());
        if (command.Options.Length > 0) result["flags"] = new JsonArray(command.Options.Select(option => ProjectParameter(option, option: true)).ToArray());
        return result;
    }

    private static JsonObject ProjectParameter(CanonicalParameter parameter, bool option)
    {
        var name = option ? SourceOptionName((CanonicalOption)parameter) : parameter.Name;
        var result = new JsonObject { ["name"] = name };
        if (option)
        {
            var canonicalOption = (CanonicalOption)parameter;
            if (canonicalOption.Aliases.Length > 0) result["aliases"] = Strings(canonicalOption.Aliases);
        }

        AddOptional(result, "summary", parameter.Summary);
        AddOptional(result, "description", parameter.Description);
        AddOptional(result, "type", parameter.Type);
        if (parameter.Required == true) result["required"] = true;
        if (parameter.Variadic)
        {
            result["variadic"] = true;
            if (parameter.ArityMinimum is not null) result["minItems"] = parameter.ArityMinimum.Value;
            if (parameter.ArityMaximum is not null) result["maxItems"] = parameter.ArityMaximum.Value;
        }

        AddOptional(result, "hint", option ? parameter.Hint : null);
        if (parameter.Hidden) result["hidden"] = true;
        if (parameter.Choices.Length > 0) result["choices"] = new JsonArray(parameter.Choices.Select(ProjectChoice).ToArray());
        if (option && parameter.DefaultValue is not null) result["default"] = parameter.DefaultValue.DeepClone();
        if (option && parameter.AlternativeSources.Length > 0) result["alternativeSources"] = new JsonArray(parameter.AlternativeSources.Select(ProjectSource).ToArray());
        if (!option && parameter is CanonicalArgument argument && argument.Passthrough) result["passthrough"] = true;
        return result;
    }

    private static string SourceOptionName(CanonicalOption option)
    {
        if (!SourceContractRules.IsNormalizedOptionName(option.Name))
        {
            throw new NormalizationException("INVALID_BASELINE", "A canonical option name is not produced by the source normalizer.");
        }

        return option.Name == "--" ? "-" : option.Name[2..];
    }

    private static JsonObject ProjectChoice(CanonicalChoice choice)
    {
        var result = new JsonObject { ["value"] = choice.Value.DeepClone() };
        AddOptional(result, "description", choice.Description);
        return result;
    }

    private static JsonObject ProjectSource(CanonicalAlternativeSource source) => new()
    {
        ["type"] = source.Type,
        ["property"] = source.Property
    };

    private static JsonObject ProjectExitCode(CanonicalExitCode exitCode)
    {
        var result = new JsonObject
        {
            ["code"] = exitCode.Code,
            ["status"] = exitCode.Status,
            ["summary"] = exitCode.Summary
        };
        AddOptional(result, "description", exitCode.Description);
        return result;
    }

    private static JsonObject ProjectExample(CanonicalExample example)
    {
        var result = new JsonObject { ["content"] = example.Content };
        AddOptional(result, "title", example.Title);
        return result;
    }

    private static bool NeedsExplicitRoot(CanonicalCommand root) =>
        root.Kind != "group" ||
        root.Aliases.Length > 0 ||
        root.Summary is not null ||
        root.Description is not null ||
        root.Hidden ||
        root.ExitCodes.Length > 0 ||
        root.Examples.Length > 0 ||
        root.Arguments.Length > 0 ||
        root.Options.Length > 0;

    private static string CommandKey(string binary, string path) =>
        path == "root" ? binary : binary + " " + string.Join(' ', path.Split(" / ").Skip(1));

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode)value).ToArray());

    private static IEnumerable<CanonicalCommand> Flatten(CanonicalCommand command)
    {
        yield return command;
        foreach (var child in command.Subcommands.SelectMany(Flatten)) yield return child;
    }

    private static void AddOptional(JsonObject target, string property, string? value)
    {
        if (value is not null) target[property] = value;
    }
}
