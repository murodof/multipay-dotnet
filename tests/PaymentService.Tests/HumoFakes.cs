using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PaymentService.Tests;

public sealed record HumoReply(bool Ok, JsonObject Result, object? ErrorCode = null);

// The HUMO halves of test_humo_flow.py's fakes, answering in each protocol's own format
// (Middleware/SMS JSON with an echoed id, Payment Service SOAP) so the real clients parse them.
public sealed class FakeMiddleware
{
    public bool Active = true;

    public FakeMiddleware(TestApp app) => app.Stubs.Route("humo-mw.test", async request =>
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
        var result = new JsonObject
        {
            ["card"] = new JsonObject
            {
                ["expiry"] = "2901",
                ["nameOnCard"] = "TEST USER",
                ["statuses"] = new JsonObject { ["item"] = new JsonArray(new JsonObject { ["type"] = "card", ["actionCode"] = Active ? "000" : "125" }) },
            },
            ["account"] = new JsonArray(new JsonObject { ["status"] = "Active" }),
        };
        return Http.Json(new JsonObject { ["id"] = body["id"]!.DeepClone(), ["result"] = result });
    });
}

public sealed class FakeSms
{
    public int VerifyCalls;
    public int SendCalls;
    public int? ErrorCode;
    public bool MissingToken;
    public TaskCompletionSource? VerifyEntered;
    public TaskCompletionSource? VerifyRelease;

    public FakeSms(TestApp app) => app.Stubs.Route("humo-sms.test", async request =>
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
        var path = request.RequestUri!.AbsolutePath;
        HumoReply reply;
        if (path.EndsWith("/sms2", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref SendCalls);
            reply = new HumoReply(true, new JsonObject { ["msisdn"] = "+998*******01" });
        }
        else if (path.EndsWith("/code2", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref VerifyCalls);
            if (VerifyEntered is not null)
            {
                VerifyEntered.TrySetResult();
                await VerifyRelease!.Task;
            }
            reply = ErrorCode is not null ? new HumoReply(false, [], ErrorCode)
                : MissingToken ? new HumoReply(true, [])
                : new HumoReply(true, new JsonObject { ["token"] = "PFID00000000", ["expiry"] = "2029-01-01" });
        }
        else
        {
            reply = new HumoReply(true, new JsonObject { ["message"] = "ok" });
        }
        var answer = new JsonObject { ["id"] = body["id"]!.DeepClone() };
        if (reply.Ok)
        {
            answer["result"] = reply.Result;
        }
        else
        {
            answer["error"] = new JsonObject { ["code"] = System.Text.Json.JsonSerializer.SerializeToNode(reply.ErrorCode) };
        }
        return Http.Json(answer);
    });
}

public sealed class FakePayment
{
    public int Holds;
    public int Confirms;
    public int Cancels;
    public int Returns;
    public long? LastAmount;
    public long CardholderAmount = 5_075_000;
    public bool CancelOk = true;
    public Func<Task<HumoReply>>? GetPayment;
    public List<string> Requests { get; } = [];

    public FakePayment(TestApp app) => app.Stubs.Route("humo-ps.test", async request =>
    {
        var xml = await request.Content!.ReadAsStringAsync();
        lock (Requests)
        {
            Requests.Add(xml);
        }
        var operation = XDocument.Parse(xml).Root!.Elements().Last().Elements().Single();
        var name = operation.Name.LocalName;
        var fields = operation.Elements().GroupBy(element => element.Name.LocalName).ToDictionary(group => group.Key, group => group.First().Value);
        HumoReply reply;
        switch (name)
        {
            case "Payment" when fields.ContainsKey("billerRef"):
                Interlocked.Increment(ref Holds);
                LastAmount = long.Parse(operation.Descendants("item").Single(item => item.Element("name")!.Value == "amount").Element("value")!.Value, System.Globalization.CultureInfo.InvariantCulture);
                reply = new HumoReply(true, new JsonObject
                {
                    ["paymentID"] = "12345",
                    ["action"] = "4",
                    ["auth_action_code"] = "000",
                    ["auth_ref_number"] = "789456123",
                    ["cardholder_amount1a"] = CardholderAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
                break;
            case "Payment":
                Interlocked.Increment(ref Confirms);
                reply = new HumoReply(true, new JsonObject { ["action"] = "10" });
                break;
            case "CancelRequest":
                Interlocked.Increment(ref Cancels);
                reply = new HumoReply(CancelOk, []);
                break;
            case "GetPayment":
                reply = GetPayment is not null ? await GetPayment() : new HumoReply(true, new JsonObject { ["status"] = "finished", ["paymentID"] = "12345" });
                break;
            case "ReturnPayment":
                Interlocked.Increment(ref Returns);
                reply = new HumoReply(true, []);
                break;
            default:
                throw new InvalidOperationException(name);
        }
        return Http.Xml(Soap(name, reply));
    });

    public static string Soap(string operation, HumoReply reply)
    {
        if (!reply.Ok)
        {
            return """<Envelope xmlns="http://schemas.xmlsoap.org/soap/envelope/"><Body><Fault><faultcode>Server</faultcode><faultstring>rejected</faultstring></Fault></Body></Envelope>""";
        }
        var fields = string.Concat(reply.Result.Select(item => $"<{item.Key}>{System.Security.SecurityElement.Escape(item.Value!.GetValue<string>())}</{item.Key}>"));
        return $"""<Envelope xmlns="http://schemas.xmlsoap.org/soap/envelope/"><Body><{operation}Response xmlns="urn:PaymentServer">{fields}</{operation}Response></Body></Envelope>""";
    }
}
