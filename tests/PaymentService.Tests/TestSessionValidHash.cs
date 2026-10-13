using System.Text.RegularExpressions;
using PaymentService.Domain.Enums;
using PaymentService.Infrastructure.Services;
using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// The success path of POST /api/uzcard/session: what comes back, what is stored, and what is not.
[Collection("app")]
public sealed class TestSessionValidHash(Containers containers) : AppTest(containers)
{
    [Fact]
    public async Task A_valid_hash_opens_a_session()
    {
        var response = await CreateSession(Client, MakeHash(("order_id", "order-100"), ("amount", 5_000_000)));

        Assert.Equal(200, (int)response.StatusCode);
        var body = await response.Json();
        Assert.True(body["success"].IsTrue());
        var result = body["result"]!;
        Assert.Equal("+998 90 *** ** 67", result["phone_mask"]!.GetValue<string>());
        Assert.Equal(5_000_000, result["amount"]!.GetValue<long>());
        Assert.Equal(75_000, result["commission"]!.GetValue<long>());
        Assert.Equal(5_075_000, result["total"]!.GetValue<long>());
        Assert.Equal(180, result["idle_timeout"]!.GetValue<int>());
        Assert.Equal(App.Clock.GetUtcNow() + TimeSpan.FromSeconds(600), DateTimeOffset.Parse(result["expires_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));

        var rows = await App.StoredSessions();
        Assert.Single(rows);
        Assert.Equal(result["session_id"]!.GetValue<string>(), rows[0].Id.ToString());
        Assert.Equal("order-100", rows[0].OrderId);
        Assert.Equal("998901234567", rows[0].Phone);
        Assert.Equal(PaymentSessionStatus.CREATED, rows[0].Status);
        Assert.Equal(App.Clock.GetUtcNow() + TimeSpan.FromSeconds(600), rows[0].ExpiresAt);
    }

    // 1.5% of 100_100 is 1501.5 → 1502; of 100_033 it is 1500.495 → 1500. Never through float.
    [Fact]
    public async Task Commission_rounds_half_up_to_a_whole_tiyin()
    {
        var up = (await (await CreateSession(Client, MakeHash(("amount", 100_100)))).Json())["result"]!;
        var down = (await (await CreateSession(Client, MakeHash(("amount", 100_033), ("phone", "998901234568")))).Json())["result"]!;

        Assert.Equal((1502L, 101_602L), (up["commission"]!.GetValue<long>(), up["total"]!.GetValue<long>()));
        Assert.Equal((1500L, 101_533L), (down["commission"]!.GetValue<long>(), down["total"]!.GetValue<long>()));
    }

    // A database dump must not hand out live sessions — the same rule x-go applies to refresh tokens.
    [Fact]
    public async Task The_token_is_128_bits_and_only_its_hash_is_stored()
    {
        var result = (await (await CreateSession(Client, MakeHash())).Json())["result"]!;
        var token = result["session_token"]!.GetValue<string>();

        Assert.Matches(new Regex(@"\A[A-Za-z0-9_-]{22}\z"), token);
        var row = (await App.StoredSessions())[0];
        Assert.Equal(SessionService.Hash(token), row.SessionTokenHash);
        var values = typeof(PaymentService.Domain.Models.PaymentSession).GetProperties().Select(property => $"{property.GetValue(row)}");
        Assert.DoesNotContain(token, values);
    }

    [Fact]
    public async Task Every_session_gets_its_own_token()
    {
        var first = (await (await CreateSession(Client, MakeHash())).Json())["result"]!;
        var second = (await (await CreateSession(Client, MakeHash(("phone", "998901234568")))).Json())["result"]!;

        Assert.NotEqual(first["session_token"]!.GetValue<string>(), second["session_token"]!.GetValue<string>());
        Assert.NotEqual(first["session_id"]!.GetValue<string>(), second["session_id"]!.GetValue<string>());
    }

    // The token travels in the body only; the frontend keeps it in memory and sends a Bearer header.
    [Fact]
    public async Task No_cookie_is_ever_set()
    {
        var response = await CreateSession(Client, MakeHash());

        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_hash_signed_with_the_outgoing_key_still_works_during_rotation()
    {
        var expiry = (App.Clock.GetUtcNow() + TimeSpan.FromHours(24)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        App.Settings["SECRET_KEYS"] = $"k2:{OTHER_SECRET},{KID}:{SECRET}@{expiry}";

        var old = await CreateSession(Client, MakeHash());
        var current = await CreateSession(Client, App.MakeHash(kid: "k2", secret: OTHER_SECRET, fields: new() { ["phone"] = "998901234568" }));

        Assert.True((await old.Json())["success"].IsTrue());
        Assert.True((await current.Json())["success"].IsTrue());
    }
}
