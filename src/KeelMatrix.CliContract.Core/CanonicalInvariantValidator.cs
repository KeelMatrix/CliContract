using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

internal static class CanonicalInvariantValidator
{
    private static readonly string[] ExitCodeStatuses =
    [
        "BAD_USER_INPUT_ERROR",
        "UNAUTHENTICATED_ERROR",
        "UNAUTHORIZED_ERROR",
        "CANCELED_ERROR",
        "INTERNAL_CLI_ERROR",
        "NOT_IMPLEMENTED_ERROR",
        "OK"
    ];

    public static void Validate(CanonicalManifest manifest)
    {
        if (manifest.SchemaVersion != CanonicalManifestReader.SupportedSchemaVersion ||
            !string.Equals(manifest.Adapter, "opencli", StringComparison.Ordinal) ||
            !string.Equals(manifest.SourceVersion, Normalizer.OpenCliVersion, StringComparison.Ordinal))
        {
            throw new NormalizationException("INVALID_BASELINE", "The canonical manifest source identity is unsupported.");
        }

        RequireNonEmpty(manifest.Info.Title, "The canonical info title is required.");
        RequireNonEmpty(manifest.Info.Binary, "The canonical info binary is required.");
        RequireNonEmpty(manifest.Info.Version, "The canonical info version is required.");

        var commands = Flatten(manifest.Root).ToArray();
        var byPath = new Dictionary<string, CanonicalCommand>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (!byPath.TryAdd(command.Path, command))
            {
                throw new NormalizationException("OPENCLI_DUPLICATE_COMMAND_PATH", "The canonical command tree contains duplicate command paths.");
            }
        }

        ValidateCommandTree(manifest.Root, commands, byPath);
        ValidateFileSources(manifest.GlobalConfig);
        ValidateExitCodes(manifest.GlobalExitCodes, "global");
        ValidateOptionCollection(manifest.GlobalOptions, "global", manifest.GlobalConfig?.FileSources.Length > 0);

        foreach (var command in commands)
        {
            ValidateCommand(command, manifest.GlobalConfig?.FileSources.Length > 0);
        }

        ValidateEffectiveOptionCollisions(manifest, commands);
        ValidateCommandInvocations(manifest, commands, byPath);
    }

    private static void ValidateCommandTree(CanonicalCommand root, IReadOnlyList<CanonicalCommand> commands, Dictionary<string, CanonicalCommand> byPath)
    {
        if (!string.Equals(root.Path, "root", StringComparison.Ordinal))
        {
            throw new NormalizationException("INVALID_BASELINE", "The canonical manifest root path must be root.");
        }

        if (!root.Subcommands.SequenceEqual(root.Subcommands.OrderBy(command => command.Path, StringComparer.Ordinal)))
        {
            throw new NormalizationException("INVALID_BASELINE", "Canonical subcommands must be ordered by path.");
        }

        foreach (var command in commands)
        {
            ValidateCommandPath(command.Path);
            if (!ReferenceEquals(command, root))
            {
                if (command.Subcommands.Length > 0)
                {
                    throw new NormalizationException("INVALID_BASELINE", "Canonical command records must use the flat command list under root.");
                }

                var segments = command.Path.Split(" / ", StringSplitOptions.None);
                var parentPath = string.Join(" / ", segments[..^1]);
                if (!byPath.ContainsKey(parentPath))
                {
                    throw new NormalizationException("INVALID_BASELINE", "Every canonical command must have a materialized parent command.");
                }
            }
        }
    }

    private static void ValidateCommandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim() != path)
        {
            throw new NormalizationException("INVALID_BASELINE", "A canonical command path must be nonempty and trimmed.");
        }

        var segments = path.Split(" / ", StringSplitOptions.None);
        if (segments.Length == 0 || segments[0] != "root" || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment.Any(char.IsWhiteSpace) || segment.Contains('/')))
        {
            throw new NormalizationException("INVALID_BASELINE", "A canonical command path has an invalid hierarchy.");
        }
    }

    private static void ValidateFileSources(CanonicalGlobalConfig? config)
    {
        var formats = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in config?.FileSources ?? [])
        {
            if (source.Format is not ("json" or "toml" or "yaml") || !formats.Add(source.Format) || string.IsNullOrWhiteSpace(source.Path))
            {
                throw new NormalizationException("INVALID_BASELINE", "Canonical file sources must have unique supported formats and nonempty paths.");
            }
        }

        var sources = (config?.FileSources ?? []).ToArray();
        if (!sources.SequenceEqual(sources.OrderBy(source => source.Format, StringComparer.Ordinal)))
        {
            throw new NormalizationException("INVALID_BASELINE", "Canonical file sources must be ordered by format.");
        }
    }

    private static void ValidateExitCodes(IEnumerable<CanonicalExitCode> exitCodes, string subject)
    {
        var seen = new HashSet<int>();
        foreach (var exitCode in exitCodes)
        {
            if (!seen.Add(exitCode.Code) || !ExitCodeStatuses.Contains(exitCode.Status, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(exitCode.Summary))
            {
                throw new NormalizationException("INVALID_BASELINE", $"The canonical {subject} exit-code collection is not source-producible.");
            }
        }

        var array = exitCodes.ToArray();
        if (!array.SequenceEqual(array.OrderBy(exitCode => exitCode.Code)))
        {
            throw new NormalizationException("INVALID_BASELINE", $"The canonical {subject} exit-code collection must be ordered by code.");
        }
    }

    private static void ValidateCommand(CanonicalCommand command, bool hasFileSource)
    {
        if (command.Kind is not ("action" or "group"))
        {
            throw new NormalizationException("INVALID_BASELINE", "A canonical command kind must be action or group.");
        }

        if (command.Status is not null)
        {
            throw new NormalizationException("INVALID_BASELINE", "Alpha.14 canonical commands cannot carry status or deprecation state.");
        }

        ValidateStringCollection(command.Aliases, "command aliases", requireSorted: true);
        ValidateExitCodes(command.ExitCodes, command.Path);

        if (command.Kind == "group" && (command.Arguments.Length > 0 || command.Options.Length > 0))
        {
            throw new NormalizationException("OPENCLI_GROUP_COMMAND", "A group command cannot declare command-local arguments or flags.");
        }

        ValidateArgumentCollection(command.Arguments, command.Path, hasFileSource);
        ValidateOptionCollection(command.Options, command.Path, hasFileSource);
    }

    private static void ValidateArgumentCollection(IEnumerable<CanonicalArgument> arguments, string commandPath, bool hasFileSource)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            if (!names.Add(argument.Name))
            {
                throw new NormalizationException("OPENCLI_DUPLICATE_PARAMETER", "A canonical argument collection contains duplicate names.");
            }

            ValidateParameter(argument, commandPath, option: false, hasFileSource);
        }
    }

    private static void ValidateOptionCollection(IEnumerable<CanonicalOption> options, string commandPath, bool hasFileSource)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            if (!names.Add(OptionIdentity(option.Name)) || option.Aliases.Any(alias => !names.Add(OptionIdentity(alias))))
            {
                throw new NormalizationException("OPENCLI_DUPLICATE_PARAMETER", $"The canonical option collection at {commandPath} contains duplicate accepted invocation names.");
            }

            if (!option.Name.StartsWith("--", StringComparison.Ordinal) || option.Name.Length == 2 || option.Name[2] == '-' || option.Name.Any(char.IsWhiteSpace))
            {
                throw new NormalizationException("INVALID_BASELINE", "A canonical option name must use the normalized --name form.");
            }

            ValidateStringCollection(option.Aliases, "option aliases", requireSorted: true);
            ValidateParameter(option, commandPath, option: true, hasFileSource);
        }

        if (options is CanonicalOption[] array && !array.SequenceEqual(array.OrderBy(option => option.Name, StringComparer.Ordinal)))
        {
            throw new NormalizationException("INVALID_BASELINE", $"Canonical options at {commandPath} must be ordered by name.");
        }
    }

    private static void ValidateParameter(CanonicalParameter parameter, string commandPath, bool option, bool hasFileSource)
    {
        RequireNonEmpty(parameter.Name, "A canonical parameter name is required.");
        if (parameter.Name.Any(char.IsWhiteSpace))
        {
            throw new NormalizationException("INVALID_BASELINE", "Canonical parameter names cannot contain whitespace.");
        }

        if (parameter.Status is not null)
        {
            throw new NormalizationException("INVALID_BASELINE", "Alpha.14 canonical parameters cannot carry status or deprecation state.");
        }

        if (parameter.Required is null || !parameter.Variadic && (parameter.ArityMinimum is null || parameter.ArityMaximum != 1) ||
            parameter.ArityMinimum is < 0 or > 1 || parameter.ArityMaximum is < 0 ||
            parameter.ArityMinimum.HasValue && parameter.ArityMaximum.HasValue && parameter.ArityMinimum > parameter.ArityMaximum)
        {
            throw new NormalizationException("OPENCLI_ARITY", "Canonical parameter requiredness and arity are not source-producible.");
        }

        if (parameter.Variadic && option && parameter.Required == true)
        {
            throw new NormalizationException("OPENCLI_VARIADIC", "A variadic flag cannot be marked as required.");
        }

        if (option && !TypedDomain.IsSupportedType(parameter.Type) || !option && parameter.Type is not null && !TypedDomain.IsSupportedType(parameter.Type))
        {
            throw new NormalizationException("INVALID_BASELINE", "A canonical parameter type is unsupported.");
        }

        if (!option && (parameter.DefaultValue is not null || parameter.AlternativeSources.Length != 0))
        {
            throw new NormalizationException("OPENCLI_ARGUMENT_FIELD", "Canonical arguments cannot carry flag-only defaults or alternative sources.");
        }

        ValidateAllowedValues(parameter);
        ValidateChoices(parameter);
        if (option && parameter.DefaultValue is not null && !TypedDomain.Accepts(parameter.Type, parameter.DefaultValue))
        {
            throw new NormalizationException("OPENCLI_DEFAULT", "The canonical default value is not representable by its declared type.");
        }

        ValidateSources(parameter.AlternativeSources, hasFileSource);
    }

    private static void ValidateAllowedValues(CanonicalParameter parameter)
    {
        if (parameter.AllowedValues.Length != parameter.Choices.Length ||
            parameter.AllowedValues.Where((value, index) => !JsonNode.DeepEquals(value, parameter.Choices[index].Value)).Any())
        {
            throw new NormalizationException("INVALID_BASELINE", "Canonical AllowedValues must exactly match Choices[].Value.");
        }
    }

    private static void ValidateChoices(CanonicalParameter parameter)
    {
        var type = TypedDomain.NormalizeType(parameter.Type);
        foreach (var choice in parameter.Choices)
        {
            if (!TypedDomain.Accepts(type, choice.Value))
            {
                throw new NormalizationException("OPENCLI_CHOICE", $"A choice value is not representable by the declared {type} parameter type.");
            }
        }
    }

    private static void ValidateSources(IEnumerable<CanonicalAlternativeSource> sources, bool hasFileSource)
    {
        foreach (var source in sources)
        {
            if (source.Type is not ("$ENV" or "$FILE") || string.IsNullOrWhiteSpace(source.Property) || source.Type == "$FILE" && !hasFileSource)
            {
                throw new NormalizationException("OPENCLI_DEFAULT_SOURCE", "A canonical alternative source is invalid or lacks global file configuration.");
            }
        }
    }

    private static void ValidateEffectiveOptionCollisions(CanonicalManifest manifest, IReadOnlyList<CanonicalCommand> commands)
    {
        var globalNames = OptionNames(manifest.GlobalOptions);
        foreach (var command in commands)
        {
            var localNames = OptionNames(command.Options);
            if (globalNames.Overlaps(localNames))
            {
                throw new NormalizationException("OPENCLI_DUPLICATE_PARAMETER", $"The global and local option surfaces at {command.Path} contain a duplicate accepted invocation name.");
            }
        }
    }

    private static HashSet<string> OptionNames(IEnumerable<CanonicalOption> options) =>
        options.SelectMany(option => new[] { option.Name }.Concat(option.Aliases)).Select(OptionIdentity).ToHashSet(StringComparer.Ordinal);

    private static string OptionIdentity(string name) => name.TrimStart('-');

    private static void ValidateStringCollection(IEnumerable<string> values, string subject, bool requireSorted)
    {
        var array = values.ToArray();
        if (array.Any(string.IsNullOrWhiteSpace) || array.Any(value => value.Any(char.IsWhiteSpace)) || array.Distinct(StringComparer.Ordinal).Count() != array.Length)
        {
            throw new NormalizationException("INVALID_BASELINE", $"Canonical {subject} contain an empty, duplicated, or whitespace-containing value.");
        }

        if (requireSorted && !array.SequenceEqual(array.OrderBy(value => value, StringComparer.Ordinal)))
        {
            throw new NormalizationException("INVALID_BASELINE", $"Canonical {subject} must be sorted.");
        }
    }

    private static void RequireNonEmpty(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new NormalizationException("INVALID_BASELINE", message);
    }

    private static void ValidateCommandInvocations(CanonicalManifest manifest, IReadOnlyList<CanonicalCommand> commands, Dictionary<string, CanonicalCommand> byPath)
    {
        var invocationOwners = new Dictionary<string, CanonicalCommand>(StringComparer.Ordinal);
        var invocationCache = new Dictionary<CanonicalCommand, IReadOnlyList<string>>();

        foreach (var command in commands.OrderBy(command => command.Path.Count(value => value == '/')).ThenBy(command => command.Path, StringComparer.Ordinal))
        {
            var invocations = GetInvocations(command, manifest, byPath, invocationCache);
            foreach (var invocation in invocations)
            {
                if (invocationOwners.TryGetValue(invocation, out var existing) && !ReferenceEquals(existing, command))
                {
                    throw new NormalizationException("OPENCLI_DUPLICATE_INVOCATION", "The canonical command graph contains duplicate accepted invocation paths.");
                }

                invocationOwners[invocation] = command;
            }
        }
    }

    private static IReadOnlyList<string> GetInvocations(CanonicalCommand command, CanonicalManifest manifest, Dictionary<string, CanonicalCommand> byPath, Dictionary<CanonicalCommand, IReadOnlyList<string>> cache)
    {
        if (cache.TryGetValue(command, out var cached)) return cached;

        string[] parentInvocations;
        if (command.Path == "root")
        {
            var rootInvocations = new List<string> { "root", manifest.Info.Binary! };
            rootInvocations.AddRange(manifest.Root.Aliases);
            parentInvocations = rootInvocations.Distinct(StringComparer.Ordinal).ToArray();
        }
        else
        {
            var segments = command.Path.Split(" / ", StringSplitOptions.None);
            var parentPath = string.Join(" / ", segments[..^1]);
            parentInvocations = byPath.TryGetValue(parentPath, out var parent)
                ? GetInvocations(parent, manifest, byPath, cache).ToArray()
                : [parentPath];
        }

        var names = new[] { command.Path == "root" ? "root" : command.Path.Split(" / ")[^1] }
            .Concat(command.Path == "root" ? [] : command.Aliases)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = command.Path == "root"
            ? parentInvocations.Distinct(StringComparer.Ordinal).ToArray()
            : parentInvocations.SelectMany(parent => names.Select(name => parent + " / " + name)).Distinct(StringComparer.Ordinal).ToArray();
        cache[command] = result;
        return result;
    }

    private static IEnumerable<CanonicalCommand> Flatten(CanonicalCommand root)
    {
        yield return root;
        foreach (var child in root.Subcommands.SelectMany(Flatten)) yield return child;
    }
}
