using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

public sealed class CanonicalManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string Adapter { get; init; }
    public required string SourceVersion { get; init; }
    public required CanonicalCommand Root { get; init; }
}

public sealed class CanonicalCommand
{
    public required string Path { get; init; }
    public string[] Aliases { get; init; } = [];
    public string? Summary { get; init; }
    public string? Description { get; init; }
    public string? Status { get; init; }
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
    public JsonNode[] AllowedValues { get; init; } = [];
    public JsonNode? DefaultValue { get; init; }
    public CanonicalAlternativeSource[] AlternativeSources { get; init; } = [];
    public string? Status { get; init; }
}

public sealed class CanonicalAlternativeSource
{
    public required string Type { get; init; }
    public required string Property { get; init; }
}

public sealed class CanonicalArgument : CanonicalParameter
{
}

public sealed class CanonicalOption : CanonicalParameter
{
    public string[] Aliases { get; init; } = [];
}
