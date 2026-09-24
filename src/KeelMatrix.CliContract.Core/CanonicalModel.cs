using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

public sealed class CanonicalManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string Adapter { get; init; }
    public required string SourceVersion { get; init; }
    public CanonicalInfo Info { get; init; } = new();
    public CanonicalExitCode[] GlobalExitCodes { get; init; } = [];
    public CanonicalGlobalConfig? GlobalConfig { get; init; }
    public required CanonicalCommand Root { get; init; }
}

public sealed class CanonicalInfo
{
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? Description { get; init; }
    public string? Binary { get; init; }
    public string? Version { get; init; }
    public CanonicalLicense? License { get; init; }
    public CanonicalContact? Contact { get; init; }
    public CanonicalInstall[] Install { get; init; } = [];
}

public sealed class CanonicalGlobalConfig
{
    public CanonicalFileSource[] FileSources { get; init; } = [];
}

public sealed class CanonicalFileSource
{
    public required string Format { get; init; }
    public required string Path { get; init; }
}

public sealed class CanonicalLicense
{
    public required string Name { get; init; }
    public string? SpdxId { get; init; }
    public string? Url { get; init; }
}

public sealed class CanonicalContact
{
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Url { get; init; }
}

public sealed class CanonicalInstall
{
    public required string Name { get; init; }
    public string? Command { get; init; }
    public string? Url { get; init; }
    public string? Description { get; init; }
}

public sealed class CanonicalExitCode
{
    public int Code { get; init; }
    public required string Status { get; init; }
    public required string Summary { get; init; }
    public string? Description { get; init; }
}

public sealed class CanonicalExample
{
    public string? Title { get; init; }
    public required string Content { get; init; }
}

public sealed class CanonicalCommand
{
    public required string Path { get; init; }
    public string? Kind { get; init; }
    public string[] Aliases { get; init; } = [];
    public string? Summary { get; init; }
    public string? Description { get; init; }
    public string? Status { get; init; }
    public bool Hidden { get; init; }
    public CanonicalExitCode[] ExitCodes { get; init; } = [];
    public CanonicalExample[] Examples { get; init; } = [];
    public CanonicalArgument[] Arguments { get; init; } = [];
    public CanonicalOption[] Options { get; init; } = [];
    public CanonicalCommand[] Subcommands { get; init; } = [];
}

public abstract class CanonicalParameter
{
    public required string Name { get; init; }
    public string? Summary { get; init; }
    public string? Description { get; init; }
    public string? Type { get; init; }
    public bool? Required { get; init; }
    public int? ArityMinimum { get; init; }
    public int? ArityMaximum { get; init; }
    public bool Variadic { get; init; }
    public string? Hint { get; init; }
    public bool Hidden { get; init; }
    public JsonNode[] AllowedValues { get; init; } = [];
    public CanonicalChoice[] Choices { get; init; } = [];
    public JsonNode? DefaultValue { get; init; }
    public CanonicalAlternativeSource[] AlternativeSources { get; init; } = [];
    public string? Status { get; init; }
}

public sealed class CanonicalChoice
{
    public required JsonNode Value { get; init; }
    public string? Description { get; init; }
}

public sealed class CanonicalAlternativeSource
{
    public required string Type { get; init; }
    public required string Property { get; init; }
}

public sealed class CanonicalArgument : CanonicalParameter
{
    public bool Passthrough { get; init; }
}

public sealed class CanonicalOption : CanonicalParameter
{
    public string[] Aliases { get; init; } = [];
}
