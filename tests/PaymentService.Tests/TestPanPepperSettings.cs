using Microsoft.Extensions.Configuration;
using PaymentService.Core.Configurations;

namespace PaymentService.Tests;

public sealed class TestPanPepperSettings
{
    public static Settings SettingsFor(Dictionary<string, string?> values)
    {
        var all = new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Server=localhost;Database=test;User Id=sa;Password=x;TrustServerCertificate=True",
            ["REDIS_URL"] = "redis://localhost/0",
            ["SECRET_KEYS"] = "k1:test-secret-0123456789abcdefghijklmnop",
        };
        foreach (var (key, value) in values)
        {
            all[key] = value;
        }
        return Settings.Load(new ConfigurationBuilder().AddInMemoryCollection(all).Build());
    }

    private static List<string> ProductionErrors(string pepper) =>
        SettingsFor(new() { ["ENVIRONMENT"] = "production", ["PAN_HASH_PEPPER"] = pepper }).StartupErrors().ToList();

    [Theory]
    [InlineData("development-pan-pepper-change-in-production")]
    [InlineData("replace-with-a-separate-random-production-secret")]
    [InlineData("too-short")]
    public void Production_refuses_default_pan_pepper(string pepper) =>
        Assert.Contains(ProductionErrors(pepper), error => error.Contains("PAN_HASH_PEPPER", StringComparison.Ordinal));

    [Fact]
    public void Production_accepts_strong_pan_pepper() =>
        Assert.Empty(ProductionErrors(PaymentService.Infrastructure.Utils.Defaults.TokenUrlSafe(32)));
}
