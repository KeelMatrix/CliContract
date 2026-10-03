namespace KeelMatrix.CliContract.Core;

internal static class SourceContractRules
{
    private static readonly string[] SupportedTypes = ["string", "number", "integer", "boolean"];
    private static readonly string[] SupportedExitCodeStatuses =
    [
        "BAD_USER_INPUT_ERROR",
        "UNAUTHENTICATED_ERROR",
        "UNAUTHORIZED_ERROR",
        "CANCELED_ERROR",
        "INTERNAL_CLI_ERROR",
        "NOT_IMPLEMENTED_ERROR",
        "OK"
    ];

    public static bool IsNonEmpty(string? value) => value is not null && value.Length > 0;

    public static bool IsNonWhitespace(string? value) => IsNonEmpty(value) && !string.IsNullOrWhiteSpace(value);

    public static string NormalizeOptionName(string sourceName) => "--" + sourceName;

    public static bool IsRepresentableOptionName(string sourceName) =>
        IsNonEmpty(sourceName) && sourceName[0] is not ('-' or '=');

    public static bool IsSingleByteAlias(string alias) =>
        alias.Length == 1 && alias[0] <= '\u007f';

    public static bool AreRepresentableOptionAliases(IEnumerable<string> aliases)
    {
        var shorthandCount = 0;
        foreach (var alias in aliases)
        {
            if (!IsRepresentableOptionName(alias))
            {
                return false;
            }

            if (IsSingleByteAlias(alias) && ++shorthandCount > 1)
            {
                return false;
            }
        }

        return true;
    }

    public static string OptionAliasInvocationName(string alias) =>
        IsSingleByteAlias(alias) ? "-" + alias : NormalizeOptionName(alias);

    public static IEnumerable<string> OptionInvocationNames(string canonicalName, IEnumerable<string> aliases)
    {
        yield return canonicalName;
        foreach (var alias in aliases)
        {
            yield return OptionAliasInvocationName(alias);
        }
    }

    public static bool IsNormalizedOptionName(string canonicalName)
    {
        if (!canonicalName.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        return canonicalName.Length > 2 &&
            IsRepresentableOptionName(canonicalName[2..]) &&
            string.Equals(canonicalName, NormalizeOptionName(canonicalName[2..]), StringComparison.Ordinal);
    }

    public static bool IsSupportedType(string? type) => SupportedTypes.Contains(type, StringComparer.Ordinal);

    public static bool IsSupportedExitCodeStatus(string? status) => SupportedExitCodeStatuses.Contains(status, StringComparer.Ordinal);

    public static bool IsSupportedAlternativeSourceType(string? type) => type is "$ENV" or "$FILE";

    public static bool IsSupportedFileFormat(string? format) => format is "json" or "toml" or "yaml";

    public static bool IsCommandSegment(string segment) =>
        IsNonEmpty(segment) &&
        !segment.Any(char.IsWhiteSpace) &&
        !segment.Contains('/', StringComparison.Ordinal) &&
        char.IsAsciiLetter(segment[0]);
}
