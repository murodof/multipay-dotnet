using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// Locks the JSON key names the frontend and the Operator parse.
//
// Renaming a schema field is a contract change even when every behavioural test still passes: the
// endpoint keeps working, the client silently stops finding the key. The whole shape is pinned, so
// an added or dropped key fails here too. Key order and the exact bytes are pinned against bodies
// captured from the Python service.
[Collection("app")]
public sealed class TestWireContract(Containers containers) : AppTest(containers)
{
    [Fact]
    public async Task Session_created_keys()
    {
        var body = await (await CreateSession(Client, MakeHash())).Json();

        Assert.Equal(["success", "result"], body.Select(item => item.Key));
        Assert.Equal(
            ["session_id", "session_token", "phone_mask", "amount", "commission", "total", "expires_at", "idle_timeout"],
            body["result"]!.AsObject().Select(item => item.Key));
    }

    [Fact]
    public async Task Error_keys()
    {
        var body = await (await CreateSession(Client, "garbage")).Json();

        Assert.Equal(["success", "error"], body.Select(item => item.Key));
        Assert.Equal(["code", "error_code", "message"], body["error"]!.AsObject().Select(item => item.Key));
        Assert.Equal(["uz", "en", "ru"], body["error"]!["message"]!.AsObject().Select(item => item.Key));
    }

    [Fact]
    public async Task Rate_limit_error_keys()
    {
        for (var index = 0; index < 10; index++)
        {
            await CreateSession(Client, "garbage");
        }

        var error = (await (await CreateSession(Client, MakeHash())).Json())["error"]!.AsObject();

        Assert.Equal(["code", "error_code", "message", "details"], error.Select(item => item.Key));
        Assert.Equal(["retry_after"], error["details"]!.AsObject().Select(item => item.Key));
    }

    [Fact]
    public async Task Amount_error_keys()
    {
        var error = (await (await CreateSession(Client, MakeHash(("amount", 1)))).Json())["error"]!.AsObject();

        Assert.Equal(["min_amount", "max_amount"], error["details"]!.AsObject().Select(item => item.Key));
    }

    // Byte-for-byte what FastAPI sent for the same request: compact separators, raw UTF-8, no escaping of apostrophes.
    [Fact]
    public async Task Error_bodies_match_the_python_service_byte_for_byte()
    {
        var hashInvalid = await CreateSession(Client, "garbage");
        var notFound = await Client.GetAsync("/nope");

        Assert.Equal(
            """{"success":false,"error":{"code":"HASH_INVALID","error_code":-2001,"message":{"uz":"To'lov havolasi yaroqsiz!","en":"Payment link is invalid","ru":"Ссылка на оплату недействительна"}}}""",
            await hashInvalid.Content.ReadAsStringAsync());
        Assert.Equal("application/json", hashInvalid.Content.Headers.ContentType!.ToString());
        Assert.Equal(213, hashInvalid.Content.Headers.ContentLength);
        Assert.Equal(
            """{"success":false,"error":{"code":"NOT_FOUND","error_code":-2009,"message":{"uz":"Manzil topilmadi!","en":"Not found","ru":"Не найдено"}}}""",
            await notFound.Content.ReadAsStringAsync());
        Assert.Equal(146, notFound.Content.Headers.ContentLength);
    }
}
