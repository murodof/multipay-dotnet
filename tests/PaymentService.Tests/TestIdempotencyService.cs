using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Services;

namespace PaymentService.Tests;

[Collection("app")]
public sealed class TestIdempotencyService(Containers containers) : AppTest(containers)
{
    private (IdempotencyService Service, IdempotencyClaims Claims, IServiceScope Scope) Resolve()
    {
        var scope = App.Services.CreateScope();
        return (scope.ServiceProvider.GetRequiredService<IdempotencyService>(), scope.ServiceProvider.GetRequiredService<IdempotencyClaims>(), scope);
    }

    [Fact]
    public async Task Reused_key_with_different_body_is_rejected()
    {
        var (service, _, scope) = Resolve();
        using var _scope = scope;
        var key = Guid.NewGuid().ToString();
        var firstCard = new CardSchema("8600123412341239", "12/29").ModelDump();
        var otherCard = new CardSchema("8600123412341239", "11/29").ModelDump();

        var first = await service.Start("session:card", key, firstCard);
        await service.Complete(first.CacheKey, new SuccessResponse(new Dictionary<string, object?> { ["otp_ttl"] = 59 }));
        var replay = await service.Start("session:card", key, firstCard);
        var conflict = await service.Start("session:card", key, otherCard);

        Assert.Equal("""{"otp_ttl":59}""", Assert.IsType<SuccessResponse>(replay.Response).ResultNode!.ToJsonString());
        Assert.Equal("IDEMPOTENCY_CONFLICT", Assert.IsType<ErrorResponse>(conflict.Response).Code);
    }

    [Fact]
    public async Task Old_claim_cannot_overwrite_new_owner()
    {
        var (service, _, scope) = Resolve();
        using var _scope = scope;
        var first = await service.Start("session:expire", Guid.NewGuid().ToString());
        await App.Redis.SetValue(first.CacheKey!.Key, "foreign-owner", 600);

        await service.Complete(first.CacheKey, new SuccessResponse(new Dictionary<string, object?> { ["status"] = "EXPIRED" }));

        Assert.Equal("foreign-owner", await App.Redis.Get(first.CacheKey.Key));
    }

    [Fact]
    public async Task Exception_cleanup_releases_only_owned_claim()
    {
        var (service, claims, scope) = Resolve();
        using var _scope = scope;
        var key = Guid.NewGuid().ToString();
        var first = await service.Start("session:verify", key);
        Assert.NotNull(first.CacheKey);

        await claims.AbortOpenClaims();

        var second = await service.Start("session:verify", key);
        Assert.NotNull(second.CacheKey);
        Assert.NotEqual(first.CacheKey.Value, second.CacheKey!.Value);
        await service.Complete(second.CacheKey, new SuccessResponse(new Dictionary<string, object?> { ["status"] = "SUCCESS" }));
    }
}
