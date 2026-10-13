using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// Step (b): |now - ts| <= 300s, measured when the request arrives.
[Collection("app")]
public sealed class TestSessionExpiredTs(Containers containers) : AppTest(containers)
{
    private const string NONCE = "nonce-0123456789abcdef";

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    [InlineData(-3600)]
    public async Task A_timestamp_outside_the_window_is_expired(int offset)
    {
        var response = await CreateSession(Client, MakeHash(("ts", App.Ts + offset)));

        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("HASH_EXPIRED", await ErrorOf(response));
        Assert.Equal(-2002, (await response.Json())["error"]!["error_code"]!.GetValue<int>());
        Assert.Empty(await App.StoredSessions());
    }

    // 300s either way is allowed, so an Operator clock a few minutes fast still works.
    [Theory]
    [InlineData(-300)]
    [InlineData(300)]
    public async Task The_window_edges_are_inclusive(int offset)
    {
        var response = await CreateSession(Client, MakeHash(("ts", App.Ts + offset)));

        Assert.True((await response.Json())["success"].IsTrue());
    }

    [Fact]
    public async Task A_hash_that_ages_out_before_it_is_posted_is_expired()
    {
        var hash = MakeHash();
        App.Clock.Advance(TimeSpan.FromSeconds(301));

        Assert.Equal("HASH_EXPIRED", await ErrorOf(await CreateSession(Client, hash)));
    }

    // The nonce is claimed only after the time check, so a stale link does not poison a fresh one.
    [Fact]
    public async Task An_expired_hash_does_not_burn_its_nonce()
    {
        var expired = await CreateSession(Client, MakeHash(("nonce", NONCE), ("ts", App.Ts - 301)));
        var fresh = await CreateSession(Client, MakeHash(("nonce", NONCE)));

        Assert.Equal("HASH_EXPIRED", await ErrorOf(expired));
        Assert.True((await fresh.Json())["success"].IsTrue());
    }
}
