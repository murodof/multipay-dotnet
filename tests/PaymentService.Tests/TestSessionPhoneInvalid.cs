using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// Step (e): the phone is exactly 12 ASCII digits starting with 998 — no plus, no separators.
//
// Nothing is normalized here: the Operator signs the value, and rewriting a signed value would
// mean acting on something the Operator did not send.
[Collection("app")]
public sealed class TestSessionPhoneInvalid(Containers containers) : AppTest(containers)
{
    [Theory]
    [InlineData("+998901234567")]
    [InlineData("99890123456")]
    [InlineData("9989012345678")]
    [InlineData("997901234567")]
    [InlineData("998 90 123 45 67")]
    [InlineData("998٩٠١٢٣٤٥٦٧")]
    [InlineData("")]
    [InlineData("998901234567\n")]
    public async Task A_malformed_phone_is_refused(string phone)
    {
        var response = await CreateSession(Client, MakeHash(("phone", phone)));

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal("PHONE_INVALID", await ErrorOf(response));
        Assert.Equal(-2005, (await response.Json())["error"]!["error_code"]!.GetValue<int>());
        Assert.Empty(await App.StoredSessions());
    }
}
