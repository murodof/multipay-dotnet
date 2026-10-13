using System.Buffers.Text;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Schemas;
using StatusCodes = PaymentService.Domain.Enums.StatusCodes;

namespace PaymentService.Infrastructure.Utils;

public sealed record HashPayload(string Kid, string OrderId, string Phone, long Amount, long Ts, string Nonce);

public static class OperatorHash
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string B64UrlEncode(byte[] data) => Base64Url.EncodeToString(data);

    public static byte[]? B64UrlDecode(string value)
    {
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9_-]+\z") || value.Length % 4 == 1)
        {
            return null;
        }
        var padded = value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
        return B64UrlEncode(raw) == value ? raw : null;
    }

    public static byte[] Mac(byte[] key, string kid, string body) =>
        HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{kid}.{body}"));

    public static string SignHash(JsonObject payload, string kid, string secret) =>
        SignHash(payload, kid, Encoding.UTF8.GetBytes(secret));

    public static string SignHash(JsonObject payload, string kid, byte[] secret)
    {
        var body = B64UrlEncode(Encoding.UTF8.GetBytes(payload.ToJsonString(WireJson.Options)));
        return $"{kid}.{body}.{B64UrlEncode(Mac(secret, kid, body))}";
    }

    public static object VerifyHash(string? raw, DateTimeOffset now, SigningKeys keys, int orderIdLength)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 2048 || !Ascii.IsValid(raw) || raw.Count(character => character == '.') != 2)
        {
            return new ErrorResponse(StatusCodes.HASH_INVALID);
        }

        var parts = raw.Split('.');
        var (kid, body, signature) = (parts[0], parts[1], parts[2]);
        var provided = B64UrlDecode(signature);
        var key = keys.Keys.GetValueOrDefault(kid);
        var usable = key is not null && key.IsActive(now);
        var expected = Mac(usable ? key!.Secret : RandomNumberGenerator.GetBytes(32), kid, body);

        if (provided is null || !CryptographicOperations.FixedTimeEquals(expected, provided) || !usable)
        {
            return new ErrorResponse(StatusCodes.HASH_INVALID);
        }
        return ParsePayload(kid, body, orderIdLength);
    }

    public static object ParsePayload(string kid, string body, int orderIdLength)
    {
        var decoded = B64UrlDecode(body);
        JsonElement data;
        try
        {
            using var document = JsonDocument.Parse(decoded is null ? "" : StrictUtf8.GetString(StripBom(decoded)));
            data = document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException)
        {
            return Invalid("payload");
        }
        if (data.ValueKind != JsonValueKind.Object)
        {
            return Invalid("payload");
        }

        var orderId = StringField(data, "order_id");
        if (orderId is null || !Regex.IsMatch(orderId, $@"\A[A-Za-z0-9_.:-]{{1,{orderIdLength}}}\z"))
        {
            return Invalid("order_id");
        }
        var phone = StringField(data, "phone");
        if (phone is null)
        {
            return Invalid("phone");
        }
        var amount = IntegerField(data, "amount");
        if (amount is null)
        {
            return Invalid("amount");
        }
        var ts = IntegerField(data, "ts");
        if (ts is null || !(ts > 0 && ts < BigInteger.Pow(10, 11)))
        {
            return Invalid("ts");
        }
        var nonce = StringField(data, "nonce");
        if (nonce is null || !Regex.IsMatch(nonce, @"\A[A-Za-z0-9_-]{16,128}\z"))
        {
            return Invalid("nonce");
        }
        return new HashPayload(kid, orderId, phone, Clamp(amount.Value), (long)ts.Value, nonce);
    }

    public static bool IsFresh(HashPayload payload, DateTimeOffset now, int windowSeconds) =>
        Math.Abs(now.ToUnixTimeSeconds() - payload.Ts) <= windowSeconds;

    private static ErrorResponse Invalid(string field) =>
        new(StatusCodes.HASH_INVALID, details: new Dictionary<string, object?> { ["field"] = field });

    private static string? StringField(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return "\uFFFD";
        }
    }

    private static BigInteger? IntegerField(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        var text = value.GetRawText();
        return text.Any(character => character is '.' or 'e' or 'E')
            ? null
            : BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    private static long Clamp(BigInteger value) =>
        value > long.MaxValue ? long.MaxValue : value < long.MinValue ? long.MinValue : (long)value;

    private static byte[] StripBom(byte[] value) =>
        value.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? value[3..] : value;
}
