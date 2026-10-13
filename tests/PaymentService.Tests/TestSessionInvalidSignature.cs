using System.Text;
using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// Step (a): the MAC. Nothing in the payload is acted on — no nonce claimed, no phone counted,
// no row read — until the signature has passed.
[Collection("app")]
public sealed class TestSessionInvalidSignature(Containers containers) : AppTest(containers)
{
    private const string NONCE = "nonce-0123456789abcdef";

    [Fact]
    public async Task A_hash_signed_with_the_wrong_secret_is_refused()
    {
        var response = await CreateSession(Client, App.MakeHash(secret: OTHER_SECRET));
        var body = await response.Json();

        Assert.Equal(401, (int)response.StatusCode);
        Assert.False(body["success"].IsTrue());
        Assert.Equal("HASH_INVALID", body["error"]!["code"]!.GetValue<string>());
        Assert.Equal(-2001, body["error"]!["error_code"]!.GetValue<int>());
        Assert.Empty(await App.StoredSessions());
    }

    [Fact]
    public async Task A_payload_swapped_under_a_valid_signature_is_refused()
    {
        var parts = MakeHash(("amount", 5_000_000)).Split('.');
        var cheaperBody = MakeHash(("amount", 100_000)).Split('.')[1];

        var response = await CreateSession(Client, $"{parts[0]}.{cheaperBody}.{parts[2]}");

        Assert.Equal("HASH_INVALID", await ErrorOf(response));
        Assert.Empty(await App.StoredSessions());
    }

    // `hash` is optional in the schema on purpose: a missing one must land in the envelope as
    // HASH_INVALID, not as a generic 422.
    [Theory]
    [InlineData("""{"hash":"garbage"}""")]
    [InlineData("""{"hash":""}""")]
    [InlineData("""{"hash":null}""")]
    [InlineData("""{}""")]
    public async Task A_missing_or_garbage_hash_is_invalid(string body)
    {
        var response = await Client.PostAsync("/api/uzcard/session", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("HASH_INVALID", await ErrorOf(response));
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_an_invalid_request()
    {
        var content = new ByteArrayContent("hash=abc"u8.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        var response = await Client.PostAsync("/api/uzcard/session", content);

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal("INVALID_REQUEST", await ErrorOf(response));
    }

    // If the nonce were claimed before the MAC check, anyone who saw a nonce in transit could lock
    // the genuine hash out by posting garbage that carries it.
    [Fact]
    public async Task A_forged_hash_does_not_burn_its_nonce()
    {
        var forged = await CreateSession(Client, App.MakeHash(secret: OTHER_SECRET, fields: new() { ["nonce"] = NONCE }));
        var genuine = await CreateSession(Client, MakeHash(("nonce", NONCE)));

        Assert.Equal("HASH_INVALID", await ErrorOf(forged));
        Assert.True((await genuine.Json())["success"].IsTrue());
    }

    [Fact]
    public async Task The_signature_is_checked_before_the_timestamp()
    {
        var response = await CreateSession(Client, App.MakeHash(secret: OTHER_SECRET, fields: new() { ["ts"] = App.Ts - 3600 }));

        Assert.Equal("HASH_INVALID", await ErrorOf(response));
    }

    // Naming the broken field helps the Operator integrate; a forger learns nothing from it.
    [Fact]
    public async Task Shape_errors_are_explained_only_behind_a_valid_signature()
    {
        var forged = await CreateSession(Client, App.MakeHash(secret: OTHER_SECRET, fields: new() { ["nonce"] = "short" }));
        var signed = await CreateSession(Client, MakeHash(("nonce", "short")));

        Assert.False((await forged.Json())["error"]!.AsObject().ContainsKey("details"));
        Assert.Equal("""{"field":"nonce"}""", (await signed.Json())["error"]!["details"]!.ToJsonString());
    }
}
