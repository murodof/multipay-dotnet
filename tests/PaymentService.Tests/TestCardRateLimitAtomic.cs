using PaymentService.Infrastructure.Services;

namespace PaymentService.Tests;

[Collection("app")]
public sealed class TestCardRateLimitAtomic(Containers containers) : AppTest(containers)
{
    [Fact]
    public async Task Ip_rejection_does_not_consume_pan_quota()
    {
        using var scope = App.Services.CreateScope();
        var limiter = scope.ServiceProvider.GetRequiredService<RateLimitService>();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 10; index++)
        {
            Assert.Equal(0, await limiter.CardWait($"other-pan-{index}", "203.0.113.1", now));
        }

        Assert.True(await limiter.CardWait("target-pan", "203.0.113.1", now) > 0);
        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(0, await limiter.CardWait("target-pan", $"203.0.113.{index + 2}", now));
        }
        Assert.True(await limiter.CardWait("target-pan", "203.0.113.20", now) > 0);
    }
}
