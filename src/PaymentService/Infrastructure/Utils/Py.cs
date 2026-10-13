using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaymentService.Infrastructure.Utils;

public static class Py
{
    public static JsonNode? Get(this JsonObject? data, string key) => data is not null && data.TryGetPropertyValue(key, out var value) ? value : null;

    public static bool Has(this JsonObject? data, string key) => data is not null && data.ContainsKey(key);

    public static string Str(JsonNode? value) => value switch
    {
        null => "None",
        JsonValue scalar => scalar.GetValueKind() switch
        {
            JsonValueKind.String => scalar.GetValue<string>(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => Number(scalar),
            _ => "None",
        },
        _ => Repr(value),
    };

    public static string StrGet(this JsonObject? data, string key, string missing) => data.Has(key) ? Str(data.Get(key)) : missing;

    public static string? StrOrNone(JsonNode? value) => value is null ? null : Str(value);

    public static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonObject item => item.Count > 0,
        JsonArray item => item.Count > 0,
        JsonValue scalar => scalar.GetValueKind() switch
        {
            JsonValueKind.String => scalar.GetValue<string>().Length > 0,
            JsonValueKind.True => true,
            JsonValueKind.Number => scalar.GetValue<double>() != 0,
            _ => false,
        },
        _ => true,
    };

    public static bool IsTrue(JsonNode? value) => value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.True;

    public static bool StrEquals(JsonNode? value, string text) => value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String && scalar.GetValue<string>() == text;

    public static bool CodeIn(JsonNode? value, params int[] codes)
    {
        if (value is not JsonValue scalar)
        {
            return false;
        }
        double? number = scalar.GetValueKind() switch
        {
            JsonValueKind.Number => scalar.GetValue<double>(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
        return number is not null && codes.Any(code => code == number);
    }

    public static long? Int(JsonNode? value)
    {
        if (value is not JsonValue scalar)
        {
            return null;
        }
        switch (scalar.GetValueKind())
        {
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.Number:
                var raw = scalar.ToJsonString();
                if (!raw.Any(character => character is '.' or 'e' or 'E'))
                {
                    return Clamp(BigInteger.Parse(raw, CultureInfo.InvariantCulture));
                }
                var real = scalar.GetValue<double>();
                return double.IsFinite(real) ? Clamp(new BigInteger(Math.Truncate(real))) : null;
            case JsonValueKind.String:
                return Int(scalar.GetValue<string>());
            default:
                return null;
        }
    }

    public static long? Int(string text)
    {
        var trimmed = text.Trim();
        var sign = 1;
        if (trimmed.Length > 0 && trimmed[0] is '+' or '-')
        {
            sign = trimmed[0] == '-' ? -1 : 1;
            trimmed = trimmed[1..];
        }
        if (trimmed.Length == 0 || trimmed[0] == '_' || trimmed[^1] == '_' || trimmed.Contains("__", StringComparison.Ordinal))
        {
            return null;
        }
        var value = BigInteger.Zero;
        foreach (var rune in trimmed.Replace("_", "", StringComparison.Ordinal).EnumerateRunes())
        {
            var digit = CharUnicodeInfo.GetDecimalDigitValue(rune.ToString(), 0);
            if (digit < 0)
            {
                return null;
            }
            value = value * 10 + digit;
        }
        return Clamp(sign * value);
    }

    private static long Clamp(BigInteger value) =>
        value > long.MaxValue ? long.MaxValue : value < long.MinValue ? long.MinValue : (long)value;

    private static string Number(JsonValue scalar)
    {
        var raw = scalar.ToJsonString();
        if (!raw.Any(character => character is '.' or 'e' or 'E'))
        {
            return BigInteger.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
        var real = scalar.GetValue<double>();
        if (double.IsInfinity(real))
        {
            return real > 0 ? "inf" : "-inf";
        }
        var text = real.ToString("R", CultureInfo.InvariantCulture);
        var exponent = text.IndexOf('E', StringComparison.Ordinal);
        if (exponent < 0)
        {
            return Math.Abs(real) >= 1e16 ? Scientific(real) : text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
        }
        return Scientific(real);
    }

    private static string Scientific(double real)
    {
        var text = real.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('E', StringComparison.Ordinal))
        {
            text = real.ToString("E16", CultureInfo.InvariantCulture);
            var mantissa = decimal.Parse(text[..text.IndexOf('E', StringComparison.Ordinal)], CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture);
            text = mantissa + text[text.IndexOf('E', StringComparison.Ordinal)..];
        }
        var parts = text.Split('E');
        var power = int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (power is > -5 and < 16)
        {
            return text;
        }
        return $"{parts[0]}e{(power < 0 ? "-" : "+")}{Math.Abs(power):D2}";
    }

    private static string Repr(JsonNode? value) => value switch
    {
        null => "None",
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => Quote(scalar.GetValue<string>()),
        JsonValue scalar => Str(scalar),
        JsonArray items => "[" + string.Join(", ", items.Select(Repr)) + "]",
        JsonObject items => "{" + string.Join(", ", items.Select(item => $"{Quote(item.Key)}: {Repr(item.Value)}")) + "}",
        _ => "None",
    };

    private static string Quote(string text)
    {
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var builder = new StringBuilder().Append(quote);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when character == quote => "\\" + quote,
                < ' ' => $"\\x{(int)character:x2}",
                _ => character.ToString(),
            });
        }
        return builder.Append(quote).ToString();
    }
}
