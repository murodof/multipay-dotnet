using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PaymentService.Core.Configurations;

public sealed record SigningKey(string Kid, byte[] Secret, DateTimeOffset? NotAfter)
{
    public bool IsActive(DateTimeOffset now) => NotAfter is null || now <= NotAfter;
}

public sealed record SigningKeys(IReadOnlyDictionary<string, SigningKey> Keys, string? Error)
{
    public static SigningKeys Parse(string value)
    {
        var keys = new Dictionary<string, SigningKey>(StringComparer.Ordinal);
        var entries = value.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
        for (var index = 0; index < entries.Count; index++)
        {
            var position = index + 1;
            var (kid, rest) = Partition(entries[index], ':');
            var (secret, notAfter) = Partition(rest, '@');
            if (!Regex.IsMatch(kid, @"\A[A-Za-z0-9_-]{1,32}\z") || keys.ContainsKey(kid))
            {
                return Failed($"SECRET_KEYS: entry #{position} has an invalid or duplicate kid");
            }
            if (!Regex.IsMatch(secret, @"\A[A-Za-z0-9_\-+/=]{32,}\z"))
            {
                return Failed($"SECRET_KEYS: secret of entry #{position} must be at least 32 base64/hex characters");
            }
            DateTimeOffset? expiry = null;
            if (notAfter.Length > 0)
            {
                if (!Regex.IsMatch(notAfter, @"(Z|[+-]\d{2}(:?\d{2})?)\z", RegexOptions.IgnoreCase))
                {
                    return Failed($"SECRET_KEYS: expiry of entry #{position} must carry a UTC offset");
                }
                if (!DateTimeOffset.TryParse(notAfter, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    return Failed($"SECRET_KEYS: expiry of entry #{position} is not an ISO 8601 instant");
                }
                expiry = parsed;
            }
            keys[kid] = new SigningKey(kid, Encoding.ASCII.GetBytes(secret), expiry);
        }
        return keys.Count == 0 ? Failed("SECRET_KEYS: at least one key is required") : new SigningKeys(keys, null);
    }

    private static SigningKeys Failed(string error) => new(new Dictionary<string, SigningKey>(), error);

    private static (string Head, string Tail) Partition(string value, char separator)
    {
        var index = value.IndexOf(separator, StringComparison.Ordinal);
        return index < 0 ? (value, "") : (value[..index], value[(index + 1)..]);
    }
}
