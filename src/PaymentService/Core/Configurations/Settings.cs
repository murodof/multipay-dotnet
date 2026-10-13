using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PaymentService.Core.Middlewares;

namespace PaymentService.Core.Configurations;

public sealed class Settings
{
    public string DATABASE_URL { get; set; } = "";
    public string REDIS_URL { get; set; } = "";
    public string ENVIRONMENT { get; set; } = "development";

    public string SECRET_KEYS { get; set; } = "";
    public string ALLOWED_ORIGINS { get; set; } = "";
    public string FORWARDED_ALLOW_IPS { get; set; } = "127.0.0.1";

    public long MIN_AMOUNT { get; set; } = 100_000;
    public long MAX_AMOUNT { get; set; } = 100_000_000;
    public decimal COMMISSION_PERCENT { get; set; }

    public int HASH_TS_WINDOW_SECONDS { get; set; } = 300;
    public int NONCE_TTL_SECONDS { get; set; } = 900;
    public int SESSION_TTL_SECONDS { get; set; } = 600;
    public int SESSION_IDLE_TIMEOUT_SECONDS { get; set; } = 180;
    public int IDEMPOTENCY_TTL_SECONDS { get; set; } = 600;
    public int OTP_TTL_SECONDS { get; set; } = 59;
    public int OTP_LOCK_SECONDS { get; set; } = 14_400;
    public string PAN_HASH_PEPPER { get; set; } = "development-pan-pepper-change-in-production";

    public string SVGATE_URL { get; set; } = "";
    public string SVGATE_USERNAME { get; set; } = "";
    public string SVGATE_PASSWORD { get; set; } = "";
    public int SVGATE_TIMEOUT_SECONDS { get; set; } = 25;
    public int SVGATE_TEMPLATE_ID { get; set; } = 1;
    public string SVGATE_SERVICE_NAME { get; set; } = "Multipay";
    public string SVGATE_MERCHANT_ID { get; set; } = "";
    public string SVGATE_TERMINAL_ID { get; set; } = "";
    public string SVGATE_MCC { get; set; } = "4814";
    public string SVGATE_LEGAL_NAME { get; set; } = "";
    public int SVGATE_LEGAL_TYPE { get; set; } = 1;
    public string SVGATE_LEGAL_ID { get; set; } = "";
    public string SVGATE_LEGAL_OKED { get; set; } = "";

    public bool HUMO_ENABLED { get; set; }
    public string HUMO_MW_URL { get; set; } = "";
    public string HUMO_SMS_URL { get; set; } = "";
    public string HUMO_BEARER_TOKEN { get; set; } = "";
    public string HUMO_PS_URL { get; set; } = "";
    public string HUMO_PS_USERNAME { get; set; } = "";
    public string HUMO_PS_PASSWORD { get; set; } = "";
    public string HUMO_MERCHANT_ID { get; set; } = "";
    public string HUMO_TERMINAL_ID { get; set; } = "";
    public string HUMO_CENTRE_ID { get; set; } = "";
    public string HUMO_SERVICE_NAME { get; set; } = "Multipay";
    public string HUMO_PAN_ENCRYPTION_KEY { get; set; } = "";
    public bool HUMO_BALANCE_PRECHECK { get; set; }

    public string OPERATOR_URL { get; set; } = "";
    public string OPERATOR_TOKEN { get; set; } = "";
    public int OPERATOR_TIMEOUT_SECONDS { get; set; } = 10;

    public int RATE_LIMIT_IP_PER_MINUTE { get; set; } = 10;
    public int RATE_LIMIT_IP_PER_HOUR { get; set; } = 60;
    public int RATE_LIMIT_PHONE_PER_10_MINUTES { get; set; } = 5;

    public long HSTS_MAX_AGE_SECONDS { get; set; } = 31_536_000;
    public bool DOCS_ENABLED { get; set; }
    public bool ADMIN_ENABLED { get; set; }
    public string ADMIN_USERNAME { get; set; } = "admin";
    public string ADMIN_PASSWORD { get; set; } = "";
    public string ADMIN_SECRET_KEY { get; set; } = "";
    public bool ADMIN_COOKIE_SECURE { get; set; } = true;
    public string LOG_LEVEL { get; set; } = "INFO";
    public string LOG_FILE { get; set; } = "logs/payment.log";
    public long LOG_MAX_BYTES { get; set; } = 10_485_760;
    public int LOG_BACKUP_COUNT { get; set; } = 5;

    public List<string> LoadErrors { get; } = [];

    public static Settings Load(IConfiguration configuration)
    {
        var settings = new Settings();
        foreach (var property in typeof(Settings).GetProperties().Where(property => property.CanWrite))
        {
            var raw = configuration[property.Name];
            if (raw is null)
            {
                continue;
            }
            object? value = property.PropertyType switch
            {
                var type when type == typeof(string) => raw,
                var type when type == typeof(int) => ParseInteger(raw) is { } number && number is >= int.MinValue and <= int.MaxValue ? (int)number : null,
                var type when type == typeof(long) => ParseInteger(raw),
                var type when type == typeof(decimal) => ParseDecimal(raw),
                var type when type == typeof(bool) => ParseBool(raw),
                _ => null,
            };
            if (value is null)
            {
                settings.LoadErrors.Add($"{property.Name}: invalid value");
                continue;
            }
            property.SetValue(settings, value);
        }
        foreach (var required in new[] { nameof(DATABASE_URL), nameof(REDIS_URL), nameof(SECRET_KEYS) })
        {
            if (configuration[required] is null)
            {
                settings.LoadErrors.Add($"{required}: field required");
            }
        }
        if (configuration[nameof(ENVIRONMENT)] is { } environment && environment is not ("development" or "production"))
        {
            settings.LoadErrors.Add("ENVIRONMENT: input should be 'development' or 'production'");
        }
        return settings;
    }

    public IEnumerable<string> ConsistencyErrors()
    {
        if (NONCE_TTL_SECONDS < 2L * HASH_TS_WINDOW_SECONDS)
        {
            yield return "NONCE_TTL_SECONDS must be at least 2 * HASH_TS_WINDOW_SECONDS";
        }
        if (!(0 < MIN_AMOUNT && MIN_AMOUNT <= MAX_AMOUNT))
        {
            yield return "MIN_AMOUNT and MAX_AMOUNT must satisfy 0 < MIN_AMOUNT <= MAX_AMOUNT";
        }
        if (COMMISSION_PERCENT < 0)
        {
            yield return "COMMISSION_PERCENT must not be negative";
        }
        if (ADMIN_ENABLED && (ADMIN_USERNAME.Trim().Length == 0 || ADMIN_PASSWORD.Length < 16 || ADMIN_SECRET_KEY.Length < 32))
        {
            yield return "ADMIN_ENABLED requires ADMIN_USERNAME, ADMIN_PASSWORD (16+ chars), and ADMIN_SECRET_KEY (32+ chars)";
        }
        if (!(0 < SESSION_IDLE_TIMEOUT_SECONDS && SESSION_IDLE_TIMEOUT_SECONDS <= SESSION_TTL_SECONDS))
        {
            yield return "SESSION_IDLE_TIMEOUT_SECONDS must be within the session lifetime";
        }
        if (!(0 < OTP_TTL_SECONDS && OTP_TTL_SECONDS <= 59))
        {
            yield return "OTP_TTL_SECONDS must be between 1 and 59";
        }
        if (ENVIRONMENT == "production" && (
            PAN_HASH_PEPPER == "development-pan-pepper-change-in-production"
            || PAN_HASH_PEPPER.StartsWith("replace-with-", StringComparison.Ordinal)
            || PAN_HASH_PEPPER.Length < 32))
        {
            yield return "production requires a unique PAN_HASH_PEPPER of at least 32 characters";
        }
        if (HUMO_ENABLED && new[]
            {
                HUMO_MW_URL, HUMO_SMS_URL, HUMO_BEARER_TOKEN, HUMO_PS_URL, HUMO_PS_USERNAME, HUMO_PS_PASSWORD,
                HUMO_MERCHANT_ID, HUMO_TERMINAL_ID, HUMO_CENTRE_ID, HUMO_PAN_ENCRYPTION_KEY,
            }.Any(string.IsNullOrEmpty))
        {
            yield return "HUMO_ENABLED requires all HUMO credentials, endpoints and encryption key";
        }
        else if (HUMO_ENABLED && PanEncryptionKey(HUMO_PAN_ENCRYPTION_KEY) is null)
        {
            yield return "HUMO_PAN_ENCRYPTION_KEY must be a base64-encoded 32-byte key";
        }
    }

    public IEnumerable<string> StartupErrors()
    {
        foreach (var error in LoadErrors.Concat(ConsistencyErrors()))
        {
            yield return error;
        }
        var signing = SigningKeys.Parse(SECRET_KEYS);
        if (signing.Error is not null)
        {
            yield return signing.Error;
        }
        if (SecurityOrigins.Parse(ALLOWED_ORIGINS, ENVIRONMENT).Error is { } originError)
        {
            yield return originError;
        }
        if (LogLevels.Parse(LOG_LEVEL) is null)
        {
            yield return $"LOG_LEVEL: unknown level {LOG_LEVEL}";
        }
        string? connectionError = null;
        try
        {
            _ = new SqlConnectionStringBuilder(DATABASE_URL);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or KeyNotFoundException)
        {
            connectionError = "DATABASE_URL must be a SQL Server connection string";
        }
        if (connectionError is not null)
        {
            yield return connectionError;
        }
        if (RedisUrl.Parse(REDIS_URL) is null)
        {
            yield return "REDIS_URL must be a redis:// or rediss:// URL";
        }
    }

    public static byte[]? PanEncryptionKey(string value)
    {
        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        try
        {
            var key = Convert.FromBase64String(normalized);
            return key.Length == 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static long? ParseInteger(string raw)
    {
        var text = WithoutDigitSeparators(raw.Trim());
        if (text is null)
        {
            return null;
        }
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
            && real == decimal.Truncate(real) && real is >= long.MinValue and <= long.MaxValue
            ? (long)real
            : null;
    }

    private static decimal? ParseDecimal(string raw)
    {
        var text = WithoutDigitSeparators(raw.Trim());
        return text is not null && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static string? WithoutDigitSeparators(string text) =>
        Regex.IsMatch(text, "(^_|_$|__|[^0-9]_|_[^0-9])") ? null : text.Replace("_", "", StringComparison.Ordinal);

    private static bool? ParseBool(string raw) => raw.ToLowerInvariant() switch
    {
        "1" or "on" or "t" or "true" or "y" or "yes" => true,
        "0" or "off" or "f" or "false" or "n" or "no" => false,
        _ => null,
    };
}

public sealed class SettingsValidator : IValidateOptions<Settings>
{
    public ValidateOptionsResult Validate(string? name, Settings options)
    {
        var errors = options.StartupErrors().ToList();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public static class DotEnv
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            else
            {
                var comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    value = value[..comment].TrimEnd();
                }
            }
            values[key] = value;
        }
        return values;
    }

    public static void AddTo(IConfigurationBuilder configuration, string path)
    {
        var values = Read(path)
            .Where(item => Environment.GetEnvironmentVariable(item.Key) is null)
            .ToDictionary(item => item.Key, item => (string?)item.Value);
        configuration.AddInMemoryCollection(values);
    }
}

public sealed record RedisUrl(string Host, int Port, int Database, string? User, string? Password, bool Ssl)
{
    public static RedisUrl? Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("redis" or "rediss") || uri.Host.Length == 0)
        {
            return null;
        }
        var database = 0;
        var path = uri.AbsolutePath.Trim('/');
        if (path.Length > 0 && !int.TryParse(path, NumberStyles.None, CultureInfo.InvariantCulture, out database))
        {
            return null;
        }
        string? user = null;
        string? password = null;
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = parts[0].Length > 0 ? Uri.UnescapeDataString(parts[0]) : null;
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        }
        return new RedisUrl(uri.Host.Trim('[', ']'), uri.IsDefaultPort ? 6379 : uri.Port, database, user, password, uri.Scheme == "rediss");
    }
}

public static class LogLevels
{
    public static Serilog.Events.LogEventLevel? Parse(string value) => value.ToUpperInvariant() switch
    {
        "DEBUG" or "NOTSET" => Serilog.Events.LogEventLevel.Debug,
        "INFO" => Serilog.Events.LogEventLevel.Information,
        "WARNING" or "WARN" => Serilog.Events.LogEventLevel.Warning,
        "ERROR" => Serilog.Events.LogEventLevel.Error,
        "CRITICAL" or "FATAL" => Serilog.Events.LogEventLevel.Fatal,
        _ => null,
    };
}
