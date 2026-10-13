using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace PaymentService.Tests;

public sealed record GatewayResult(JsonNode? Result = null, int? ErrorCode = null, bool TransportError = false);

public static class Http
{
    public static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Xml(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/xml"),
    };
}

// The SV-GATE side of test_uzcard_flow.py's FakeSvgate, served over HTTP so the real SvgateClient
// (JSON-RPC envelope, normalisation, masking) runs too.
public sealed class FakeSvgate
{
    public int OtpCalls;
    public int VerifyCalls;
    public int PayCalls;
    public int ReverseCalls;
    public int RemoveTokenCalls;
    public int RemoveTokenFailures;
    public int ExtCalls;
    public int? VerifyError;
    public GatewayResult? VerifyResult;
    public string? RawReply;
    public TaskCompletionSource? VerifyEntered;
    public TaskCompletionSource? VerifyRelease;
    public GatewayResult? PayResult;
    public GatewayResult? ExtResult;
    public GatewayResult? ReverseResult;
    public Func<string, Task<GatewayResult>>? TransExt;
    public Func<Task<GatewayResult>>? TransPayPurpose;
    public List<JsonObject> Requests { get; } = [];

    public static GatewayResult Result(JsonNode? result = null, int? errorCode = null, bool transportError = false) =>
        new(result ?? new JsonObject(), errorCode, transportError);

    public FakeSvgate(TestApp app) => app.Stubs.Route("svgate.test", Handle);

    private async Task<HttpResponseMessage> Handle(HttpRequestMessage request)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
        lock (Requests)
        {
            Requests.Add(body);
        }
        if (RawReply is not null)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RawReply, Encoding.UTF8, "application/json") };
        }
        var parameters = body["params"]!.AsObject();
        var outcome = body["method"]!.GetValue<string>() switch
        {
            "cards.new.otp" => CardsNewOtp(),
            "cards.new.verify" => await CardsNewVerify(),
            "trans.pay.purpose" => await Pay(),
            "trans.ext" => await Ext(parameters["extId"]!.GetValue<string>()),
            "trans.reverse" => Reverse(parameters["tranId"]!.GetValue<string>()),
            "cards.remove.token" => RemoveToken(parameters["tokens"]![0]!.GetValue<string>()),
            var method => throw new InvalidOperationException(method),
        };
        if (outcome.TransportError)
        {
            throw new HttpRequestException("ReadTimeout");
        }
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone() };
        if (outcome.ErrorCode is not null)
        {
            reply["error"] = new JsonObject { ["code"] = outcome.ErrorCode, ["message"] = null };
        }
        else
        {
            reply["result"] = outcome.Result?.DeepClone();
        }
        return Http.Json(reply);
    }

    private GatewayResult CardsNewOtp()
    {
        Interlocked.Increment(ref OtpCalls);
        return Result(new JsonObject { ["id"] = 123456, ["phoneMask"] = "****1234", ["verified"] = false });
    }

    private async Task<GatewayResult> CardsNewVerify()
    {
        Interlocked.Increment(ref VerifyCalls);
        if (VerifyEntered is not null)
        {
            VerifyEntered.TrySetResult();
            await VerifyRelease!.Task;
        }
        if (VerifyError is not null)
        {
            return Result(errorCode: VerifyError);
        }
        if (VerifyResult is not null)
        {
            return VerifyResult;
        }
        return Result(new JsonObject { ["id"] = "card-token", ["pan"] = "8600********1239", ["fullName"] = "TEST USER", ["balance"] = 10_000_000 });
    }

    private async Task<GatewayResult> Pay()
    {
        if (TransPayPurpose is not null)
        {
            return await TransPayPurpose();
        }
        Interlocked.Increment(ref PayCalls);
        return PayResult ?? Result(new JsonObject
        {
            ["status"] = "OK",
            ["resp"] = 0,
            ["refNum"] = "123456789012",
            ["stan"] = "123456",
            ["date12"] = "20260919120000",
        });
    }

    private async Task<GatewayResult> Ext(string paymentId)
    {
        if (TransExt is not null)
        {
            return await TransExt(paymentId);
        }
        Interlocked.Increment(ref ExtCalls);
        if (ExtResult is not null)
        {
            return ExtResult;
        }
        if (ReverseCalls > 0)
        {
            return Result(new JsonObject
            {
                ["transactions"] = new JsonArray(
                    new JsonObject { ["tranType"] = "DEBIT", ["status"] = "OK", ["resp"] = 0, ["refNum"] = "123456789012" },
                    new JsonObject { ["tranType"] = "REVERSAL", ["status"] = "ROK", ["resp"] = 0, ["refNum"] = "123456789012" }),
            });
        }
        return Result(new JsonObject { ["status"] = "OK", ["resp"] = 0, ["refNum"] = "123456789012" });
    }

    private GatewayResult Reverse(string refNum)
    {
        Interlocked.Increment(ref ReverseCalls);
        return ReverseResult ?? Result(new JsonObject
        {
            ["transactions"] = new JsonArray(new JsonObject { ["tranType"] = "REVERSAL", ["status"] = "ROK", ["resp"] = 0, ["refNum"] = refNum }),
        });
    }

    private GatewayResult RemoveToken(string token)
    {
        Interlocked.Increment(ref RemoveTokenCalls);
        if (RemoveTokenFailures > 0)
        {
            RemoveTokenFailures -= 1;
            return Result(errorCode: -211);
        }
        return Result(new JsonObject { ["tokens"] = new JsonArray(new JsonObject { ["id"] = token, ["Status"] = 1 }) });
    }
}

public sealed class FakeOperator
{
    public bool Ok = true;
    public bool Uncertain;
    public int Calls;

    public FakeOperator(TestApp app) => app.Stubs.Route("operator.test", _ =>
    {
        Interlocked.Increment(ref Calls);
        if (Uncertain)
        {
            throw new HttpRequestException("ConnectTimeout");
        }
        return Task.FromResult(Http.Json(Ok
            ? new JsonObject { ["success"] = true, ["transactionId"] = "operator-1" }
            : new JsonObject { ["success"] = false }));
    });
}
