using System.Text.Json;
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

        var baselineDuplicate = CanonicalCommandValidation.FindDuplicatePath(baseline.Root);
        var currentDuplicate = CanonicalCommandValidation.FindDuplicatePath(current.Root);
        if (baselineDuplicate is not null || currentDuplicate is not null)
        {
            var duplicate = baselineDuplicate ?? currentDuplicate;
            throw new CompatibilityException(
                "OPENCLI_DUPLICATE_COMMAND_PATH",
                $"The canonical command tree contains duplicate command path '{duplicate}'.");
        }

        var findings = new List<CompatibilityFinding>();
        CompareCommand(baseline.Root, current.Root, findings);
        return new CompatibilityResult(findings);
    }

    private static void CompareCommand(CanonicalCommand baseline, CanonicalCommand current, List<CompatibilityFinding> findings)
    {
        CompareAliases(baseline.Aliases, current.Aliases, baseline.Path, "callable alias", "KMCLI104", "KMCLI004", findings);
        CompareText(baseline.Summary, current.Summary, baseline.Path, "summary", findings);
        CompareText(baseline.Description, current.Description, baseline.Path, "description", findings);
        CompareStatus(baseline.Status, current.Status, baseline.Path, findings);

        var baselineOptions = ToUniqueMap(baseline.Options, "option", baseline.Path);
        var currentOptions = ToUniqueMap(current.Options, "option", current.Path);
        CompareParameters(baselineOptions, currentOptions, baseline.Path, "option", findings);

        var baselineArgumentNames = baseline.Arguments.Select(argument => argument.Name).ToArray();
        var currentArgumentNames = current.Arguments.Select(argument => argument.Name).ToArray();
        if (baselineArgumentNames.Length == currentArgumentNames.Length &&
            !baselineArgumentNames.SequenceEqual(currentArgumentNames, StringComparer.Ordinal) &&
            baselineArgumentNames.Order(StringComparer.Ordinal).SequenceEqual(currentArgumentNames.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            findings.Add(new CompatibilityFinding("KMCLI109", "breaking", baseline.Path, "Positional argument order changed."));
        }

        var baselineArguments = ToUniqueMap(baseline.Arguments, "argument", baseline.Path);
        var currentArguments = ToUniqueMap(current.Arguments, "argument", current.Path);
        CompareParameters(baselineArguments, currentArguments, baseline.Path, "argument", findings);

        var baselineCommands = ToUniqueCommandMap(baseline.Subcommands);
        var currentCommands = ToUniqueCommandMap(current.Subcommands);
        foreach (var removed in baselineCommands.Keys.Except(currentCommands.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new CompatibilityFinding("KMCLI103", "breaking", removed, "Removed command."));
        }

        foreach (var added in currentCommands.Keys.Except(baselineCommands.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new CompatibilityFinding("KMCLI001", "info", added, "Added command."));
        }

        foreach (var path in baselineCommands.Keys.Intersect(currentCommands.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            CompareCommand(baselineCommands[path], currentCommands[path], findings);
        }
    }

    private static Dictionary<string, TParameter> ToUniqueMap<TParameter>(
        IEnumerable<TParameter> parameters,
        string kind,
        string commandPath)
        where TParameter : CanonicalParameter
    {
        var result = new Dictionary<string, TParameter>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (!result.TryAdd(parameter.Name, parameter))
            {
                throw new CompatibilityException(
                    "OPENCLI_DUPLICATE_PARAMETER",
                    $"The canonical {kind} collection at {commandPath} contains duplicate normalized names.");
            }
        }

        return result;
    }

    private static Dictionary<string, CanonicalCommand> ToUniqueCommandMap(IEnumerable<CanonicalCommand> commands)
    {
        var result = new Dictionary<string, CanonicalCommand>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (!result.TryAdd(command.Path, command))
            {
                throw new CompatibilityException(
                    "OPENCLI_DUPLICATE_COMMAND_PATH",
                    $"The canonical command collection contains duplicate command path '{command.Path}'.");
            }
        }

        return result;
    }

    private static void CompareParameters<TParameter>(
        IReadOnlyDictionary<string, TParameter> baseline,
        IReadOnlyDictionary<string, TParameter> current,
        string commandPath,
        string kind,
        List<CompatibilityFinding> findings)
        where TParameter : CanonicalParameter
    {
        foreach (var removed in baseline.Keys.Except(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var path = commandPath + " / " + removed;
            var code = kind == "option" ? "KMCLI101" : "KMCLI102";
            findings.Add(new CompatibilityFinding(code, "breaking", path, $"Removed {kind}."));
        }

        foreach (var added in current.Keys.Except(baseline.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var parameter = current[added];
            var path = commandPath + " / " + added;
            if (parameter.Required == true)
            {
                findings.Add(new CompatibilityFinding("KMCLI108", "breaking", path, $"Added required {kind}."));
            }
            else
            {
                var code = kind == "option" ? "KMCLI002" : "KMCLI003";
                findings.Add(new CompatibilityFinding(code, "info", path, $"Added {kind}."));
            }
        }

        foreach (var name in baseline.Keys.Intersect(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var oldValue = baseline[name];
            var newValue = current[name];
            var path = commandPath + " / " + name;

            if (oldValue is CanonicalOption oldOption && newValue is CanonicalOption newOption)
            {
                CompareAliases(oldOption.Aliases, newOption.Aliases, path, "alias", "KMCLI104", "KMCLI004", findings);
            }

            if (newValue.Required == true && oldValue.Required != true)
            {
                findings.Add(new CompatibilityFinding("KMCLI105", "breaking", path, $"{Capitalize(kind)} changed from optional to required."));
            }

            if (IsArityNarrowed(oldValue, newValue))
            {
                findings.Add(new CompatibilityFinding("KMCLI106", "breaking", path, $"Accepted {kind} arity narrowed."));
            }

            var domain = CompareDomain(oldValue, newValue);
            if (domain == DomainChange.Narrowed)
            {
                findings.Add(new CompatibilityFinding("KMCLI107", "breaking", path, $"Accepted {kind} type or domain narrowed."));
            }
            else if (domain == DomainChange.Changed)
            {
                findings.Add(new CompatibilityFinding("KMCLI203", "warning", path, $"Represented {kind} type or domain changed."));
            }

            if (!JsonNode.DeepEquals(oldValue.DefaultValue, newValue.DefaultValue))
            {
                findings.Add(new CompatibilityFinding("KMCLI201", "warning", path, "Default value changed."));
            }

            CompareText(oldValue.Summary, newValue.Summary, path, "summary", findings);
            CompareText(oldValue.Description, newValue.Description, path, "description", findings);
            CompareStatus(oldValue.Status, newValue.Status, path, findings);
        }
    }

    private static void CompareAliases(
        IEnumerable<string> baseline,
        IEnumerable<string> current,
        string path,
        string kind,
        string removedCode,
        string addedCode,
        List<CompatibilityFinding> findings)
    {
        var oldAliases = baseline.ToHashSet(StringComparer.Ordinal);
        var newAliases = current.ToHashSet(StringComparer.Ordinal);
        foreach (var alias in oldAliases.Except(newAliases, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new CompatibilityFinding(removedCode, "breaking", path, $"Removed {kind} '{alias}'."));
        }

        foreach (var alias in newAliases.Except(oldAliases, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new CompatibilityFinding(addedCode, "info", path, $"Added {kind} '{alias}'."));
        }
    }

    private static void CompareText(string? baseline, string? current, string path, string kind, List<CompatibilityFinding> findings)
    {
        if (!string.Equals(baseline, current, StringComparison.Ordinal))
        {
            findings.Add(new CompatibilityFinding("KMCLI005", "info", path, $"{Capitalize(kind)} changed."));
        }
    }

    private static void CompareStatus(string? baseline, string? current, string path, List<CompatibilityFinding> findings)
    {
        if (!string.Equals(baseline, current, StringComparison.Ordinal))
        {
            findings.Add(new CompatibilityFinding("KMCLI202", "warning", path, "Status or deprecation state changed."));
        }
    }

    private static bool IsArityNarrowed(CanonicalParameter baseline, CanonicalParameter current)
    {
        var oldMinimum = baseline.ArityMinimum ?? 0;
        var newMinimum = current.ArityMinimum ?? 0;
        var oldMaximum = baseline.ArityMaximum ?? int.MaxValue;
        var newMaximum = current.ArityMaximum ?? int.MaxValue;
        var minimumNarrowed = newMinimum > oldMinimum &&
            !(baseline.Required != true && current.Required == true && oldMinimum == 0 && newMinimum == 1);
        return minimumNarrowed || newMaximum < oldMaximum;
    }

    private static DomainChange CompareDomain(CanonicalParameter baseline, CanonicalParameter current)
    {
        var oldType = NormalizeType(baseline.Type);
        var newType = NormalizeType(current.Type);
        var typeNarrowed = IsTypeNarrower(oldType, newType);
        var typeChanged = !string.Equals(oldType, newType, StringComparison.Ordinal);

        var oldValues = baseline.AllowedValues.Select(value => value.ToJsonString()).ToHashSet(StringComparer.Ordinal);
        var newValues = current.AllowedValues.Select(value => value.ToJsonString()).ToHashSet(StringComparer.Ordinal);
        var valuesChanged = !oldValues.SetEquals(newValues);
        var valuesNarrowed = valuesChanged && newValues.Count > 0 && (oldValues.Count == 0 || newValues.IsSubsetOf(oldValues));

        if (typeNarrowed || valuesNarrowed)
        {
            return DomainChange.Narrowed;
        }

        return typeChanged || valuesChanged ? DomainChange.Changed : DomainChange.Unchanged;
    }

    private static bool IsTypeNarrower(string? baseline, string? current)
    {
        if (baseline is null || current is null || string.Equals(baseline, current, StringComparison.Ordinal))
        {
            return false;
        }

        return baseline switch
        {
            "any" => true,
            "number" when current is "integer" or "int" => true,
            "string" when current == "enum" => true,
            _ => false
        };
    }

    private static string? NormalizeType(string? type) => type?.Trim().ToLowerInvariant();

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private enum DomainChange
    {
        Unchanged,
        Changed,
        Narrowed
    }
}
