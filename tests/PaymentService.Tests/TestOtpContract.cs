using System.Text.Json;
using System.Text.Json.Nodes;
using PaymentService.Domain.Schemas;

namespace PaymentService.Tests;

public sealed class TestOtpContract
{
    private static T? Validate<T>(string json) where T : class, IRequestSchema<T>
    {
        using var document = JsonDocument.Parse(json);
        return T.Validate(document.RootElement);
    }

    private static VerifySchema? Verify(string code) =>
        Validate<VerifySchema>(new JsonObject { ["code"] = code, ["payment_confirmed"] = true }.ToJsonString());

    [Theory]
    [InlineData("A1B2C3")]
    [InlineData("1234567")]
    [InlineData("a1b2c3d4")]
    public void Otp_accepts_six_to_eight_ascii_alphanumeric_characters(string code) =>
        Assert.Equal(code, Verify(code)?.Code);

    [Theory]
    [InlineData("A1B2C")]
    [InlineData("A1B2C3D4E")]
    [InlineData("ABC 123")]
    [InlineData("ABC-123")]
    [InlineData("АBC123")]
    [InlineData("１２３４５６")]
    [InlineData("A1B2C3\n")]
    public void Otp_rejects_other_formats(string code) => Assert.Null(Verify(code));

    // pydantic's lax bool, probed on the Python service: these spellings are accepted or refused there too.
    [Theory]
    [InlineData("true", true)]
    [InlineData("\"yes\"", true)]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("0", false)]
    [InlineData("\"off\"", false)]
    public void Payment_confirmed_is_coerced_like_pydantic(string raw, bool expected) =>
        Assert.Equal(expected, Validate<VerifySchema>($$"""{"code":"A12B34","payment_confirmed":{{raw}}}""")?.PaymentConfirmed);

    [Theory]
    [InlineData("""{"code":"A12B34"}""")]
    [InlineData("""{"code":"A12B34","payment_confirmed":null}""")]
    [InlineData("""{"code":"A12B34","payment_confirmed":2}""")]
    [InlineData("""{"code":"A12B34","payment_confirmed":"maybe"}""")]
    [InlineData("""{"code":123456,"payment_confirmed":true}""")]
    [InlineData("""[]""")]
    public void A_malformed_verify_body_is_refused(string json) => Assert.Null(Validate<VerifySchema>(json));

    [Theory]
    [InlineData("""{"pan":"8600123412341239","expiry":"12/29"}""", true)]
    [InlineData("""{"pan":"8600123412341239","expiry":"١٢/29"}""", true)]
    [InlineData("""{"pan":8600123412341239,"expiry":"12/29"}""", false)]
    [InlineData("""{"pan":"８６００１２３４１２３４１２３９","expiry":"12/29"}""", false)]
    [InlineData("""{"pan":"860012341234123","expiry":"12/29"}""", false)]
    [InlineData("""{"pan":"8600123412341239","expiry":"1/299"}""", false)]
    [InlineData("""{"pan":"8600123412341239","expiry":"+1/29"}""", false)]
    [InlineData("""{"pan":"8600123412341239","expiry":"12/2x"}""", false)]
    [InlineData("""{"pan":"8600123412341239"}""", false)]
    public void Card_schema_follows_the_pydantic_validators(string json, bool valid) =>
        Assert.Equal(valid, Validate<CardSchema>(json) is not null);

    [Theory]
    [InlineData("""{"hash":null}""", true)]
    [InlineData("""{"hash":"x","extra":1}""", true)]
    [InlineData("""{}""", true)]
    [InlineData("""{"hash":1}""", false)]
    [InlineData("""{"hash":true}""", false)]
    [InlineData("""{"hash":["x"]}""", false)]
    [InlineData("""[]""", false)]
    public void Session_schema_accepts_only_a_string_or_missing_hash(string json, bool valid) =>
        Assert.Equal(valid, Validate<SessionCreateSchema>(json) is not null);
}
