using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

internal static class CanonicalInvariantValidator
{
    public static void Validate(CanonicalManifest manifest)
    {
        var commands = Flatten(manifest.Root).ToArray();
        var byPath = new Dictionary<string, CanonicalCommand>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (!byPath.TryAdd(command.Path, command))
            {
                throw new NormalizationException("OPENCLI_DUPLICATE_COMMAND_PATH", "The canonical command tree contains duplicate command paths.");
            }
        }

        if (!string.Equals(manifest.Root.Path, "root", StringComparison.Ordinal))
        {
            throw new NormalizationException("INVALID_BASELINE", "The canonical manifest root path must be root.");
        }

        var fileSources = manifest.GlobalConfig?.FileSources ?? [];
        foreach (var source in fileSources)
        {
            if (source.Format is not ("json" or "toml" or "yaml"))
            {
                throw new NormalizationException("INVALID_BASELINE", "A canonical file source format is unsupported.");
            }
        }

        foreach (var command in commands)
        {
            ValidateCommand(command, fileSources.Length > 0);
        }

        ValidateCommandInvocations(manifest, commands, byPath);
    }

    private static void ValidateCommand(CanonicalCommand command, bool hasFileSource)
    {
        var kind = command.Kind ?? "action";
        if (kind is not ("action" or "group"))
        {
            throw new NormalizationException("INVALID_BASELINE", "A canonical command kind must be action or group.");
        }

        if (kind == "group" && command.Path != "root" && (command.Arguments.Length > 0 || command.Options.Length > 0))
        {
            throw new NormalizationException("OPENCLI_GROUP_COMMAND", "A group command cannot declare command-local arguments or flags.");
        }

        var commandNames = new HashSet<string>(StringComparer.Ordinal);
        var seenOptional = false;
        var variadicArguments = 0;
        foreach (var argument in command.Arguments)
        {
            if (!commandNames.Add(argument.Name))
            {
                throw new NormalizationException("OPENCLI_DUPLICATE_PARAMETER", "A canonical argument collection contains duplicate names.");
            }

            if (seenOptional && argument.Required == true)
            {
                throw new NormalizationException("OPENCLI_ARGUMENT_ORDER", "A required positional argument cannot follow an optional positional argument.");
            }
            seenOptional |= argument.Required != true;

            if (argument.Variadic && ++variadicArguments > 1 || argument.Variadic && !ReferenceEquals(argument, command.Arguments[^1]))
            {
                throw new NormalizationException("OPENCLI_VARIADIC", "Only one variadic positional argument is allowed and it must be last.");
            }

            ValidateArity(argument);
            ValidateDefault(argument, false);
            ValidateSources(argument.AlternativeSources, hasFileSource);
        }

        var optionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in command.Options)
        {
            foreach (var acceptedName in AcceptedOptionNames(option))
            {
                if (!optionNames.Add(acceptedName))
                {
                    throw new NormalizationException("OPENCLI_DUPLICATE_PARAMETER", "A canonical option collection contains duplicate accepted invocation names.");
                }
            }

            if (option.Variadic && option.Required == true)
            {
                throw new NormalizationException("OPENCLI_VARIADIC", "A variadic flag cannot be marked as required.");
            }

            ValidateArity(option);
            ValidateDefault(option, true);
            ValidateSources(option.AlternativeSources, hasFileSource);
        }
    }

    private static IEnumerable<string> AcceptedOptionNames(CanonicalOption option)
    {
        yield return OptionIdentity(option.Name);
        foreach (var alias in option.Aliases)
        {
            if (!string.IsNullOrEmpty(alias)) yield return OptionIdentity(alias);
        }
    }

    private static string OptionIdentity(string name) => name.TrimStart('-');

    private static void ValidateArity(CanonicalParameter parameter)
    {
        if (parameter.ArityMinimum is < 0 or > int.MaxValue || parameter.ArityMaximum is < 0 or > int.MaxValue)
        {
            throw new NormalizationException("OPENCLI_ARITY", "Parameter item bounds must be non-negative integers.");
        }

        if (!parameter.Variadic &&
            (parameter.ArityMinimum is not null and not (0 or 1) || parameter.ArityMaximum is not null and not (0 or 1)))
        {
            throw new NormalizationException("OPENCLI_ARITY", "Parameter item bounds apply only when variadic is true.");
        }

        if (parameter.ArityMinimum.HasValue && parameter.ArityMaximum.HasValue && parameter.ArityMinimum > parameter.ArityMaximum)
        {
            throw new NormalizationException("OPENCLI_ARITY", "Parameter minimum items cannot exceed maximum items.");
        }
    }

    private static void ValidateDefault(CanonicalParameter parameter, bool option)
    {
        if (!option || parameter.DefaultValue is null) return;
        var type = NormalizeType(parameter.Type);
        var value = parameter.DefaultValue;
        var valid = type switch
        {
            "string" => value is JsonValue stringValue && stringValue.GetValueKind() is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
            "number" => ExactNumber.TryParse(value, allowNumericString: false, out _),
            "integer" => ExactNumber.TryParse(value, allowNumericString: false, out var integer) && integer.IsInteger,
            "boolean" => value is JsonValue booleanValue && booleanValue.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
            _ => false
        };

        if (!valid)
        {
            throw new NormalizationException("OPENCLI_DEFAULT", $"The default value is not representable by the declared {type} flag type.");
        }
    }

    private static void ValidateSources(IEnumerable<CanonicalAlternativeSource> sources, bool hasFileSource)
    {
        foreach (var source in sources)
        {
            if (source.Type == "$FILE" && !hasFileSource)
            {
                throw new NormalizationException("OPENCLI_DEFAULT_SOURCE", "A $FILE alternative source requires a global.config file source.");
            }
        }
    }

    private static void ValidateCommandInvocations(CanonicalManifest manifest, IReadOnlyList<CanonicalCommand> commands, IReadOnlyDictionary<string, CanonicalCommand> byPath)
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

    private static IReadOnlyList<string> GetInvocations(CanonicalCommand command, CanonicalManifest manifest, IReadOnlyDictionary<string, CanonicalCommand> byPath, Dictionary<CanonicalCommand, IReadOnlyList<string>> cache)
    {
        if (cache.TryGetValue(command, out var cached)) return cached;

        string[] parentInvocations;
        if (command.Path == "root")
        {
            var rootInvocations = new List<string> { "root" };
            if (manifest.Info.Binary is not null) rootInvocations.Add(manifest.Info.Binary);
            rootInvocations.AddRange(manifest.Root.Aliases);
            parentInvocations = rootInvocations.ToArray();
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

    private static string NormalizeType(string? type) => type?.Trim().ToLowerInvariant() switch { null or "" => "string", var value => value };
}
