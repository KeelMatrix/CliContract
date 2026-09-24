using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

internal static class TypedDomain
{
    public static string NormalizeType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        null or "" => "string",
        var value => value
    };

    public static bool IsSupportedType(string? type) => NormalizeType(type) is "string" or "number" or "integer" or "boolean";

    public static bool Accepts(string? type, JsonNode value, bool allowNumericStrings = false)
    {
        return NormalizeType(type) switch
        {
            "string" => value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False,
            "number" => ExactNumber.TryParse(value, allowNumericStrings, out _),
            "integer" => ExactNumber.TryParse(value, allowNumericStrings, out var integer) && integer.IsInteger,
            "boolean" => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ||
                allowNumericStrings && value is JsonValue booleanValue && booleanValue.TryGetValue<string>(out var booleanText) && booleanText is "true" or "false",
            _ => false
        };
    }

    public static bool Equivalent(string? type, JsonNode left, JsonNode right, bool allowNumericStrings = false)
    {
        return NormalizeType(type) switch
        {
            "number" or "integer" => ExactNumber.TryParse(left, allowNumericStrings, out var leftNumber) &&
                ExactNumber.TryParse(right, allowNumericStrings, out var rightNumber) &&
                leftNumber.EqualsValue(rightNumber),
            "boolean" => BooleanValue(left) == BooleanValue(right),
            "string" => StringValue(left) == StringValue(right),
            _ => JsonNode.DeepEquals(left, right)
        };
    }

    public static bool IsBaseSubset(string oldType, string newType) =>
        string.Equals(oldType, newType, StringComparison.Ordinal) ||
        oldType == "integer" && newType == "number" ||
        oldType is "integer" or "number" or "boolean" && newType == "string";

    private static string StringValue(JsonNode value) => value.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.Number => ExactNumber.TryParse(value, allowNumericString: false, out var number) ? number.ToCanonicalString() : value.ToJsonString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.ToJsonString()
    };

    private static string? BooleanValue(JsonNode value) => value.GetValueKind() switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String when value.GetValue<string>() is "true" or "false" => value.GetValue<string>(),
        _ => null
    };
}
