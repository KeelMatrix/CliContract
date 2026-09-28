using System.Text;
using System.Text.Json;
using KeelMatrix.CliContract.Core;

return CliApplication.Run(args);

internal static class CliApplication
{
    private const string Version = "0.1.0";
    private static readonly int MaxInputBytes = new NormalizationLimits().MaxInputBytes;
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
            return ReportError("UNEXPECTED_ERROR", "An unexpected tool failure occurred.", 4, RequestedFormat(args));
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
        var exitCode = GatedExit(filtered, invocation.FailOn);
        TrackSuccessfulComparison(invocation, baseline);
        return exitCode;
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
        var exitCode = GatedExit(filtered, invocation.FailOn);
        TrackSuccessfulComparison(invocation, baseline);
        return exitCode;
    }

    private static CanonicalManifest LoadSource(string path, InputKind inputKind)
    {
        var input = ReadFile(path, FileRole.SourceSchema);
        var kind = ClassifyInput(input);
        if (kind == InputDocumentKind.Canonical)
        {
            throw new NormalizationException("UNSUPPORTED_INPUT", "Canonical manifests are accepted only as diff operands with --input auto.");
        }

        if (kind == InputDocumentKind.Ambiguous)
        {
            throw new NormalizationException("AMBIGUOUS_INPUT", "The input matches more than one supported schema shape.");
        }

        return Normalizer.Normalize("opencli", input);
    }

    private static CanonicalManifest LoadDescription(string path, InputKind inputKind)
    {
        var input = ReadFile(path, FileRole.SourceSchema);
        var kind = ClassifyInput(input);
        if (kind == InputDocumentKind.Ambiguous)
        {
            throw new NormalizationException("AMBIGUOUS_INPUT", "The input matches more than one supported schema shape.");
        }

        if (kind == InputDocumentKind.Canonical)
        {
            if (inputKind == InputKind.OpenCli)
            {
                throw new NormalizationException("UNSUPPORTED_INPUT", "The --input opencli selection requires OpenCLI source schemas; canonical manifests are accepted only with --input auto.");
            }

            return CanonicalManifestReader.Read(input);
        }

        return Normalizer.Normalize("opencli", input);
    }

    private static CanonicalManifest ReadManifest(string path)
    {
        return CanonicalManifestReader.Read(ReadFile(path, FileRole.CanonicalBaseline));
    }

    private static string ReadFile(string path, FileRole role)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvocationException(NotFoundCode(role), NotFoundMessage(role));
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            if (stream.Length > MaxInputBytes)
            {
                throw new InputTooLargeException();
            }

            var bytes = new byte[MaxInputBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }

            if (count > MaxInputBytes || stream.Length > MaxInputBytes || stream.Position < stream.Length)
            {
                throw new InputTooLargeException();
            }

            try
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes, 0, count);
            }
            catch (DecoderFallbackException)
            {
                ThrowReadFailure(role, "INVALID_UTF8", "The file is not valid UTF-8.");
                throw new InvalidOperationException();
            }
        }
        catch (InputTooLargeException)
        {
            ThrowReadFailure(role, TooLargeCode(role), TooLargeMessage(role));
            throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ThrowReadFailure(role, UnreadableCode(role), UnreadableMessage(role));
            throw new InvalidOperationException();
        }
    }

    private static string NotFoundCode(FileRole role) => role switch
    {
        FileRole.CanonicalBaseline => "BASELINE_NOT_FOUND",
        FileRole.Suppression => "IGNORE_NOT_FOUND",
        _ => "INPUT_NOT_FOUND"
    };

    private static string NotFoundMessage(FileRole role) => role switch
    {
        FileRole.CanonicalBaseline => "The canonical baseline file does not exist.",
        FileRole.Suppression => "The suppression file does not exist.",
        _ => "The source schema file does not exist."
    };

    private static string UnreadableCode(FileRole role) => role switch
    {
        FileRole.CanonicalBaseline => "BASELINE_UNREADABLE",
        FileRole.Suppression => "IGNORE_UNREADABLE",
        _ => "INPUT_UNREADABLE"
    };

    private static string UnreadableMessage(FileRole role) => role switch
    {
        FileRole.CanonicalBaseline => "The canonical baseline file could not be read.",
        FileRole.Suppression => "The suppression file could not be read.",
        _ => "The source schema file could not be read."
    };

    private static string TooLargeCode(FileRole role) => role == FileRole.Suppression ? "IGNORE_TOO_LARGE" : "INPUT_TOO_LARGE";

    private static string TooLargeMessage(FileRole role) => role == FileRole.Suppression
        ? "The suppression file exceeds the configured size limit in UTF-8 bytes."
        : role == FileRole.CanonicalBaseline
            ? "The canonical baseline exceeds the configured size limit in UTF-8 bytes."
            : "The source schema exceeds the configured size limit in UTF-8 bytes.";

    private static void ThrowReadFailure(FileRole role, string code, string message)
    {
        if (role == FileRole.Suppression)
        {
            throw new InvocationException(code, message);
        }

        throw new NormalizationException(code, message);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvocationException(WriteFailureCode(FileRole.OutputDestination), "The snapshot output file could not be written.");
        }
    }

    private static string WriteFailureCode(FileRole role) => role == FileRole.OutputDestination ? "OUTPUT_NOT_WRITABLE" : "INVALID_INVOCATION";

    private static InputDocumentKind ClassifyInput(string input)
    {
        if (!input.TrimStart().StartsWith('{')) return InputDocumentKind.Source;
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return InputDocumentKind.Source;

            var hasOpenCli = document.RootElement.TryGetProperty("opencliVersion", out _);
            var hasCanonical = document.RootElement.TryGetProperty("SchemaVersion", out _) ||
                document.RootElement.TryGetProperty("Adapter", out _) ||
                document.RootElement.TryGetProperty("Root", out _);
            return (hasOpenCli, hasCanonical) switch
            {
                (true, true) => InputDocumentKind.Ambiguous,
                (false, true) => InputDocumentKind.Canonical,
                _ => InputDocumentKind.Source
            };
        }
        catch (JsonException)
        {
            return InputDocumentKind.Source;
        }
    }

    private static IReadOnlyList<CompatibilityFinding> ApplySuppressions(IReadOnlyList<CompatibilityFinding> findings, string? ignorePath)
    {
        if (ignorePath is null) return findings;
        var input = ReadFile(ignorePath, FileRole.Suppression);
        var (codes, paths) = ReadSuppressions(input);
        return findings.Where(finding => !codes.Contains(finding.Code) && !paths.Contains(finding.Path)).ToArray();
    }

    private static (HashSet<string> Codes, HashSet<string> Paths) ReadSuppressions(string input)
    {
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                MaxDepth = new NormalizationLimits().MaxDepth + 1,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });

            var codes = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var counter = 0;
            ReadSuppressionDocument(document.RootElement, 0, ref counter, codes, paths);
            return (codes, paths);
        }
        catch (InvocationException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new InvocationException("INVALID_IGNORE", "The ignore file must be valid bounded JSON.");
        }
    }

    private static void ReadSuppressionDocument(JsonElement value, int depth, ref int nodeCount, HashSet<string> codes, HashSet<string> paths)
    {
        var limits = new NormalizationLimits();
        if (depth > limits.MaxDepth || ++nodeCount > limits.MaxNodes)
        {
            throw new InvocationException("INVALID_IGNORE", "The ignore file exceeds the configured structure limits.");
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > limits.MaxCollectionItems) throw new InvocationException("INVALID_IGNORE", "The ignore file contains too many entries.");
            foreach (var item in value.EnumerateArray())
            {
                AddSuppressionString(item, limits, codes);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvocationException("INVALID_IGNORE", "The ignore file must be an array or object.");
        }

        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.Add(property.Name) || property.Name is not ("codes" or "paths"))
            {
                throw new InvocationException("INVALID_IGNORE", "The ignore file object may contain only unique codes and paths properties.");
            }

            if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > limits.MaxCollectionItems)
            {
                throw new InvocationException("INVALID_IGNORE", $"The ignore file '{property.Name}' value must be an array.");
            }

            var target = property.Name == "codes" ? codes : paths;
            foreach (var item in property.Value.EnumerateArray()) AddSuppressionString(item, limits, target);
        }
    }

    private static void AddSuppressionString(JsonElement value, NormalizationLimits limits, HashSet<string> target)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvocationException("INVALID_IGNORE", "Ignore entries must be nonempty strings.");
        }

        var text = value.GetString() ?? string.Empty;
        if (text.Length == 0 || text.Length > limits.MaxStringLength)
        {
            throw new InvocationException("INVALID_IGNORE", "Ignore entries must be nonempty bounded strings.");
        }

        if (!target.Add(text)) throw new InvocationException("INVALID_IGNORE", "The ignore file contains duplicate entries.");
    }

    private static int GatedExit(IReadOnlyList<CompatibilityFinding> findings, FailOn failOn)
    {
        return findings.Any(finding => finding.Category == "breaking" || (failOn == FailOn.Warning && finding.Category == "warning")) ? 1 : 0;
    }

    private static void TrackSuccessfulComparison(Invocation invocation, CanonicalManifest baseline)
    {
        if (invocation.NoTelemetry || !HasNonEmptyCommandSurface(baseline) || IsTelemetrySuppressedForDevelopmentOrCi())
        {
            return;
        }

        try
        {
            // The published telemetry contract accepts only shared bounded activation data.
            // No schema-derived value is passed to the telemetry package.
            ActivationTelemetry.TrackActivation();
        }
        catch
        {
            // Telemetry is optional and must never affect comparison behavior.
        }
    }

    private static bool HasNonEmptyCommandSurface(CanonicalManifest manifest)
    {
        return HasMeaningfulCommand(manifest.Root);
    }

    internal static bool HasMeaningfulCommand(CanonicalCommand command)
    {
        return CanonicalCommandContract.ContainsRunnableAction(command);
    }

    private static bool IsTelemetrySuppressedForDevelopmentOrCi()
    {
        return IsTrue(Environment.GetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY")) ||
            IsTrue(Environment.GetEnvironmentVariable("KEELMATRIX_DEVELOPMENT")) ||
            IsTrue(Environment.GetEnvironmentVariable("CI"));
    }

    private static bool IsTrue(string? value) => value is "1" or "true" or "TRUE" or "True";

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
            Console.WriteLine($"{finding.Category.ToUpperInvariant()} {finding.Code} {SafeDisplay(finding.Message)} Path: {SafeDisplay(finding.Path)}");
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
            Console.WriteLine($"SNAPSHOT adapter={SafeDisplay(manifest.Adapter)} schema={manifest.SchemaVersion} output={SafeDisplay(Path.GetFileName(output))}");
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
            Console.Error.WriteLine($"ERROR {SafeDisplay(code)}: {SafeDisplay(message)}");
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
        var noTelemetry = false;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--input": EnsureSingleOption(seenOptions, argument); inputKind = ParseInputKind(NextValue(args, ref index, "--input")); break;
                case "--format": EnsureSingleOption(seenOptions, argument); format = ParseFormat(NextValue(args, ref index, "--format")); break;
                case "--fail-on": EnsureSingleOption(seenOptions, argument); failOn = ParseFailOn(NextValue(args, ref index, "--fail-on")); break;
                case "--output": EnsureSingleOption(seenOptions, argument); output = NextValue(args, ref index, "--output"); break;
                case "--baseline": EnsureSingleOption(seenOptions, argument); baseline = NextValue(args, ref index, "--baseline"); break;
                case "--ignore": EnsureSingleOption(seenOptions, argument); ignore = NextValue(args, ref index, "--ignore"); break;
                case "--no-telemetry": EnsureSingleOption(seenOptions, argument); noTelemetry = true; break;
                default:
                    if (argument.StartsWith('-')) throw new InvocationException("UNKNOWN_OPTION", $"Unknown option '{argument}'.");
                    positionals.Add(argument);
                    break;
            }
        }

        if (command is not ("snapshot" or "check" or "diff" or "validate")) throw new InvocationException("UNKNOWN_COMMAND", "Use snapshot, check, diff, or validate.");
        EnsureAllowedOptions(command, seenOptions);
        return new Invocation(command, positionals, inputKind, format, failOn, output, baseline, ignore, noTelemetry);
    }

    private static void EnsureAllowedOptions(string command, IReadOnlySet<string> seenOptions)
    {
        var allowed = command switch
        {
            "snapshot" => new HashSet<string>(["--input", "--format", "--output", "--no-telemetry"], StringComparer.Ordinal),
            "validate" => new HashSet<string>(["--input", "--format", "--no-telemetry"], StringComparer.Ordinal),
            "diff" => new HashSet<string>(["--input", "--format", "--fail-on", "--ignore", "--no-telemetry"], StringComparer.Ordinal),
            "check" => new HashSet<string>(["--input", "--format", "--fail-on", "--baseline", "--ignore", "--no-telemetry"], StringComparer.Ordinal),
            _ => throw new InvocationException("UNKNOWN_COMMAND", "Use snapshot, check, diff, or validate.")
        };

        foreach (var option in seenOptions)
        {
            if (!allowed.Contains(option))
            {
                throw new InvocationException("UNSUPPORTED_OPTION", $"Option '{option}' is not supported for '{command}'; remove it or use a command that consumes it.");
            }
        }
    }

    private static void EnsureSingleOption(HashSet<string> seenOptions, string option)
    {
        if (!seenOptions.Add(option))
        {
            throw new InvocationException("DUPLICATE_OPTION", $"Option '{option}' was provided more than once; provide it only once.");
        }
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

    private static string SafeDisplay(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == ':' && index + 1 < value.Length && value[index + 1] == ':')
            {
                builder.Append("\\u003A\\u003A");
                index++;
                continue;
            }

            var isPair = char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]);
            var codePoint = isPair ? char.ConvertToUtf32(value, index) : character;
            var codePointLength = isPair ? 2 : 1;
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(value, index);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format ||
                codePoint is 0x2028 or 0x2029 || IsDefaultIgnorable(codePoint))
            {
                if (codePoint <= 0xffff)
                {
                    builder.Append("\\u").Append(codePoint.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append("\\U").Append(codePoint.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }

                index += codePointLength - 1;
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static bool IsDefaultIgnorable(int codePoint) =>
        codePoint is 0x00ad or 0x034f or 0x061c or 0x180e or 0x200b or 0x200c or 0x200d or 0x200e or 0x200f or
            (>= 0x115f and <= 0x1160) or (>= 0x17b4 and <= 0x17b5) or (>= 0x180b and <= 0x180d) or
            (>= 0x202a and <= 0x202e) or (>= 0x2060 and <= 0x2064) or (>= 0x2066 and <= 0x206f) or
            (>= 0xfe00 and <= 0xfe0f) or 0xfeff or (>= 0xfff0 and <= 0xfff8) or
            (>= 0xe0000 and <= 0xe0fff);

    private sealed record Invocation(string Command, List<string> Positionals, InputKind InputKind, OutputFormat Format, FailOn FailOn, string? Output, string? Baseline, string? IgnoreFile, bool NoTelemetry);
    private sealed record ToolStatus(string Operation, string Result, string InputKind, int CommandCount, int ChangeCount);
    private sealed record ToolError(string Code, string Message);
    private sealed record OutputEnvelope(ToolStatus Tool, IReadOnlyList<CompatibilityFinding> Findings, IReadOnlyList<ToolError> Errors);
    private sealed class InvocationException(string code, string message) : Exception(message) { public string Code { get; } = code; }
    private sealed class InputTooLargeException : Exception;
    private enum InputKind { Auto, OpenCli }
    private enum InputDocumentKind { Source, Canonical, Ambiguous }
    private enum OutputFormat { Text, Json }
    private enum FailOn { Breaking, Warning }
    private enum FileRole { SourceSchema, CanonicalBaseline, Suppression, OutputDestination }

    private const string HelpText = """
    CliContract fails CI when an OpenCLI command-line contract changes incompatibly.

    Usage:
      clicontract snapshot <schema> --output <file>
      clicontract check <schema> --baseline <file>
      clicontract diff <old> <new>
      clicontract validate <schema>

    Command options:
      snapshot   --input --format --output --no-telemetry
      check      --input --format --baseline --fail-on --ignore --no-telemetry
      diff       --input --format --fail-on --ignore --no-telemetry
      validate   --input --format --no-telemetry

    Options:
      --input auto|opencli       Input format (default: auto)
      --format text|json         Output format (default: text)
      --fail-on breaking|warning Failure threshold (default: breaking)
      --ignore <file>            Explicit JSON suppression file
      --no-telemetry             Disable optional telemetry; CI/development suppress automatically
      Telemetry activates only after comparison with a runnable action at the root or below a group;
      aliases, parameters, help metadata, and group-only trees do not qualify.

    Canonicalization:
      --input opencli requires OpenCLI source for every source-schema operand. Canonical manifests are accepted
      only as diff operands with --input auto; auto accepts source/source, canonical/canonical, and mixed pairs.
      snapshot, validate, and check source operands reject canonical manifests with UNSUPPORTED_INPUT.
      Finite JSON and recognized YAML numbers are compared by exact numeric value,
      including trailing-dot exponent mantissas such as 5.e2.
      YAML .inf and .nan are outside that boundary: tagged forms error; untagged forms are strings.
      Accepted command names include aliases at every command segment; retained aliases preserve old paths.
      All supported string, number, integer, and boolean type domains are compared, and exit-code changes warn.
      Choices and defaults must match their declared type; non-integral integer values fail with exit 3.
      Exact numeric representation and integer detection are shared by validation and compatibility comparison.
      Argument passthrough and keyed global config formats are represented compatibility semantics.
      Represented info metadata, install guidance, summary/description text, and choice descriptions produce KMCLI005
      info findings; visibility and example metadata produce KMCLI006 info findings. Info findings are reported but
      never gate either --fail-on threshold.

    Validation boundary:
      Accepted canonical baselines must be source-producible alpha.14 states: required metadata and contact/install
      presence, ASCII-letter command segments and materialized hierarchy, exact parameter types, valid sources,
      unique file-source/exit-code values, and canonical choice domains are checked before comparison.
      One variadic positional argument is allowed and it must be last; minItems/maxItems require variadic=true.
      Typed defaults and choices that are not representable by their declared type fail with exit 3.
      Missing source or baseline paths return 2. Present but unreadable, malformed, unsupported, invalid-UTF-8,
      or oversized source/baseline files return 3. Suppression/configuration and output failures return 2.

    Exit codes:
      0  No gated compatibility finding
      1  Gated compatibility finding reported
      2  Invalid invocation or configuration
      3  Invalid or unsupported input schema or baseline
      4  Unexpected tool failure
    """;
}
