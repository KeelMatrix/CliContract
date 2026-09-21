using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KeelMatrix.CliContract.Core;

return CliApplication.Run(args);

internal static class CliApplication
{
    private const string Version = "0.1.0";
    private static readonly JsonSerializerOptions OutputJsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Any(argument => argument is "--help" or "-h"))
            {
                Console.WriteLine(HelpText);
                return 0;
            }

            if (args.Length == 1 && args[0] == "--version")
            {
                Console.WriteLine(Version);
                return 0;
            }

            var invocation = Parse(args);
            return Execute(invocation);
        }
        catch (InvocationException exception)
        {
            return ReportError(exception.Code, exception.Message, 2, RequestedFormat(args));
        }
        catch (NormalizationException exception)
        {
            return ReportError(exception.Code, exception.Message, 3, RequestedFormat(args));
        }
        catch (CompatibilityException exception)
        {
            return ReportError(exception.Code, exception.Message, 3, RequestedFormat(args));
        }
        catch (Exception)
        {
            return ReportError("INTERNAL_ERROR", "The analysis could not complete.", 4, RequestedFormat(args));
        }
    }

    private static int Execute(Invocation invocation)
    {
        return invocation.Command switch
        {
            "snapshot" => Snapshot(invocation),
            "validate" => Validate(invocation),
            "diff" => Diff(invocation),
            "check" => Check(invocation),
            _ => throw new InvocationException("INVALID_INVOCATION", "Use snapshot, check, diff, or validate.")
        };
    }

    private static int Snapshot(Invocation invocation)
    {
        RequirePositionals(invocation, 1);
        if (invocation.Output is null) throw new InvocationException("MISSING_OUTPUT", "snapshot requires --output <file>.");
        var manifest = LoadSource(invocation.Positionals[0], invocation.InputKind);
        WriteManifest(invocation.Output, manifest);
        return ReportManifest(manifest, invocation.Format, invocation.Output);
    }

    private static int Validate(Invocation invocation)
    {
        RequirePositionals(invocation, 1);
        var manifest = LoadSource(invocation.Positionals[0], invocation.InputKind);
        if (invocation.Format == OutputFormat.Json)
        {
            WriteJson(new OutputEnvelope(new ToolStatus("validate", "valid", manifest.Adapter, CountCommands(manifest), 0), [], []));
        }
        else
        {
            Console.WriteLine($"VALID adapter={manifest.Adapter} source={manifest.SourceVersion}");
        }

        return 0;
    }

    private static int Diff(Invocation invocation)
    {
        RequirePositionals(invocation, 2);
        var baseline = LoadDescription(invocation.Positionals[0], invocation.InputKind);
        var current = LoadDescription(invocation.Positionals[1], invocation.InputKind);
        var result = CompatibilityAnalyzer.Compare(baseline, current);
        var filtered = ApplySuppressions(result.Findings, invocation.IgnoreFile);
        WriteFindings(invocation.Format, filtered, [], CountCommands(current));
        return GatedExit(filtered, invocation.FailOn);
    }

    private static int Check(Invocation invocation)
    {
        RequirePositionals(invocation, 1);
        if (invocation.Baseline is null) throw new InvocationException("MISSING_BASELINE", "check requires --baseline <file>.");
        var current = LoadSource(invocation.Positionals[0], invocation.InputKind);
        var baseline = ReadManifest(invocation.Baseline);
        var result = CompatibilityAnalyzer.Compare(baseline, current);
        var filtered = ApplySuppressions(result.Findings, invocation.IgnoreFile);
        WriteFindings(invocation.Format, filtered, [], CountCommands(current));
        return GatedExit(filtered, invocation.FailOn);
    }

    private static CanonicalManifest LoadSource(string path, InputKind inputKind)
    {
        var input = ReadFile(path, "INPUT_NOT_FOUND");
        if (inputKind == InputKind.OpenCli)
        {
            return Normalizer.Normalize("opencli", input);
        }

        var trimmed = input.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            JsonObject? root;
            try { root = JsonNode.Parse(input) as JsonObject; }
            catch (JsonException) { root = null; }
            if (root is not null && root.ContainsKey("opencliVersion") && root.ContainsKey("SchemaVersion"))
            {
                throw new NormalizationException("AMBIGUOUS_INPUT", "The input matches more than one supported schema shape.");
            }
            if (root is not null && root.ContainsKey("schemaVersion"))
            {
                throw new NormalizationException("UNSUPPORTED_INPUT", "Canonical manifests are not source schemas for snapshot or validate.");
            }
        }

        return Normalizer.Normalize("opencli", input);
    }

    private static CanonicalManifest LoadDescription(string path, InputKind inputKind)
    {
        var input = ReadFile(path, "INPUT_NOT_FOUND");
        if (LooksLikeCanonicalManifest(input))
        {
            return CanonicalManifestReader.Read(input);
        }

        if (inputKind == InputKind.OpenCli)
        {
            return Normalizer.Normalize("opencli", input);
        }

        var trimmed = input.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            JsonObject? root;
            try { root = JsonNode.Parse(input) as JsonObject; }
            catch (JsonException) { root = null; }
            if (root is not null && root.ContainsKey("opencliVersion") && root.ContainsKey("schemaVersion"))
            {
                throw new NormalizationException("AMBIGUOUS_INPUT", "The input matches more than one supported schema shape.");
            }
        }

        return Normalizer.Normalize("opencli", input);
    }

    private static CanonicalManifest ReadManifest(string path)
    {
        return CanonicalManifestReader.Read(ReadFile(path, "BASELINE_NOT_FOUND"));
    }

    private static string ReadFile(string path, string code)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvocationException(code, "The requested input file does not exist.");
        }

        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception) when (code is "INPUT_NOT_FOUND" or "BASELINE_NOT_FOUND")
        {
            throw new InvocationException(code, "The requested input file could not be read.");
        }
    }

    private static void WriteManifest(string path, CanonicalManifest manifest)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is null) throw new IOException();
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, Normalizer.Serialize(manifest), new UTF8Encoding(false));
        }
        catch (Exception)
        {
            throw new InvocationException("OUTPUT_NOT_WRITABLE", "The snapshot output file could not be written.");
        }
    }

    private static bool LooksLikeCanonicalManifest(string input)
    {
        if (!input.TrimStart().StartsWith('{')) return false;
        try
        {
            var root = JsonNode.Parse(input) as JsonObject;
            return root?.ContainsKey("SchemaVersion") == true && root.ContainsKey("Adapter") && root.ContainsKey("Root");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<CompatibilityFinding> ApplySuppressions(IReadOnlyList<CompatibilityFinding> findings, string? ignorePath)
    {
        if (ignorePath is null) return findings;
        var input = ReadFile(ignorePath, "IGNORE_NOT_FOUND");
        JsonNode document;
        try { document = JsonNode.Parse(input) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvocationException("INVALID_IGNORE", "The ignore file must be valid JSON."); }

        var codes = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (document is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonValue || item.GetValueKind() != JsonValueKind.String) throw new InvocationException("INVALID_IGNORE", "The ignore file array must contain strings.");
                codes.Add(item.GetValue<string>());
            }
        }
        else if (document is JsonObject objectNode)
        {
            ReadIgnoreValues(objectNode["codes"], codes, "codes");
            ReadIgnoreValues(objectNode["paths"], paths, "paths");
        }
        else
        {
            throw new InvocationException("INVALID_IGNORE", "The ignore file must be an array or object.");
        }

        return findings.Where(finding => !codes.Contains(finding.Code) && !paths.Contains(finding.Path)).ToArray();
    }

    private static void ReadIgnoreValues(JsonNode? node, HashSet<string> output, string property)
    {
        if (node is null) return;
        if (node is not JsonArray array) throw new InvocationException("INVALID_IGNORE", $"The ignore file '{property}' value must be an array.");
        foreach (var item in array)
        {
            if (item is not JsonValue || item.GetValueKind() != JsonValueKind.String) throw new InvocationException("INVALID_IGNORE", "Ignore entries must be strings.");
            output.Add(item.GetValue<string>());
        }
    }

    private static int GatedExit(IReadOnlyList<CompatibilityFinding> findings, FailOn failOn)
    {
        return findings.Any(finding => finding.Category == "breaking" || (failOn == FailOn.Warning && finding.Category == "warning")) ? 1 : 0;
    }

    private static void WriteFindings(OutputFormat format, IReadOnlyList<CompatibilityFinding> findings, IReadOnlyList<ToolError> errors, int commandCount)
    {
        if (format == OutputFormat.Json)
        {
            WriteJson(new OutputEnvelope(new ToolStatus("compare", findings.Count == 0 ? "compatible" : "findings", "opencli", commandCount, findings.Count), findings, errors));
            return;
        }

        if (findings.Count == 0)
        {
            Console.WriteLine("COMPATIBLE no gated compatibility changes found.");
            return;
        }

        foreach (var finding in findings)
        {
            Console.WriteLine($"{finding.Category.ToUpperInvariant()} {finding.Code} {finding.Message} Path: {finding.Path}");
        }
    }

    private static int CountCommands(CanonicalManifest manifest) => CountCommands(manifest.Root);

    private static int CountCommands(CanonicalCommand command) => 1 + command.Subcommands.Sum(CountCommands);

    private static int ReportManifest(CanonicalManifest manifest, OutputFormat format, string output)
    {
        if (format == OutputFormat.Json)
        {
            WriteJson(new { tool = new ToolStatus("snapshot", "created", manifest.Adapter, CountCommands(manifest), 0), manifest });
        }
        else
        {
            Console.WriteLine($"SNAPSHOT adapter={manifest.Adapter} schema={manifest.SchemaVersion} output={Path.GetFileName(output)}");
        }

        return 0;
    }

    private static int ReportError(string code, string message, int exitCode, OutputFormat? format)
    {
        if (format == OutputFormat.Json)
        {
            WriteJson(new OutputEnvelope(new ToolStatus("error", "error", "opencli", 0, 0), [], [new ToolError(code, message)]));
        }
        else
        {
            Console.Error.WriteLine($"ERROR {code}: {message}");
        }

        return exitCode;
    }

    private static Invocation Parse(string[] args)
    {
        var command = args[0].ToLowerInvariant();
        var positionals = new List<string>();
        var inputKind = InputKind.Auto;
        var format = OutputFormat.Text;
        var failOn = FailOn.Breaking;
        string? output = null;
        string? baseline = null;
        string? ignore = null;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--input": inputKind = ParseInputKind(NextValue(args, ref index, "--input")); break;
                case "--format": format = ParseFormat(NextValue(args, ref index, "--format")); break;
                case "--fail-on": failOn = ParseFailOn(NextValue(args, ref index, "--fail-on")); break;
                case "--output": output = NextValue(args, ref index, "--output"); break;
                case "--baseline": baseline = NextValue(args, ref index, "--baseline"); break;
                case "--ignore": ignore = NextValue(args, ref index, "--ignore"); break;
                case "--no-telemetry": break;
                default:
                    if (argument.StartsWith('-')) throw new InvocationException("UNKNOWN_OPTION", $"Unknown option '{argument}'.");
                    positionals.Add(argument);
                    break;
            }
        }

        if (command is not ("snapshot" or "check" or "diff" or "validate")) throw new InvocationException("UNKNOWN_COMMAND", "Use snapshot, check, diff, or validate.");
        return new Invocation(command, positionals, inputKind, format, failOn, output, baseline, ignore);
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || args[index].StartsWith('-')) throw new InvocationException("MISSING_OPTION_VALUE", $"{option} requires a value.");
        return args[index];
    }

    private static InputKind ParseInputKind(string value) => value.ToLowerInvariant() switch
    {
        "auto" => InputKind.Auto,
        "opencli" => InputKind.OpenCli,
        _ => throw new InvocationException("INVALID_INPUT_KIND", "--input accepts auto or opencli.")
    };

    private static OutputFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "text" => OutputFormat.Text,
        "json" => OutputFormat.Json,
        _ => throw new InvocationException("INVALID_FORMAT", "--format accepts text or json.")
    };

    private static OutputFormat? RequestedFormat(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == "--format")
            {
                return args[index + 1].Equals("json", StringComparison.OrdinalIgnoreCase) ? OutputFormat.Json : OutputFormat.Text;
            }
        }

        return OutputFormat.Text;
    }

    private static FailOn ParseFailOn(string value) => value.ToLowerInvariant() switch
    {
        "breaking" => FailOn.Breaking,
        "warning" => FailOn.Warning,
        _ => throw new InvocationException("INVALID_FAIL_ON", "--fail-on accepts breaking or warning.")
    };

    private static void RequirePositionals(Invocation invocation, int count)
    {
        if (invocation.Positionals.Count != count) throw new InvocationException("INVALID_INVOCATION", $"{invocation.Command} expects {count} input path{(count == 1 ? "" : "s")}.");
    }

    private static void WriteJson<T>(T value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, OutputJsonOptions));
    }

    private sealed record Invocation(string Command, List<string> Positionals, InputKind InputKind, OutputFormat Format, FailOn FailOn, string? Output, string? Baseline, string? IgnoreFile);
    private sealed record ToolStatus(string Operation, string Result, string InputKind, int CommandCount, int ChangeCount);
    private sealed record ToolError(string Code, string Message);
    private sealed record OutputEnvelope(ToolStatus Tool, IReadOnlyList<CompatibilityFinding> Findings, IReadOnlyList<ToolError> Errors);
    private sealed class InvocationException(string code, string message) : Exception(message) { public string Code { get; } = code; }
    private enum InputKind { Auto, OpenCli }
    private enum OutputFormat { Text, Json }
    private enum FailOn { Breaking, Warning }

    private const string HelpText = """
    CliContract fails CI when an OpenCLI command-line contract changes incompatibly.

    Usage:
      clicontract snapshot <schema> --output <file>
      clicontract check <schema> --baseline <file>
      clicontract diff <old> <new>
      clicontract validate <schema>

    Options:
      --input auto|opencli       Input format (default: auto)
      --format text|json         Output format (default: text)
      --fail-on breaking|warning Failure threshold (default: breaking)
      --ignore <file>            Explicit JSON suppression file
      --no-telemetry             Disable optional telemetry
    """;
}
