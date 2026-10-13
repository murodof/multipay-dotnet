using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaymentService.Domain.Schemas;

public sealed record CardSchema(string Pan, string Expiry) : IRequestSchema<CardSchema>
{
    public static CardSchema? Validate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("pan", out var pan) || pan.ValueKind != JsonValueKind.String
            || !body.TryGetProperty("expiry", out var expiry) || expiry.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var panText = pan.GetString()!;
        var expiryText = expiry.GetString()!;
        var expiryRunes = expiryText.EnumerateRunes().ToArray();
        var panValid = panText.Length == 16 && panText.All(char.IsAsciiDigit);
        var expiryValid = expiryRunes.Length == 5
            && expiryRunes[2].Value == '/'
            && expiryRunes[..2].All(IsDigit)
            && expiryRunes[3..].All(IsDigit);
        return panValid && expiryValid ? new CardSchema(panText, expiryText) : null;
    }

    public IReadOnlyDictionary<string, object?> ModelDump() => new Dictionary<string, object?>
    {
        ["pan"] = Pan,
        ["expiry"] = Expiry,
    };

    private static bool IsDigit(Rune rune) =>
        CharUnicodeInfo.GetDigitValue(rune.ToString(), 0) >= 0;
}

public sealed partial record VerifySchema(string Code, bool PaymentConfirmed) : IRequestSchema<VerifySchema>
{
    public static VerifySchema? Validate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String
            || !body.TryGetProperty("payment_confirmed", out var confirmed))
        {
            return null;
        }
        var codeText = code.GetString()!;
        var codeValid = codeText.Length is >= 6 and <= 8 && CodePattern().IsMatch(codeText);
        var flag = LaxBool(confirmed);
        return codeValid && flag is not null ? new VerifySchema(codeText, flag.Value) : null;
    }

    public IReadOnlyDictionary<string, object?> ModelDump() => new Dictionary<string, object?>
    {
        ["code"] = Code,
        ["payment_confirmed"] = PaymentConfirmed,
    };

    private static bool? LaxBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetDecimal(out var number) && number is 0m or 1m => number == 1m,
        JsonValueKind.String => value.GetString()!.ToLowerInvariant() switch
        {
            "1" or "on" or "t" or "true" or "y" or "yes" => true,
            "0" or "off" or "f" or "false" or "n" or "no" => false,
            _ => null,
        },
        _ => null,
    };

    [GeneratedRegex(@"\A[A-Za-z0-9]+\z")]
    private static partial Regex CodePattern();
}

public sealed record OtpSentResponse(
    string Processing,
    string PhoneMask,
    int OtpTtl,
    int ResendAfter,
    int AttemptsLeft,
    int ResendsLeft);

public sealed record ProcessingResponse(string Status, int? PollAfter = null, object? Receipt = null);

public sealed record SessionStatusResponse(
    string Status,
    string? Processing,
    DateTimeOffset? UpdatedAt,
    object? Receipt,
    object? Error);
