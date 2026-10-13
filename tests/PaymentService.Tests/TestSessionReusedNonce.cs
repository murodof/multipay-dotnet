using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// Step (c): a nonce is single-use, claimed atomically with SET NX EX 900.
[Collection("app")]
public sealed class TestSessionReusedNonce(Containers containers) : AppTest(containers)
{
    private const string NONCE = "nonce-0123456789abcdef";

    [Fact]
    public async Task Replaying_the_same_hash_is_refused()
    {
        var hash = MakeHash();

        var first = await CreateSession(Client, hash);
        var replay = await CreateSession(Client, hash);

        Assert.True((await first.Json())["success"].IsTrue());
        Assert.Equal(409, (int)replay.StatusCode);
        Assert.Equal("NONCE_USED", await ErrorOf(replay));
        Assert.Equal(-2003, (await replay.Json())["error"]!["error_code"]!.GetValue<int>());
        Assert.Single(await App.StoredSessions());
    }

    [Fact]
    public async Task A_nonce_cannot_be_reused_for_a_different_order()
    {
        await CreateSession(Client, MakeHash(("nonce", NONCE), ("order_id", "order-1")));
        var response = await CreateSession(Client, MakeHash(("nonce", NONCE), ("order_id", "order-2")));

        Assert.Equal("NONCE_USED", await ErrorOf(response));
        Assert.Equal(["order-1"], (await App.StoredSessions()).Select(row => row.OrderId));
    }

    [Fact]
    public async Task The_nonce_is_kept_for_fifteen_minutes()
    {
        await CreateSession(Client, MakeHash(("nonce", NONCE)));

        Assert.Equal(900, await App.Redis.Ttl($"nonce:{NONCE}"));
    }

    // The nonce TTL (900s) outlives every hash that could still pass the ±300s check, so a replay
    // after the key expires meets HASH_EXPIRED instead of opening a second session. Settings refuse
    // to start with NONCE_TTL_SECONDS < 2 * HASH_TS_WINDOW_SECONDS for this reason.
    [Fact]
    public async Task Once_the_nonce_is_gone_the_hash_is_already_expired()
    {
        var hash = MakeHash(("nonce", NONCE));
        await CreateSession(Client, hash);

        await App.Redis.Delete($"nonce:{NONCE}");
        App.Clock.Advance(TimeSpan.FromSeconds(901));

        Assert.Equal("HASH_EXPIRED", await ErrorOf(await CreateSession(Client, hash)));
    }

    // The nonce is claimed before the order, phone and amount checks, so a correctly signed hash
    // with a bad phone cannot be retried as-is: the Operator must sign a new one. Documented in
    // CLAUDE.md as the accepted cost of never letting a replay reach the database.
    [Fact]
    public async Task A_hash_rejected_later_in_the_chain_has_still_spent_its_nonce()
    {
        var hash = MakeHash(("phone", "12345"));

        Assert.Equal("PHONE_INVALID", await ErrorOf(await CreateSession(Client, hash)));
        Assert.Equal("NONCE_USED", await ErrorOf(await CreateSession(Client, hash)));
    }
}
