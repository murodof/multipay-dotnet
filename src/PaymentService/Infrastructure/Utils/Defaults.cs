using System.Security.Cryptography;

namespace PaymentService.Infrastructure.Utils;

public static class Defaults
{
    public static readonly TimeSpan TASHKENT = TimeSpan.FromHours(5);

    public static DateTimeOffset UtcNow(this TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
    }

    public static string TokenUrlSafe(int bytes) =>
        System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    public static string TokenHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    public static string IsoFormat(DateTimeOffset value)
    {
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var microseconds = value.Ticks % TimeSpan.TicksPerSecond / 10;
        if (microseconds != 0)
        {
            text += "." + microseconds.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        }
        return text + value.ToString("zzz", System.Globalization.CultureInfo.InvariantCulture);
    }
}
