using System.Globalization;
using System.Text;
using PaymentService.Domain.Enums;

namespace PaymentService.Infrastructure.Utils;

public static class Card
{
    public static bool LuhnValid(string pan)
    {
        var total = 0;
        var parity = pan.Length % 2;
        for (var index = 0; index < pan.Length; index++)
        {
            var digit = pan[index] - '0';
            if (index % 2 == parity)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }
            total += digit;
        }
        return total % 10 == 0;
    }

    public static bool UzcardPan(string pan) =>
        pan.StartsWith("8600", StringComparison.Ordinal) || pan.StartsWith("5614", StringComparison.Ordinal);

    public static bool HumoPan(string pan) => pan.StartsWith("9860", StringComparison.Ordinal);

    public static ProcessingSystem? ProcessingSystemOf(string pan) =>
        UzcardPan(pan) ? ProcessingSystem.UZCARD : HumoPan(pan) ? ProcessingSystem.HUMO : null;

    public static string? ExpiryToYymm(string expiry, DateTimeOffset now)
    {
        var runes = expiry.EnumerateRunes().ToArray();
        var month = DecimalValue(runes[..2]);
        var year = DecimalValue(runes[3..]);
        if (month is null || year is null)
        {
            return null;
        }
        var fullYear = 2000 + year.Value;
        if (month is < 1 or > 12 || (fullYear, month.Value).CompareTo((now.Year, now.Month)) < 0)
        {
            return null;
        }
        return $"{fullYear % 100:D2}{month.Value:D2}";
    }

    public static string PanDigest(string pan, string pepper) => Defaults.Sha256Hex($"{pan}{pepper}");

    public static string MaskPan(string pan) => $"{pan[..4]}********{pan[^4..]}";

    private static int? DecimalValue(Rune[] runes)
    {
        var value = 0;
        foreach (var rune in runes)
        {
            var digit = CharUnicodeInfo.GetDecimalDigitValue(rune.ToString(), 0);
            if (digit < 0)
            {
                return null;
            }
            value = value * 10 + digit;
        }
        return runes.Length > 0 ? value : null;
    }
}
