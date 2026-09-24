using System.Numerics;
using System.Text.Json.Nodes;

namespace KeelMatrix.CliContract.Core;

internal readonly record struct ExactNumber(BigInteger Significand, BigInteger PowerOfTen)
{
    public bool IsInteger => Significand.IsZero || PowerOfTen >= 0;

    public static bool TryParse(string text, out ExactNumber number)
    {
        number = default;
        var raw = text.Trim();
        if (raw.Length == 0) return false;

        var index = 0;
        var negative = raw[index] == '-';
        if (negative || raw[index] == '+') index++;
        if (index == raw.Length) return false;

        var integerStart = index;
        while (index < raw.Length && char.IsDigit(raw[index])) index++;
        var integerDigits = raw[integerStart..index];
        var fractionDigits = string.Empty;
        if (index < raw.Length && raw[index] == '.')
        {
            var fractionStart = ++index;
            while (index < raw.Length && char.IsDigit(raw[index])) index++;
            fractionDigits = raw[fractionStart..index];
        }

        if (integerDigits.Length == 0 && fractionDigits.Length == 0) return false;

        var exponent = BigInteger.Zero;
        if (index < raw.Length && raw[index] is 'e' or 'E')
        {
            index++;
            var exponentNegative = index < raw.Length && raw[index] == '-';
            if (exponentNegative || index < raw.Length && raw[index] == '+') index++;
            var exponentStart = index;
            while (index < raw.Length && char.IsDigit(raw[index])) index++;
            if (index == exponentStart) return false;
            exponent = BigInteger.Parse(raw[exponentStart..index], System.Globalization.CultureInfo.InvariantCulture);
            if (exponentNegative) exponent = -exponent;
        }

        if (index != raw.Length) return false;

        var digits = (integerDigits + fractionDigits).TrimStart('0');
        if (digits.Length == 0)
        {
            number = new ExactNumber(BigInteger.Zero, BigInteger.Zero);
            return true;
        }

        var significand = BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        if (negative) significand = -significand;
        var power = exponent - fractionDigits.Length;
        while (!significand.IsZero && significand % 10 == 0)
        {
            significand /= 10;
            power++;
        }

        number = new ExactNumber(significand, power);
        return true;
    }

    public static bool TryParse(JsonNode value, bool allowNumericString, out ExactNumber number)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            {
                return TryParse(value.ToJsonString(), out number);
            }

            if (allowNumericString && jsonValue.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                return TryParse(jsonValue.GetValue<string>(), out number);
            }
        }

        number = default;
        return false;
    }

    public string ToCanonicalString()
    {
        if (Significand.IsZero) return "0";

        var negative = Significand.Sign < 0;
        var digits = BigInteger.Abs(Significand).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var decimalPoint = PowerOfTen + digits.Length;
        var prefix = negative ? "-" : string.Empty;

        if (decimalPoint >= -28 && decimalPoint <= 29)
        {
            if (decimalPoint <= 0)
            {
                return prefix + "0." + new string('0', checked((int)-decimalPoint)) + digits;
            }

            if (decimalPoint >= digits.Length)
            {
                return prefix + digits + new string('0', checked((int)(decimalPoint - digits.Length)));
            }

            var point = checked((int)decimalPoint);
            return prefix + digits[..point] + "." + digits[point..];
        }

        var significand = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
        return prefix + significand + "e" + (decimalPoint - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool EqualsValue(ExactNumber other) => Significand == other.Significand && PowerOfTen == other.PowerOfTen;
}
