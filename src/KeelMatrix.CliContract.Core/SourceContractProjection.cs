using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

internal static class SourceContractProjection
{
    public static JsonObject Create(CanonicalManifest manifest)
    {
        var root = new JsonObject
        {
            ["info"] = ProjectInfo(manifest.Info)
        };
        SourceContractFields.OpenCliVersion.Set(root, manifest.SourceVersion);

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
            global["exitCodes"] = new JsonArray(manifest.GlobalExitCodes.Select(exitCode => ProjectExitCode(exitCode, SourceContractFields.Global.ExitCodes)).ToArray());
        }

        if (manifest.GlobalOptions.Length > 0)
        {
            global["flags"] = new JsonArray(manifest.GlobalOptions.Select(option => ProjectParameter(
                option,
                option: true,
                SourceContractFields.Global.Flag,
                SourceContractFields.GlobalChoice,
                SourceContractFields.GlobalAlternativeSource)).ToArray());
        }

        if (global.Count > 0) root["global"] = global;
        return root;
    }

    private static JsonObject ProjectInfo(CanonicalInfo info)
    {
        var result = new JsonObject();
        SourceContractFields.Info.Title.Set(result, info.Title);
        SourceContractFields.Info.Summary.Set(result, info.Summary);
        SourceContractFields.Info.Description.Set(result, info.Description);
        SourceContractFields.Info.Binary.Set(result, info.Binary);
        SourceContractFields.Info.Version.Set(result, info.Version);

        if (info.License is not null)
        {
            var license = new JsonObject();
            SourceContractFields.Info.LicenseName.Set(license, info.License.Name);
            SourceContractFields.Info.LicenseSpdxId.Set(license, info.License.SpdxId);
            SourceContractFields.Info.LicenseUrl.Set(license, info.License.Url);
            result["license"] = license;
        }

        if (info.Contact is not null)
        {
            var contact = new JsonObject();
            SourceContractFields.Info.ContactName.Set(contact, info.Contact.Name);
            SourceContractFields.Info.ContactEmail.Set(contact, info.Contact.Email);
            SourceContractFields.Info.ContactUrl.Set(contact, info.Contact.Url);
            result["contact"] = contact;
        }

        return result;
    }

    private static JsonObject ProjectInstall(CanonicalInstall install)
    {
        var result = new JsonObject();
        SourceContractFields.Install.Name.Set(result, install.Name);
        SourceContractFields.Install.Command.Set(result, install.Command);
        SourceContractFields.Install.Url.Set(result, install.Url);
        SourceContractFields.Install.Description.Set(result, install.Description);
        return result;
    }

    private static JsonObject ProjectCommand(CanonicalCommand command)
    {
        var root = command.Path == "root";
        var commandFields = root ? SourceContractFields.RootCommand : SourceContractFields.Command;
        var exitCodeFields = root ? SourceContractFields.RootExitCodes : SourceContractFields.CommandExitCodes;
        var exampleFields = root ? SourceContractFields.RootExamples : SourceContractFields.CommandExamples;
        var argumentFields = root ? SourceContractFields.RootArgument : SourceContractFields.CommandArgument;
        var flagFields = root ? SourceContractFields.RootFlag : SourceContractFields.CommandFlag;
        var argumentChoiceFields = root ? SourceContractFields.RootArgumentChoice : SourceContractFields.CommandArgumentChoice;
        var flagChoiceFields = root ? SourceContractFields.RootFlagChoice : SourceContractFields.CommandFlagChoice;
        var alternativeSourceFields = root ? SourceContractFields.RootFlagAlternativeSource : SourceContractFields.CommandFlagAlternativeSource;
        var result = new JsonObject();
        commandFields.Kind.Set(result, command.Kind);
        if (command.Aliases.Length > 0) commandFields.Aliases.Set(result, Strings(command.Aliases));
        if (command.Summary is not null) commandFields.Summary.Set(result, command.Summary);
        if (command.Description is not null) commandFields.Description.Set(result, command.Description);
        if (command.Hidden) commandFields.Hidden.Set(result, true);
        if (command.ExitCodes.Length > 0) result["exitCodes"] = new JsonArray(command.ExitCodes.Select(exitCode => ProjectExitCode(exitCode, exitCodeFields)).ToArray());
        if (command.Examples.Length > 0) result["examples"] = new JsonArray(command.Examples.Select(example => ProjectExample(example, exampleFields)).ToArray());
        if (command.Arguments.Length > 0) result["args"] = new JsonArray(command.Arguments.Select(argument => ProjectParameter(argument, option: false, argumentFields, argumentChoiceFields, alternativeSourceFields)).ToArray());
        if (command.Options.Length > 0) result["flags"] = new JsonArray(command.Options.Select(option => ProjectParameter(option, option: true, flagFields, flagChoiceFields, alternativeSourceFields)).ToArray());
        return result;
    }

    private static JsonObject ProjectParameter(
        CanonicalParameter parameter,
        bool option,
        SourceParameterFields fields,
        SourceChoiceFields choiceFields,
        SourceAlternativeSourceFields alternativeSourceFields)
    {
        var name = option ? SourceOptionName((CanonicalOption)parameter) : parameter.Name;
        var result = new JsonObject();
        fields.Name.Set(result, name);
        if (option)
        {
            var canonicalOption = (CanonicalOption)parameter;
            if (canonicalOption.Aliases.Length > 0) fields.Aliases!.Set(result, Strings(canonicalOption.Aliases));
        }

        if (parameter.Summary is not null) fields.Summary.Set(result, parameter.Summary);
        if (parameter.Description is not null) fields.Description.Set(result, parameter.Description);
        if (parameter.Type is not null) fields.Type.Set(result, parameter.Type);
        if (parameter.Required == true) fields.Required.Set(result, true);
        if (parameter.Variadic)
        {
            fields.Variadic.Set(result, true);
            if (parameter.ArityMinimum is not null) fields.Minimum.Set(result, parameter.ArityMinimum.Value);
            if (parameter.ArityMaximum is not null) fields.Maximum.Set(result, parameter.ArityMaximum.Value);
        }

        if (option && parameter.Hint is not null) fields.Hint!.Set(result, parameter.Hint);
        if (parameter.Hidden && fields.Hidden is not null) fields.Hidden.Set(result, true);
        if (parameter.Choices.Length > 0) result["choices"] = new JsonArray(parameter.Choices.Select(choice => ProjectChoice(choice, choiceFields)).ToArray());
        if (option && parameter.DefaultValue is not null) fields.DefaultValue!.Set(result, parameter.DefaultValue);
        if (option && parameter.AlternativeSources.Length > 0) result["alternativeSources"] = new JsonArray(parameter.AlternativeSources.Select(source => ProjectSource(source, alternativeSourceFields)).ToArray());
        if (!option && parameter is CanonicalArgument argument && argument.Passthrough) fields.Passthrough!.Set(result, true);
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

    private static JsonObject ProjectChoice(CanonicalChoice choice, SourceChoiceFields fields)
    {
        var result = new JsonObject();
        fields.Value.Set(result, choice.Value);
        if (choice.Description is not null) fields.Description.Set(result, choice.Description);
        return result;
    }

    private static JsonObject ProjectSource(CanonicalAlternativeSource source, SourceAlternativeSourceFields fields)
    {
        var result = new JsonObject();
        fields.Type.Set(result, source.Type);
        fields.Property.Set(result, source.Property);
        return result;
    }

    private static JsonObject ProjectExitCode(CanonicalExitCode exitCode, SourceExitCodeFields fields)
    {
        var result = new JsonObject();
        fields.Code.Set(result, exitCode.Code);
        fields.Status.Set(result, exitCode.Status);
        fields.Summary.Set(result, exitCode.Summary);
        if (exitCode.Description is not null) fields.Description.Set(result, exitCode.Description);
        return result;
    }

    private static JsonObject ProjectExample(CanonicalExample example, SourceExampleFields fields)
    {
        var result = new JsonObject();
        fields.Content.Set(result, example.Content);
        if (example.Title is not null) fields.Title.Set(result, example.Title);
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

}
