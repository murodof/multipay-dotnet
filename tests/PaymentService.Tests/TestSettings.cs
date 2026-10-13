using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;

namespace PaymentService.Tests;

// Every start-up rule of Settings, plus the proof that a broken value stops the host.
public sealed class TestSettings
{
    private static List<string> Errors(Dictionary<string, string?> values) =>
        TestPanPepperSettings.SettingsFor(values).StartupErrors().ToList();

    [Fact]
    public void The_defaults_from_env_example_are_accepted() => Assert.Empty(Errors([]));

    // Once a nonce key expires, every hash that carried it must already be outside the time window.
    [Fact]
    public void The_nonce_must_outlive_two_time_windows() =>
        Assert.Contains("NONCE_TTL_SECONDS must be at least 2 * HASH_TS_WINDOW_SECONDS", Errors(new() { ["NONCE_TTL_SECONDS"] = "599" }));

    [Theory]
    [InlineData("0", "100")]
    [InlineData("200", "100")]
    public void The_amount_range_must_be_ordered_and_positive(string min, string max) =>
        Assert.Contains("MIN_AMOUNT and MAX_AMOUNT must satisfy 0 < MIN_AMOUNT <= MAX_AMOUNT", Errors(new() { ["MIN_AMOUNT"] = min, ["MAX_AMOUNT"] = max }));

    [Fact]
    public void Commission_cannot_be_negative() =>
        Assert.Contains("COMMISSION_PERCENT must not be negative", Errors(new() { ["COMMISSION_PERCENT"] = "-0.1" }));

    [Fact]
    public void The_admin_needs_real_credentials() =>
        Assert.Contains(Errors(new() { ["ADMIN_ENABLED"] = "true", ["ADMIN_PASSWORD"] = "short" }), error => error.StartsWith("ADMIN_ENABLED requires", StringComparison.Ordinal));

    [Theory]
    [InlineData("0", "600")]
    [InlineData("601", "600")]
    public void The_idle_timeout_fits_in_the_session(string idle, string ttl) =>
        Assert.Contains("SESSION_IDLE_TIMEOUT_SECONDS must be within the session lifetime", Errors(new() { ["SESSION_IDLE_TIMEOUT_SECONDS"] = idle, ["SESSION_TTL_SECONDS"] = ttl }));

    [Theory]
    [InlineData("0")]
    [InlineData("60")]
    public void The_otp_ttl_stays_under_a_minute(string ttl) =>
        Assert.Contains("OTP_TTL_SECONDS must be between 1 and 59", Errors(new() { ["OTP_TTL_SECONDS"] = ttl }));

    [Fact]
    public void Humo_needs_every_credential() =>
        Assert.Contains("HUMO_ENABLED requires all HUMO credentials, endpoints and encryption key", Errors(new() { ["HUMO_ENABLED"] = "true" }));

    [Theory]
    [InlineData("not-base64!")]
    [InlineData("c2hvcnQ=")]
    public void The_humo_pan_key_must_be_32_bytes(string key)
    {
        var values = new Dictionary<string, string?>
        {
            ["HUMO_ENABLED"] = "true",
            ["HUMO_MW_URL"] = "x",
            ["HUMO_SMS_URL"] = "x",
            ["HUMO_BEARER_TOKEN"] = "x",
            ["HUMO_PS_URL"] = "x",
            ["HUMO_PS_USERNAME"] = "x",
            ["HUMO_PS_PASSWORD"] = "x",
            ["HUMO_MERCHANT_ID"] = "x",
            ["HUMO_TERMINAL_ID"] = "x",
            ["HUMO_CENTRE_ID"] = "x",
            ["HUMO_PAN_ENCRYPTION_KEY"] = key,
        };

        Assert.Contains("HUMO_PAN_ENCRYPTION_KEY must be a base64-encoded 32-byte key", Errors(values));
    }

    [Theory]
    [InlineData("MIN_AMOUNT", "lots")]
    [InlineData("HUMO_ENABLED", " true")]
    [InlineData("ENVIRONMENT", "staging")]
    [InlineData("SECRET_KEYS", "k1:short")]
    [InlineData("ALLOWED_ORIGINS", "http://operator.example.uz")]
    [InlineData("LOG_LEVEL", "LOUD")]
    [InlineData("REDIS_URL", "localhost:6379")]
    [InlineData("DATABASE_URL", "postgresql+asyncpg://payment:payment@localhost/payment")]
    public void A_malformed_value_is_reported(string key, string value) =>
        Assert.NotEmpty(Errors(new() { [key] = value }));

    // pydantic coerces these spellings the same way.
    [Fact]
    public void Values_are_coerced_like_pydantic()
    {
        var settings = TestPanPepperSettings.SettingsFor(new()
        {
            ["MIN_AMOUNT"] = " 100_000 ",
            ["MAX_AMOUNT"] = "5.0",
            ["COMMISSION_PERCENT"] = "1e0",
            ["DOCS_ENABLED"] = "Yes",
        });

        Assert.Equal((100_000L, 5L, 1m, true), (settings.MIN_AMOUNT, settings.MAX_AMOUNT, settings.COMMISSION_PERCENT, settings.DOCS_ENABLED));
    }

    [Fact]
    public void A_required_value_missing_is_reported()
    {
        var settings = Settings.Load(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        Assert.Contains("SECRET_KEYS: field required", settings.StartupErrors());
    }

    [Fact]
    public void The_host_refuses_to_start_with_an_invalid_setting()
    {
        var validator = new SettingsValidator();
        var result = validator.Validate(null, TestPanPepperSettings.SettingsFor(new() { ["NONCE_TTL_SECONDS"] = "10" }));

        Assert.True(result.Failed);
    }
}

[Collection("app")]
public sealed class TestSettingsStartup(Containers containers) : AppTest(containers)
{
    [Fact]
    public void The_application_does_not_start_with_an_invalid_setting()
    {
        App.Settings["NONCE_TTL_SECONDS"] = "10";

        var error = Assert.ThrowsAny<OptionsValidationException>(() => App.Client());
        Assert.Contains("NONCE_TTL_SECONDS", error.Message, StringComparison.Ordinal);
    }
}
