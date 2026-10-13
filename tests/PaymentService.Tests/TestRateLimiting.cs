using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// Sliding windows in Redis: 10/minute and 60/hour per IP, 5 per 10 minutes per phone.
[Collection("app")]
public sealed class TestRateLimiting(Containers containers) : AppTest(containers)
{
    private static async Task FillIpMinute(HttpClient client, IDictionary<string, string>? headers = null)
    {
        for (var index = 0; index < 10; index++)
        {
            Assert.Equal("HASH_INVALID", await ErrorOf(await CreateSession(client, "garbage", headers: headers)));
        }
    }

    // ─── per IP ───

    // The IP is counted before the hash is parsed, so garbage costs the same as a real attempt.
    [Fact]
    public async Task An_ip_gets_ten_requests_a_minute_whatever_they_carry()
    {
        await FillIpMinute(Client);

        var limited = await CreateSession(Client, MakeHash());
        var error = (await limited.Json())["error"]!;

        Assert.Equal(429, (int)limited.StatusCode);
        Assert.Equal("RATE_LIMITED", error["code"]!.GetValue<string>());
        Assert.Equal(-2007, error["error_code"]!.GetValue<int>());
        Assert.Equal(60, error["details"]!["retry_after"]!.GetValue<long>());
        Assert.Equal("60", limited.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task Another_ip_is_not_affected()
    {
        await FillIpMinute(Client);

        var response = await CreateSession(ClientFrom("198.51.100.7"), MakeHash());

        Assert.True((await response.Json())["success"].IsTrue());
    }

    [Fact]
    public async Task The_minute_window_slides()
    {
        await FillIpMinute(Client);

        App.Clock.Advance(TimeSpan.FromSeconds(30));
        var limited = await CreateSession(Client, MakeHash());
        App.Clock.Advance(TimeSpan.FromSeconds(30));
        var allowed = await CreateSession(Client, MakeHash());

        Assert.Equal(30, (await limited.Json())["error"]!["details"]!["retry_after"]!.GetValue<long>());
        Assert.True((await allowed.Json())["success"].IsTrue());
    }

    [Fact]
    public async Task Sixty_an_hour_even_when_spread_across_minutes()
    {
        for (var index = 0; index < 6; index++)
        {
            await FillIpMinute(Client);
            App.Clock.Advance(TimeSpan.FromSeconds(61));
        }

        var limited = await CreateSession(Client, MakeHash());

        Assert.Equal("RATE_LIMITED", await ErrorOf(limited));
        Assert.Equal(3600 - 6 * 61, (await limited.Json())["error"]!["details"]!["retry_after"]!.GetValue<long>());
    }

    // Refused hits are not recorded; otherwise a client retrying in a loop would never get back in.
    // The retries happen half-way through the window on purpose: made at the same instant as the
    // original ten, recorded retries would slide out together with them and the bug would hide.
    [Fact]
    public async Task A_refused_request_does_not_extend_the_block()
    {
        await FillIpMinute(Client);
        App.Clock.Advance(TimeSpan.FromSeconds(30));
        for (var index = 0; index < 20; index++)
        {
            Assert.Equal("RATE_LIMITED", await ErrorOf(await CreateSession(Client, "garbage")));
        }

        App.Clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True((await (await CreateSession(Client, MakeHash())).Json())["success"].IsTrue());
    }

    [Fact]
    public async Task A_limited_request_does_not_burn_its_nonce()
    {
        await FillIpMinute(Client);
        var hash = MakeHash();

        Assert.Equal("RATE_LIMITED", await ErrorOf(await CreateSession(Client, hash)));
        App.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True((await (await CreateSession(Client, hash)).Json())["success"].IsTrue());
    }

    // Only FORWARDED_ALLOW_IPS may name the client; otherwise rotating the header resets the limit.
    [Fact]
    public async Task X_forwarded_for_from_an_untrusted_peer_is_ignored()
    {
        for (var index = 0; index < 10; index++)
        {
            await CreateSession(Client, "garbage", headers: new Dictionary<string, string> { ["X-Forwarded-For"] = $"192.0.2.{index}" });
        }

        var response = await CreateSession(Client, "garbage", headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "192.0.2.99" });

        Assert.Equal("RATE_LIMITED", await ErrorOf(response));
    }

    [Fact]
    public async Task A_trusted_proxy_is_limited_per_forwarded_client()
    {
        var proxy = ClientFrom(TRUSTED_PROXY_IP);
        await FillIpMinute(proxy, new Dictionary<string, string> { ["X-Forwarded-For"] = "192.0.2.1" });

        var same = await CreateSession(proxy, "garbage", headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "192.0.2.1" });
        var other = await CreateSession(proxy, "garbage", headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "192.0.2.2" });

        Assert.Equal("RATE_LIMITED", await ErrorOf(same));
        Assert.Equal("HASH_INVALID", await ErrorOf(other));
    }

    // IIS Application Request Routing writes the client as `ip:port`, a new port per connection. The
    // port must not make one client look like many, and the stored address is the bare IP.
    [Fact]
    public async Task A_forwarded_client_with_a_port_is_one_client()
    {
        var proxy = ClientFrom(TRUSTED_PROXY_IP);
        for (var index = 0; index < 10; index++)
        {
            await CreateSession(proxy, "garbage", headers: new Dictionary<string, string> { ["X-Forwarded-For"] = $"192.0.2.1:{50000 + index}" });
        }

        var same = await CreateSession(proxy, "garbage", headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "192.0.2.1:60000" });
        var other = await CreateSession(proxy, MakeHash(), headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "192.0.2.2:60000" });

        Assert.Equal("RATE_LIMITED", await ErrorOf(same));
        Assert.True((await other.Json())["success"].IsTrue());
        Assert.Equal("192.0.2.2", (await App.StoredSessions()).Single().Ip);
    }

    // ─── per phone ───

    [Fact]
    public async Task A_phone_gets_five_attempts_per_ten_minutes_across_ips()
    {
        for (var index = 0; index < 5; index++)
        {
            Assert.True((await (await CreateSession(ClientFrom($"198.51.100.{index}"), MakeHash())).Json())["success"].IsTrue());
        }

        var limited = await CreateSession(ClientFrom("198.51.100.50"), MakeHash());
        var otherPhone = await CreateSession(ClientFrom("198.51.100.51"), MakeHash(("phone", "998907654321")));
        App.Clock.Advance(TimeSpan.FromSeconds(600));
        var afterWindow = await CreateSession(ClientFrom("198.51.100.52"), MakeHash());

        Assert.Equal("RATE_LIMITED", await ErrorOf(limited));
        Assert.Equal(600, (await limited.Json())["error"]!["details"]!["retry_after"]!.GetValue<long>());
        Assert.True((await otherPhone.Json())["success"].IsTrue());
        Assert.True((await afterWindow.Json())["success"].IsTrue());
    }

    // The phone comes from the payload, so it is counted only once the MAC and the time check pass.
    [Fact]
    public async Task Forged_hashes_cannot_exhaust_a_victims_phone_limit()
    {
        for (var index = 0; index < 10; index++)
        {
            var forged = await CreateSession(ClientFrom($"198.51.100.{index}"), App.MakeHash(secret: OTHER_SECRET, fields: new() { ["phone"] = PHONE }));
            Assert.Equal("HASH_INVALID", await ErrorOf(forged));
        }

        var genuine = await CreateSession(ClientFrom("198.51.100.99"), MakeHash(("phone", PHONE)));

        Assert.True((await genuine.Json())["success"].IsTrue());
    }

    // Redis is not a place for PII; the key carries a truncated SHA-256 of the phone.
    [Fact]
    public async Task Phone_limit_keys_do_not_store_the_number()
    {
        await CreateSession(Client, MakeHash(("phone", PHONE)));

        var redis = await App.Redis.Redis();
        var keys = ((StackExchange.Redis.RedisResult[])(await redis.ScriptEvaluateAsync("return redis.call('KEYS', ARGV[1])", [], ["rate:*"]))!).Select(key => key.ToString()).ToList();

        Assert.Contains(keys, key => key.StartsWith("rate:{phone:", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, key => key.Contains(PHONE, StringComparison.Ordinal));
    }
}
