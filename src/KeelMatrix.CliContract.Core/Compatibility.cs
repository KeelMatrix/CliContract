using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

public sealed record CompatibilityFinding(string Code, string Category, string Path, string Message);

public sealed class CompatibilityException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class CompatibilityResult
{
    public IReadOnlyList<CompatibilityFinding> Findings { get; }

    public CompatibilityResult(IEnumerable<CompatibilityFinding> findings)
    {
        Findings = findings
            .OrderBy(finding => finding.Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .ToArray();
    }
}

public static class CompatibilityAnalyzer
{
    public static CompatibilityResult Compare(CanonicalManifest baseline, CanonicalManifest current)
    {
        if (baseline.SchemaVersion != current.SchemaVersion)
        {
            throw new CompatibilityException("BASELINE_VERSION_MISMATCH", "The baseline and current manifest schema versions do not match.");
        }

        if (!string.Equals(baseline.Adapter, current.Adapter, StringComparison.Ordinal) ||
            !string.Equals(baseline.SourceVersion, current.SourceVersion, StringComparison.Ordinal))
        {
            throw new CompatibilityException("BASELINE_VERSION_MISMATCH", "The baseline and current descriptions use different supported input versions.");
        }

        var baselineGraph = InvocationNameGraph.Create(baseline);
        var currentGraph = InvocationNameGraph.Create(current);
        var findings = new List<CompatibilityFinding>();

        CompareInfo(baseline.Info, current.Info, findings);
        CompareRootInvocationNames(baseline, current, findings);
        CompareExitCodes(baseline.GlobalExitCodes, current.GlobalExitCodes, "root", findings);
        CompareGlobalConfig(baseline.GlobalConfig, current.GlobalConfig, findings);
        CompareCommand(baseline.Root, current.Root, findings);

        var matchedCurrentCommands = new HashSet<CanonicalCommand>();
        foreach (var baselineCommand in baselineGraph.Commands.OrderBy(command => command.Path, StringComparer.Ordinal))
        {
            var oldInvocations = baselineGraph.GetInvocations(baselineCommand);
            var candidates = oldInvocations
                .Where(currentGraph.ByInvocation.ContainsKey)
                .Select(invocation => currentGraph.ByInvocation[invocation])
                .Distinct()
                .ToArray();

            if (candidates.Length == 0)
            {
                findings.Add(new CompatibilityFinding("KMCLI103", "breaking", baselineCommand.Path, "Removed command."));
                continue;
            }

            if (candidates.Length > 1)
            {
                throw new CompatibilityException("OPENCLI_DUPLICATE_INVOCATION", "A baseline invocation is accepted by multiple current commands.");
            }

            var currentCommand = candidates[0];
            matchedCurrentCommands.Add(currentCommand);
            CompareInvocationNames(oldInvocations, currentGraph.GetInvocations(currentCommand), baselineCommand.Path, "callable alias", "KMCLI104", "KMCLI004", findings);
            CompareCommand(baselineCommand, currentCommand, findings);
        }

        foreach (var currentCommand in currentGraph.Commands
                     .Where(command => !matchedCurrentCommands.Contains(command))
                     .OrderBy(command => command.Path, StringComparer.Ordinal))
        {
            findings.Add(new CompatibilityFinding("KMCLI001", "info", currentCommand.Path, "Added command."));
        }

        return new CompatibilityResult(findings);
    }

    private static void CompareInfo(CanonicalInfo baseline, CanonicalInfo current, List<CompatibilityFinding> findings)
    {
        CompareInformationalValue(baseline.Title, current.Title, "info / title", "Info title", findings);
        CompareInformationalValue(baseline.Summary, current.Summary, "info / summary", "Info summary", findings);
        CompareInformationalValue(baseline.Description, current.Description, "info / description", "Info description", findings);
        CompareInformationalValue(baseline.Version, current.Version, "info / version", "Info version", findings);

        CompareLicense(baseline.License, current.License, findings);
        CompareContact(baseline.Contact, current.Contact, findings);
        CompareInstall(baseline.Install, current.Install, findings);
    }

    private static void CompareLicense(CanonicalLicense? baseline, CanonicalLicense? current, List<CompatibilityFinding> findings)
    {
        CompareInformationalValue(baseline?.Name, current?.Name, "info / license / name", "License name", findings);
        CompareInformationalValue(baseline?.SpdxId, current?.SpdxId, "info / license / spdxId", "License SPDX identifier", findings);
        CompareInformationalValue(baseline?.Url, current?.Url, "info / license / url", "License URL", findings);
    }

    private static void CompareContact(CanonicalContact? baseline, CanonicalContact? current, List<CompatibilityFinding> findings)
    {
        CompareInformationalValue(baseline?.Name, current?.Name, "info / contact / name", "Contact name", findings);
        CompareInformationalValue(baseline?.Email, current?.Email, "info / contact / email", "Contact email", findings);
        CompareInformationalValue(baseline?.Url, current?.Url, "info / contact / url", "Contact URL", findings);
    }

    private static void CompareInstall(CanonicalInstall[] baseline, CanonicalInstall[] current, List<CompatibilityFinding> findings)
    {
        var count = Math.Max(baseline.Length, current.Length);
        for (var index = 0; index < count; index++)
        {
            var oldInstall = index < baseline.Length ? baseline[index] : null;
            var newInstall = index < current.Length ? current[index] : null;
            var path = "info / install / " + index;
            CompareInformationalValue(oldInstall?.Name, newInstall?.Name, path + " / name", "Install name", findings);
            CompareInformationalValue(oldInstall?.Command, newInstall?.Command, path + " / command", "Install command", findings);
            CompareInformationalValue(oldInstall?.Url, newInstall?.Url, path + " / url", "Install URL", findings);
            CompareInformationalValue(oldInstall?.Description, newInstall?.Description, path + " / description", "Install description", findings);
        }
    }

    private static void CompareChoiceDescriptions(CanonicalChoice[] baseline, CanonicalChoice[] current, string path, List<CompatibilityFinding> findings)
    {
        var matchedCurrent = new bool[current.Length];
        foreach (var oldChoice in baseline)
        {
            var currentIndex = -1;
            for (var index = 0; index < current.Length; index++)
            {
                if (!matchedCurrent[index] && JsonNode.DeepEquals(oldChoice.Value, current[index].Value))
                {
                    currentIndex = index;
                    break;
                }
            }

            CanonicalChoice? newChoice = null;
            if (currentIndex >= 0)
            {
                matchedCurrent[currentIndex] = true;
                newChoice = current[currentIndex];
            }

            CompareInformationalValue(oldChoice.Description, newChoice?.Description, path + " / choices", "Choice description", findings);
        }

        for (var index = 0; index < current.Length; index++)
        {
            if (!matchedCurrent[index])
            {
                CompareInformationalValue(null, current[index].Description, path + " / choices", "Choice description", findings);
            }
        }
    }

    private static void CompareInformationalValue(string? baseline, string? current, string path, string label, List<CompatibilityFinding> findings)
    {
        if (!string.Equals(baseline, current, StringComparison.Ordinal))
        {
            findings.Add(new CompatibilityFinding("KMCLI005", "info", path, $"{label} changed."));
        }
    }

    private static void CompareRootInvocationNames(CanonicalManifest baseline, CanonicalManifest current, List<CompatibilityFinding> findings)
    {
        var oldNames = RootInvocationNames(baseline).ToHashSet(StringComparer.Ordinal);
        var newNames = RootInvocationNames(current).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in oldNames.Except(newNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var code = string.Equals(removed, baseline.Info.Binary, StringComparison.Ordinal) ? "KMCLI110" : "KMCLI104";
            var message = code == "KMCLI110" ? "CLI binary invocation name changed." : $"Removed callable alias '{removed}'.";
            findings.Add(new CompatibilityFinding(code, "breaking", "root", message));
        }

        foreach (var added in newNames.Except(oldNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new CompatibilityFinding("KMCLI004", "info", "root", $"Added callable alias '{added}'."));
        }
    }

    private static IEnumerable<string> RootInvocationNames(CanonicalManifest manifest)
    {
        if (manifest.Info.Binary is not null) yield return manifest.Info.Binary;
        foreach (var alias in manifest.Root.Aliases) yield return alias;
    }

    private static void CompareGlobalConfig(CanonicalGlobalConfig? baseline, CanonicalGlobalConfig? current, List<CompatibilityFinding> findings)
    {
        var oldSources = baseline?.FileSources ?? [];
        var newSources = current?.FileSources ?? [];
        var oldByFormat = oldSources.ToDictionary(source => source.Format, StringComparer.Ordinal);
        var newByFormat = newSources.ToDictionary(source => source.Format, StringComparer.Ordinal);
        if (oldByFormat.Count != newByFormat.Count || oldByFormat.Any(pair => !newByFormat.TryGetValue(pair.Key, out var currentSource) || !string.Equals(pair.Value.Path, currentSource.Path, StringComparison.Ordinal)))
        {
            findings.Add(new CompatibilityFinding("KMCLI204", "warning", "root", "Represented global file-source configuration changed."));
        }
    }

    private static void CompareCommand(CanonicalCommand baseline, CanonicalCommand current, List<CompatibilityFinding> findings)
    {
        var path = baseline.Path;
        var baselineKind = baseline.Kind ?? "action";
        var currentKind = current.Kind ?? "action";
        if (!string.Equals(baselineKind, currentKind, StringComparison.Ordinal))
        {
            var isCallableSurfaceRemoved = baselineKind == "action" && currentKind == "group";
            findings.Add(new CompatibilityFinding("KMCLI111", isCallableSurfaceRemoved ? "breaking" : "info", path, isCallableSurfaceRemoved ? "Callable command became a non-runnable group." : "Command runnable kind changed."));
        }

        if (baseline.Hidden != current.Hidden)
        {
            findings.Add(new CompatibilityFinding("KMCLI006", "info", path, "Command visibility changed."));
        }

        CompareText(baseline.Summary, current.Summary, path, "summary", findings);
        CompareText(baseline.Description, current.Description, path, "description", findings);
        CompareStatus(baseline.Status, current.Status, path, findings);
        CompareExitCodes(baseline.ExitCodes, current.ExitCodes, path, findings);
        CompareExamples(baseline.Examples, current.Examples, path, findings);
        CompareOptions(baseline, current, findings);
        CompareArguments(baseline, current, findings);
    }

    private static void CompareOptions(CanonicalCommand baseline, CanonicalCommand current, List<CompatibilityFinding> findings)
    {
        var currentMap = ToUniqueOptionMap(current.Options, current.Path);
        var matched = new HashSet<CanonicalOption>();
        foreach (var oldOption in baseline.Options.OrderBy(option => option.Name, StringComparer.Ordinal))
        {
            var candidates = OptionInvocationNames(oldOption)
                .Where(currentMap.ContainsKey)
                .Select(name => currentMap[name])
                .Distinct()
                .ToArray();
            if (candidates.Length == 0)
            {
                findings.Add(new CompatibilityFinding("KMCLI101", "breaking", baseline.Path + " / " + oldOption.Name, "Removed option."));
                continue;
            }

            if (candidates.Length > 1)
            {
                throw new CompatibilityException("OPENCLI_DUPLICATE_PARAMETER", $"The current option collection at {current.Path} accepts one invocation name for multiple options.");
            }

            var newOption = candidates[0];
            matched.Add(newOption);
            var optionPath = baseline.Path + " / " + oldOption.Name;
            CompareInvocationNames(OptionInvocationNames(oldOption), OptionInvocationNames(newOption), optionPath, "alias", "KMCLI104", "KMCLI004", findings);
            CompareParameter(oldOption, newOption, optionPath, "option", findings);
        }

        foreach (var newOption in current.Options.Where(option => !matched.Contains(option)).OrderBy(option => option.Name, StringComparer.Ordinal))
        {
            var path = current.Path + " / " + newOption.Name;
            var required = newOption.Required == true;
            findings.Add(new CompatibilityFinding(required ? "KMCLI108" : "KMCLI002", required ? "breaking" : "info", path, required ? "Added required option." : "Added option."));
        }
    }

    private static void CompareArguments(CanonicalCommand baseline, CanonicalCommand current, List<CompatibilityFinding> findings)
    {
        var oldNames = baseline.Arguments.Select(argument => argument.Name).ToArray();
        var newNames = current.Arguments.Select(argument => argument.Name).ToArray();
        if (HasPositionalSlotShift(oldNames, newNames))
        {
            findings.Add(new CompatibilityFinding("KMCLI109", "breaking", baseline.Path, "Positional argument slot changed."));
        }

        var currentMap = ToUniqueArgumentMap(current.Arguments, current.Path);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var oldArgument in baseline.Arguments.OrderBy(argument => argument.Name, StringComparer.Ordinal))
        {
            if (!currentMap.TryGetValue(oldArgument.Name, out var newArgument))
            {
                findings.Add(new CompatibilityFinding("KMCLI102", "breaking", baseline.Path + " / " + oldArgument.Name, "Removed argument."));
                continue;
            }

            matched.Add(oldArgument.Name);
            CompareParameter(oldArgument, newArgument, baseline.Path + " / " + oldArgument.Name, "argument", findings);
        }

        foreach (var newArgument in current.Arguments.Where(argument => !matched.Contains(argument.Name)).OrderBy(argument => argument.Name, StringComparer.Ordinal))
        {
            var path = current.Path + " / " + newArgument.Name;
            var required = newArgument.Required == true;
            findings.Add(new CompatibilityFinding(required ? "KMCLI108" : "KMCLI003", required ? "breaking" : "info", path, required ? "Added required argument." : "Added argument."));
        }
    }

    private static Dictionary<string, CanonicalOption> ToUniqueOptionMap(IEnumerable<CanonicalOption> options, string commandPath)
    {
        var result = new Dictionary<string, CanonicalOption>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            foreach (var name in OptionInvocationNames(option))
            {
                if (!result.TryAdd(name, option) && !ReferenceEquals(result[name], option))
                {
                    throw new CompatibilityException("OPENCLI_DUPLICATE_PARAMETER", $"The canonical option collection at {commandPath} contains duplicate accepted invocation names.");
                }
            }
        }

        return result;
    }

    private static Dictionary<string, CanonicalArgument> ToUniqueArgumentMap(IEnumerable<CanonicalArgument> arguments, string commandPath)
    {
        var result = new Dictionary<string, CanonicalArgument>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            if (!result.TryAdd(argument.Name, argument))
            {
                throw new CompatibilityException("OPENCLI_DUPLICATE_PARAMETER", $"The canonical argument collection at {commandPath} contains duplicate names.");
            }
        }

        return result;
    }

    private static IEnumerable<string> OptionInvocationNames(CanonicalOption option)
    {
        yield return option.Name;
        foreach (var alias in option.Aliases) yield return alias;
    }

    private static void CompareParameter(CanonicalParameter baseline, CanonicalParameter current, string path, string kind, List<CompatibilityFinding> findings)
    {
        if (current.Required == true && baseline.Required != true)
        {
            findings.Add(new CompatibilityFinding("KMCLI105", "breaking", path, $"{Capitalize(kind)} changed from optional to required."));
        }

        if (baseline is CanonicalArgument oldArgument && current is CanonicalArgument newArgument && oldArgument.Passthrough != newArgument.Passthrough)
        {
            findings.Add(new CompatibilityFinding("KMCLI112", oldArgument.Passthrough ? "breaking" : "info", path, oldArgument.Passthrough ? "Argument no longer accepts post-- forms as passthrough." : "Argument accepts post-- forms as passthrough."));
        }

        if (baseline.Variadic != current.Variadic || IsArityNarrowed(baseline, current))
        {
            if (!baseline.Variadic && current.Variadic)
            {
                findings.Add(new CompatibilityFinding("KMCLI106", "info", path, $"Accepted {kind} arity widened."));
            }
            else if (IsArityNarrowed(baseline, current))
            {
                findings.Add(new CompatibilityFinding("KMCLI106", "breaking", path, $"Accepted {kind} arity narrowed."));
            }
        }

        if (!baseline.AlternativeSources.Select(SourceKey).SequenceEqual(current.AlternativeSources.Select(SourceKey), StringComparer.Ordinal))
        {
            findings.Add(new CompatibilityFinding("KMCLI204", "warning", path, "Represented default-source resolution changed."));
        }

        var domain = CompareDomain(baseline, current);
        if (domain == DomainChange.Narrowed)
        {
            findings.Add(new CompatibilityFinding("KMCLI107", "breaking", path, $"Accepted {kind} type or domain narrowed."));
        }
        else if (domain == DomainChange.Changed)
        {
            findings.Add(new CompatibilityFinding("KMCLI203", "warning", path, $"Represented {kind} type or domain changed."));
        }

        if (!JsonNode.DeepEquals(baseline.DefaultValue, current.DefaultValue))
        {
            findings.Add(new CompatibilityFinding("KMCLI201", "warning", path, "Default value changed."));
        }

        if (baseline.Hint != current.Hint || baseline.Hidden != current.Hidden)
        {
            findings.Add(new CompatibilityFinding("KMCLI006", "info", path, "Parameter help visibility changed."));
        }

        CompareChoiceDescriptions(baseline.Choices, current.Choices, path, findings);
        CompareText(baseline.Summary, current.Summary, path, "summary", findings);
        CompareText(baseline.Description, current.Description, path, "description", findings);
        CompareStatus(baseline.Status, current.Status, path, findings);
    }

    private static string SourceKey(CanonicalAlternativeSource source) => source.Type + "=" + source.Property;

    private static void CompareInvocationNames(IEnumerable<string> baseline, IEnumerable<string> current, string path, string kind, string removedCode, string addedCode, List<CompatibilityFinding> findings)
    {
        var oldNames = baseline.ToHashSet(StringComparer.Ordinal);
        var newNames = current.ToHashSet(StringComparer.Ordinal);
        foreach (var removed in oldNames.Except(newNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new CompatibilityFinding(removedCode, "breaking", path, $"Removed {kind} '{removed}'."));
        }

        foreach (var added in newNames.Except(oldNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new CompatibilityFinding(addedCode, "info", path, $"Added {kind} '{added}'."));
        }
    }

    private static void CompareExitCodes(IEnumerable<CanonicalExitCode> baseline, IEnumerable<CanonicalExitCode> current, string path, List<CompatibilityFinding> findings)
    {
        var oldByCode = baseline.ToDictionary(exitCode => exitCode.Code);
        var newByCode = current.ToDictionary(exitCode => exitCode.Code);
        var changed = oldByCode.Count != newByCode.Count || oldByCode.Any(pair => !newByCode.TryGetValue(pair.Key, out var value) || !string.Equals(pair.Value.Status, value.Status, StringComparison.Ordinal) || !string.Equals(pair.Value.Summary, value.Summary, StringComparison.Ordinal) || !string.Equals(pair.Value.Description, value.Description, StringComparison.Ordinal));
        if (changed)
        {
            findings.Add(new CompatibilityFinding("KMCLI205", "warning", path, "Represented exit-code contract changed."));
        }
    }

    private static void CompareExamples(IEnumerable<CanonicalExample> baseline, IEnumerable<CanonicalExample> current, string path, List<CompatibilityFinding> findings)
    {
        var oldValues = baseline.Select(example => (example.Title ?? string.Empty) + "\u001f" + example.Content).ToHashSet(StringComparer.Ordinal);
        var newValues = current.Select(example => (example.Title ?? string.Empty) + "\u001f" + example.Content).ToHashSet(StringComparer.Ordinal);
        if (!oldValues.SetEquals(newValues))
        {
            findings.Add(new CompatibilityFinding("KMCLI006", "info", path, "Command examples changed."));
        }
    }

    private static bool HasPositionalSlotShift(IReadOnlyList<string> baseline, IReadOnlyList<string> current)
    {
        for (var index = 0; index < baseline.Count; index++)
        {
            var currentIndex = -1;
            for (var candidate = 0; candidate < current.Count; candidate++)
            {
                if (string.Equals(current[candidate], baseline[index], StringComparison.Ordinal))
                {
                    currentIndex = candidate;
                    break;
                }
            }
            if (currentIndex >= 0 && currentIndex != index) return true;
        }

        return false;
    }

    private static bool IsArityNarrowed(CanonicalParameter baseline, CanonicalParameter current)
    {
        var oldMinimum = baseline.ArityMinimum ?? 0;
        var newMinimum = current.ArityMinimum ?? 0;
        var oldMaximum = baseline.ArityMaximum ?? int.MaxValue;
        var newMaximum = current.ArityMaximum ?? int.MaxValue;
        var minimumNarrowed = newMinimum > oldMinimum && !(baseline.Required != true && current.Required == true && oldMinimum == 0 && newMinimum == 1);
        return minimumNarrowed || newMaximum < oldMaximum || baseline.Variadic && !current.Variadic;
    }

    private static DomainChange CompareDomain(CanonicalParameter baseline, CanonicalParameter current)
    {
        var oldDomain = AcceptedDomain.Create(baseline);
        var newDomain = AcceptedDomain.Create(current);
        var oldSubset = oldDomain.IsSubsetOf(newDomain);
        var newSubset = newDomain.IsSubsetOf(oldDomain);
        if (!oldSubset) return DomainChange.Narrowed;
        return newSubset ? DomainChange.Unchanged : DomainChange.Changed;
    }

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static void CompareText(string? baseline, string? current, string path, string kind, List<CompatibilityFinding> findings)
    {
        if (!string.Equals(baseline, current, StringComparison.Ordinal)) findings.Add(new CompatibilityFinding("KMCLI005", "info", path, $"{Capitalize(kind)} changed."));
    }

    private static void CompareStatus(string? baseline, string? current, string path, List<CompatibilityFinding> findings)
    {
        if (!string.Equals(baseline, current, StringComparison.Ordinal)) findings.Add(new CompatibilityFinding("KMCLI202", "warning", path, "Status or deprecation state changed."));
    }

    private enum DomainChange { Unchanged, Changed, Narrowed }

    private sealed class AcceptedDomain
    {
        private readonly string _type;
        private readonly JsonNode[]? _choices;

        private AcceptedDomain(string type, JsonNode[]? choices) { _type = type; _choices = choices; }

        public static AcceptedDomain Create(CanonicalParameter parameter) => new(NormalizeType(parameter.Type), parameter.Choices.Length == 0 ? null : parameter.Choices.Select(choice => choice.Value).ToArray());

        public bool IsSubsetOf(AcceptedDomain other)
        {
            if (_choices is null) return other._choices is null && BaseSubset(_type, other._type);
            if (other._choices is null) return _choices.All(value => Accepts(other._type, value));
            return _choices.All(value => other._choices.Any(candidate => EquivalentValue(other._type, value, candidate) && Accepts(other._type, candidate)));
        }

        private static bool BaseSubset(string oldType, string newType) => string.Equals(oldType, newType, StringComparison.Ordinal) || oldType == "integer" && newType == "number" || oldType is "integer" or "number" or "boolean" && newType == "string";

        private static bool Accepts(string type, JsonNode value) => type switch
        {
            "string" => value.GetValueKind() is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
            "number" => ExactNumber.TryParse(value, allowNumericString: true, out _),
            "integer" => ExactNumber.TryParse(value, allowNumericString: true, out var integer) && integer.IsInteger,
            "boolean" => value.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False || value is JsonValue booleanValue && booleanValue.TryGetValue<string>(out var booleanText) && booleanText is "true" or "false",
            _ => false
        };

        private static bool EquivalentValue(string type, JsonNode left, JsonNode right) => type switch
        {
            "number" or "integer" => ExactNumber.TryParse(left, allowNumericString: true, out var leftNumber) &&
                ExactNumber.TryParse(right, allowNumericString: true, out var rightNumber) &&
                leftNumber.EqualsValue(rightNumber),
            "boolean" => BooleanValue(left) == BooleanValue(right),
            "string" => StringValue(left) == StringValue(right),
            _ => JsonNode.DeepEquals(left, right)
        };

        private static string StringValue(JsonNode value) => value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => ExactNumber.TryParse(value, allowNumericString: false, out var number) ? number.ToCanonicalString() : value.ToJsonString(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            _ => value.ToJsonString()
        };

        private static string? BooleanValue(JsonNode value) => value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            System.Text.Json.JsonValueKind.String when value.GetValue<string>() is "true" or "false" => value.GetValue<string>(),
            _ => null
        };

        private static string NormalizeType(string? type) => type?.Trim().ToLowerInvariant() switch { null or "" => "string", var value => value };
    }

    private sealed class InvocationNameGraph
    {
        public required IReadOnlyList<CanonicalCommand> Commands { get; init; }
        public required IReadOnlyDictionary<string, CanonicalCommand> ByInvocation { get; init; }
        private IReadOnlyDictionary<CanonicalCommand, IReadOnlyList<string>> Paths { get; init; } = new Dictionary<CanonicalCommand, IReadOnlyList<string>>();
        public IReadOnlyList<string> GetInvocations(CanonicalCommand command) => Paths[command];

        public static InvocationNameGraph Create(CanonicalManifest manifest)
        {
            var commands = Flatten(manifest.Root).ToArray();
            var byPath = new Dictionary<string, CanonicalCommand>(StringComparer.Ordinal);
            foreach (var command in commands)
            {
                if (!byPath.TryAdd(command.Path, command)) throw new CompatibilityException("OPENCLI_DUPLICATE_COMMAND_PATH", $"The canonical command collection contains duplicate command path '{command.Path}'.");
            }
            var paths = new Dictionary<CanonicalCommand, IReadOnlyList<string>>();
            var byInvocation = new Dictionary<string, CanonicalCommand>(StringComparer.Ordinal);
            foreach (var command in commands.OrderBy(command => command.Path.Count(character => character == '/')).ThenBy(command => command.Path, StringComparer.Ordinal))
            {
                var invocations = command.Path == "root" ? ["root"] : BuildChildPaths(command, byPath, paths);
                paths[command] = invocations;
                foreach (var invocation in invocations)
                {
                    if (byInvocation.TryGetValue(invocation, out var existing) && !ReferenceEquals(existing, command)) throw new CompatibilityException("OPENCLI_DUPLICATE_INVOCATION", "The canonical command graph contains duplicate accepted invocation paths.");
                    byInvocation[invocation] = command;
                }
            }

            return new InvocationNameGraph { Commands = commands.Where(command => command.Path != "root").ToArray(), ByInvocation = byInvocation, Paths = paths };
        }

        private static string[] BuildChildPaths(CanonicalCommand command, Dictionary<string, CanonicalCommand> byPath, Dictionary<CanonicalCommand, IReadOnlyList<string>> paths)
        {
            var segments = command.Path.Split(" / ", StringSplitOptions.None);
            var parentPath = string.Join(" / ", segments[..^1]);
            if (!byPath.TryGetValue(parentPath, out var parent) || !paths.TryGetValue(parent, out var parentPaths))
            {
                parentPaths = [parentPath];
            }
            var names = new[] { segments[^1] }.Concat(command.Aliases).Distinct(StringComparer.Ordinal);
            return parentPaths.SelectMany(parentInvocation => names.Select(name => parentInvocation + " / " + name)).Distinct(StringComparer.Ordinal).ToArray();
        }

        private static IEnumerable<CanonicalCommand> Flatten(CanonicalCommand root)
        {
            yield return root;
            foreach (var child in root.Subcommands.SelectMany(Flatten)) yield return child;
        }
    }
}
