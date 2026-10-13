using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

[Collection("app")]
public sealed class TestSessionAmountOutOfRange(Containers containers) : AppTest(containers)
{
    [Theory]
    [InlineData(100_000 - 1)]
    [InlineData(100_000_000 + 1)]
    [InlineData(0)]
    [InlineData(-5_000_000)]
    public async Task An_amount_outside_the_range_is_refused(long amount)
    {
        var response = await CreateSession(Client, MakeHash(("amount", amount)));
        var body = await response.Json();

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal("AMOUNT_OUT_OF_RANGE", await ErrorOf(response));
        Assert.Equal(-2006, body["error"]!["error_code"]!.GetValue<int>());
        Assert.Equal("""{"min_amount":100000,"max_amount":100000000}""", body["error"]!["details"]!.ToJsonString());
        Assert.Empty(await App.StoredSessions());
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(100_000_000)]
    public async Task The_bounds_themselves_are_allowed(long amount)
    {
        var response = await CreateSession(Client, MakeHash(("amount", amount)));

        Assert.True((await response.Json())["success"].IsTrue());
    }

    // Money never passes through float or a decimal string; the Operator sends integer tiyin.
    [Fact]
    public async Task An_amount_that_is_not_an_integer_is_a_malformed_hash()
    {
        var response = await CreateSession(Client, MakeHash(("amount", "5000000")));

        Assert.Equal("HASH_INVALID", await ErrorOf(response));
        Assert.Equal("""{"field":"amount"}""", (await response.Json())["error"]!["details"]!.ToJsonString());
    }

    [Fact]
    public async Task The_phone_is_checked_before_the_amount()
    {
        var response = await CreateSession(Client, MakeHash(("phone", "bad"), ("amount", 1)));

        Assert.Equal("PHONE_INVALID", await ErrorOf(response));
    }
}
