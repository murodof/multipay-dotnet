using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Models;
using PaymentService.Infrastructure.ExternalServices.Humo;
using PaymentService.Infrastructure.Services;
using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

[Collection("app")]
public sealed class TestHumoFlow : AppTest
{
    private const string PAN = "9860090101191238";
    private const string EXPIRY = "01/29";

    private readonly FakeMiddleware mw;
    private readonly FakeSms sms;
    private readonly FakePayment ps;
    private readonly FakeOperator @operator;

    public TestHumoFlow(Containers containers) : base(containers)
    {
        App.Settings["HUMO_ENABLED"] = "true";
        mw = new FakeMiddleware(App);
        sms = new FakeSms(App);
        ps = new FakePayment(App);
        @operator = new FakeOperator(App);
    }

    private Task<HttpResponseMessage> Post(string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return Client.SendAsync(request);
    }

    private async Task<(string SessionId, string Token, HttpResponseMessage Sent)> OpenHumoSession()
    {
        var session = (await (await CreateSession(Client, MakeHash(), provider: "humo")).Json())["result"]!;
        var sessionId = session["session_id"]!.GetValue<string>();
        var token = session["session_token"]!.GetValue<string>();
        var sent = await Post($"/api/humo/session/{sessionId}/card", token, new { pan = PAN, expiry = EXPIRY });
        return (sessionId, token, sent);
    }

    private Task<HttpResponseMessage> Verify(string sessionId, string token) =>
        Post($"/api/humo/session/{sessionId}/verify", token, new { code = "A12B34", payment_confirmed = true });

    private static async Task<string?> StatusOf(HttpResponseMessage response) =>
        (await response.Json())["result"]?["status"]?.GetValue<string>();

    private async Task MoveTo(string sessionId, PaymentSessionStatus sessionStatus, PaymentStatus paymentStatus, int checkAttempts = 0)
    {
        await using var db = App.Db();
        var session = await db.PaymentSessions.SingleAsync(item => item.Id == Guid.Parse(sessionId));
        session.Status = sessionStatus;
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Amount = session.Amount,
            Commission = session.Commission,
            Status = paymentStatus,
            CheckAttempts = checkAttempts,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(PaymentSession Session, Payment Payment)> Stored(string sessionId)
    {
        await using var db = App.Db();
        var session = await db.PaymentSessions.AsNoTracking().SingleAsync(item => item.Id == Guid.Parse(sessionId));
        var payment = await db.Payments.AsNoTracking().SingleAsync(item => item.SessionId == session.Id);
        return (session, payment);
    }

    [Fact]
    public async Task Humo_endpoint_rejects_uzcard_pan()
    {
        var session = (await (await CreateSession(Client, MakeHash(), provider: "humo")).Json())["result"]!;

        var response = await Post(
            $"/api/humo/session/{session["session_id"]}/card", session["session_token"]!.GetValue<string>(),
            new { pan = "8600123412341239", expiry = EXPIRY });

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal("CARD_NOT_SUPPORTED", await ErrorOf(response));
    }

    [Fact]
    public async Task Humo_success_has_one_hold_and_one_confirm()
    {
        var (sessionId, token, sent) = await OpenHumoSession();
        Assert.Equal("HUMO", (await sent.Json())["result"]!["processing"]!.GetValue<string>());
        var encrypted = await App.Redis.Get($"humo:card:{sessionId}");
        Assert.NotNull(encrypted);
        Assert.DoesNotContain(PAN, encrypted, StringComparison.Ordinal);

        var verified = await Verify(sessionId, token);

        Assert.Equal("SUCCESS", await StatusOf(verified));
        Assert.Equal((1, 1, 1, 1), (sms.VerifyCalls, ps.Holds, ps.Confirms, @operator.Calls));
        Assert.Equal(5_000_000, ps.LastAmount);
        Assert.Null(await App.Redis.Get($"humo:card:{sessionId}"));
    }

    // Not in the Python suite: the resend path, including the wait, the wrong card and paying afterwards.
    [Fact]
    public async Task Humo_resend_sends_a_second_sms_and_still_pays()
    {
        var (sessionId, token, _) = await OpenHumoSession();

        var tooSoon = await Post($"/api/humo/session/{sessionId}/resend", token, new { pan = PAN, expiry = EXPIRY });
        await App.Redis.Delete($"humo:otp:{sessionId}");
        var otherCard = await Post($"/api/humo/session/{sessionId}/resend", token, new { pan = "9860000000000002", expiry = EXPIRY });
        var resent = await Post($"/api/humo/session/{sessionId}/resend", token, new { pan = PAN, expiry = EXPIRY });
        var verified = await Verify(sessionId, token);

        Assert.Equal("OTP_RESEND_TOO_SOON", await ErrorOf(tooSoon));
        Assert.NotEmpty(tooSoon.Headers.GetValues("Retry-After").Single());
        Assert.Equal("CARD_INVALID", await ErrorOf(otherCard));
        Assert.Equal(
            """{"success":true,"result":{"processing":"HUMO","phone_mask":"+998*******01","otp_ttl":59,"resend_after":59,"attempts_left":3,"resends_left":2}}""",
            await resent.Content.ReadAsStringAsync());
        Assert.Equal(2, sms.SendCalls);
        Assert.Equal("SUCCESS", await StatusOf(verified));
    }

    [Fact]
    public async Task Humo_audit_log_masks_card_and_otp()
    {
        var (sessionId, token, _) = await OpenHumoSession();
        var verified = await Verify(sessionId, token);
        Assert.Equal("SUCCESS", await StatusOf(verified));

        await using var db = App.Db();
        var logs = await db.HumoLogs.AsNoTracking().Where(log => log.SessionId == Guid.Parse(sessionId)).ToListAsync();
        Assert.Superset(new HashSet<string> { "card2", "sms2", "code2", "Payment.hold", "Payment.confirm" }, logs.Select(log => log.Method).ToHashSet());
        var serialized = new JsonArray([.. logs.Select(log => new JsonObject
        {
            ["request"] = log.RequestMasked.DeepClone(),
            ["response"] = log.ResponseMasked.DeepClone(),
        })]).ToJsonString();
        Assert.DoesNotContain(PAN, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(EXPIRY, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("A12B34", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PFID00000000", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Humo_operator_failure_cancels_hold()
    {
        @operator.Ok = false;
        var (sessionId, token, _) = await OpenHumoSession();

        var verified = await Verify(sessionId, token);

        Assert.Equal("CANCELLED", await StatusOf(verified));
        Assert.Equal((1, 1), (ps.Holds, ps.Cancels));
        Assert.Equal(0, ps.Confirms);
    }

    [Fact]
    public async Task Uncertain_humo_operator_result_keeps_hold_for_review()
    {
        @operator.Ok = false;
        @operator.Uncertain = true;
        var (sessionId, token, _) = await OpenHumoSession();

        var verified = await Verify(sessionId, token);

        Assert.Equal("NEEDS_MANUAL", await StatusOf(verified));
        Assert.Equal(1, @operator.Calls);
        Assert.Equal((0, 0), (ps.Cancels, ps.Confirms));
    }

    [Fact]
    public async Task Humo_mismatched_cardholder_total_cancels_before_operator()
    {
        ps.CardholderAmount = 5_100_000;
        var (sessionId, token, _) = await OpenHumoSession();

        var verified = await Verify(sessionId, token);

        Assert.Equal("CANCELLED", await StatusOf(verified));
        Assert.Equal(1, ps.Cancels);
        Assert.Equal((0, 0), (ps.Confirms, @operator.Calls));
    }

    [Fact]
    public async Task Humo_finished_payment_uses_return_when_cancel_fails()
    {
        @operator.Ok = false;
        ps.CancelOk = false;
        var (sessionId, token, _) = await OpenHumoSession();

        var verified = await Verify(sessionId, token);

        Assert.Equal("RETURNING", await StatusOf(verified));
        Assert.Equal((1, 1), (ps.Cancels, ps.Returns));
        Assert.Equal(0, ps.Confirms);
    }

    [Fact]
    public async Task Expired_humo_hold_is_cancelled_by_cleanup()
    {
        var (sessionId, _, _) = await OpenHumoSession();
        await MoveTo(sessionId, PaymentSessionStatus.HELD, PaymentStatus.HELD);
        App.Clock.Advance(TimeSpan.FromSeconds(181));

        await App.Service<HumoRecovery>().RecoverStaleHumoPayments();

        var (session, payment) = await Stored(sessionId);
        Assert.Equal(PaymentSessionStatus.CANCELLED, session.Status);
        Assert.Equal(PaymentStatus.CANCELLED, payment.Status);
        Assert.Equal(1, ps.Cancels);
    }

    [Fact]
    public async Task Stale_operator_credit_requires_manual_reconciliation()
    {
        var (sessionId, _, _) = await OpenHumoSession();
        await MoveTo(sessionId, PaymentSessionStatus.CREDITING, PaymentStatus.CREDITING);
        App.Clock.Advance(TimeSpan.FromSeconds(181));

        await App.Service<HumoRecovery>().RecoverStaleHumoPayments();

        var (session, payment) = await Stored(sessionId);
        Assert.Equal(PaymentSessionStatus.NEEDS_MANUAL, session.Status);
        Assert.Equal(PaymentStatus.NEEDS_MANUAL, payment.Status);
        Assert.Equal(0, ps.Cancels);
    }

    // A ReturnPayment response or GetPayment status alone does not establish that RECO and clearing
    // completed, so an unresolved return goes to NEEDS_MANUAL instead of RETURNED.
    [Fact]
    public async Task Background_reconciliation_does_not_assume_return_is_final()
    {
        var (sessionId, _, _) = await OpenHumoSession();
        await MoveTo(sessionId, PaymentSessionStatus.RETURNING, PaymentStatus.RETURNING, checkAttempts: 2);
        ps.GetPayment = () => Task.FromResult(new HumoReply(true, new JsonObject { ["status"] = "returned" }));

        await App.Service<HumoRecovery>().ReconcileHumoPendingPayments();

        var (session, payment) = await Stored(sessionId);
        Assert.Equal(PaymentSessionStatus.NEEDS_MANUAL, session.Status);
        Assert.Equal(PaymentStatus.NEEDS_MANUAL, payment.Status);
    }

    [Fact]
    public async Task Expired_humo_otp_never_calls_code2()
    {
        var (sessionId, token, _) = await OpenHumoSession();
        await App.Redis.Delete($"humo:otp:{sessionId}");

        var verified = await Verify(sessionId, token);

        Assert.Equal("OTP_EXPIRED", await ErrorOf(verified));
        Assert.Equal(0, sms.VerifyCalls);
    }

    [Fact]
    public async Task Missing_humo_verify_token_clears_card()
    {
        sms.MissingToken = true;
        var (sessionId, token, _) = await OpenHumoSession();
        Assert.NotNull(await App.Redis.Get($"humo:card:{sessionId}"));

        var response = await Verify(sessionId, token);

        Assert.Equal("PROCESSING_UNAVAILABLE", await ErrorOf(response));
        Assert.Null(await App.Redis.Get($"humo:card:{sessionId}"));
    }

    [Fact]
    public async Task Parallel_humo_verify_holds_once()
    {
        var (sessionId, token, _) = await OpenHumoSession();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Verify(sessionId, token)));

        Assert.Equal((1, 1, 1, 1), (sms.VerifyCalls, ps.Holds, ps.Confirms, @operator.Calls));
        Assert.Equal(1, (await Task.WhenAll(responses.Select(StatusOf))).Count(status => status == "SUCCESS"));
    }

    [Fact]
    public async Task Humo_expire_during_verify_does_not_cancel_hold()
    {
        sms.VerifyEntered = new TaskCompletionSource();
        sms.VerifyRelease = new TaskCompletionSource();
        var (sessionId, token, _) = await OpenHumoSession();

        var verifying = Verify(sessionId, token);
        await sms.VerifyEntered.Task;
        var expired = await Post($"/api/humo/session/{sessionId}/expire", token);
        sms.VerifyRelease.SetResult();
        var verified = await verifying;

        Assert.Equal(409, (int)expired.StatusCode);
        Assert.Equal("INVALID_STATE", await ErrorOf(expired));
        Assert.Equal("SUCCESS", await StatusOf(verified));
        Assert.Equal((1, 1, 1), (ps.Holds, ps.Confirms, @operator.Calls));
        Assert.Equal(0, ps.Cancels);
    }

    [Fact]
    public async Task Three_wrong_humo_codes_lock_card()
    {
        sms.ErrorCode = 404;
        var (sessionId, token, _) = await OpenHumoSession();

        var responses = new List<HttpResponseMessage>();
        for (var index = 0; index < 3; index++)
        {
            responses.Add(await Verify(sessionId, token));
        }

        Assert.Equal(["OTP_WRONG", "OTP_WRONG", "OTP_LOCKED"], await Task.WhenAll(responses.Select(ErrorOf)));
        Assert.Equal(3, sms.VerifyCalls);
        Assert.Equal(0, ps.Holds);
    }

    [Fact]
    public async Task Blocked_humo_card_never_sends_sms()
    {
        mw.Active = false;

        var (_, _, sent) = await OpenHumoSession();

        Assert.Equal("CARD_BLOCKED", await ErrorOf(sent));
        Assert.Equal(0, sms.SendCalls);
    }

    [Fact]
    public void Humo_soap_request_shapes_and_masked_response_parser()
    {
        var payload = HumoPaymentClient.Envelope(
            "Payment",
            [("language", "en"), ("billerRef", "SOAP_DMS"), ("payinstrRef", "SOAP_DMS"), ("sessionID", "id"), ("paymentRef", "id"), ("paymentOriginator", "login")],
            [("pan", PAN), ("amount", "1000000")]);
        var root = XDocument.Parse(Encoding.UTF8.GetString(payload));
        Assert.Equal("login", root.Descendants("paymentOriginator").Single().Value);
        Assert.Equal(PAN, root.Descendants("details").Single().Elements("item").Single(item => item.Element("name")!.Value == "pan").Element("value")!.Value);
        Assert.Equal("1000000", root.Descendants("item").Single(item => item.Element("name")!.Value == "amount").Element("value")!.Value);

        var response = """
            <Envelope xmlns="http://schemas.xmlsoap.org/soap/envelope/">
              <Body><PaymentResponse xmlns="urn:PaymentServer"><paymentID>123</paymentID>
              <details><item><name>pan</name><value>9860090101191238</value></item>
              <item><name>auth_action_code</name><value>000</value></item></details>
              <action>4</action></PaymentResponse></Body></Envelope>
            """u8.ToArray();
        var parsed = HumoPaymentClient.ParsePaymentResponse(response, "Payment");

        Assert.Equal("""{"paymentID":"123","auth_action_code":"000","action":"4"}""", parsed.Result.ToJsonString());
    }

    // The envelopes are assembled by hand, so they are pinned to the bytes ElementTree produced in
    // the Python service (prefixes, self-closing empty values, escaping of & < > only).
    public static TheoryData<string> PythonEnvelopes =>
        new(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "python_soap_envelopes.json")))!.AsObject().Select(item => item.Key));

    [Theory]
    [MemberData(nameof(PythonEnvelopes))]
    public async Task Humo_soap_envelopes_match_the_python_bytes(string name)
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "python_soap_envelopes.json")))![name]!.AsArray()
            .Select(item => item!.GetValue<string>()).ToList();
        var settings = new Settings
        {
            HUMO_PS_URL = "https://humo-ps.test/PaymentServer",
            HUMO_PS_USERNAME = "login",
            HUMO_PS_PASSWORD = "p",
            HUMO_MERCHANT_ID = name == "hold_escaping_and_empty" ? "" : "123456789012345",
            HUMO_TERMINAL_ID = "12345678",
            HUMO_CENTRE_ID = "bank",
        };
        var stubs = new StubRouter();
        var sent = new List<string>();
        stubs.Route("humo-ps.test", async request =>
        {
            sent.Add(await request.Content!.ReadAsStringAsync());
            Assert.Equal("\"\"", request.Headers.GetValues("SOAPAction").Single());
            Assert.Equal("text/xml; charset=utf-8", request.Content.Headers.ContentType!.ToString());
            return Http.Xml(FakePayment.Soap("Payment", new HumoReply(true, new JsonObject { ["action"] = "4" })));
        });
        var client = new HumoPaymentClient(new HttpClient(stubs), Options.Create(settings));

        _ = name switch
        {
            "hold" => await client.Hold("ref-1", "9860090101191238", "2901", 5_000_000),
            "confirm" => await client.Confirm("ref-1"),
            "get_payment" => await client.GetPayment("ref-1"),
            "cancel" => await client.Cancel("ref-1"),
            "return_payment" => await client.ReturnPayment("ref-1", "12345"),
            "reconcile" => await client.Reconcile("reco-1"),
            _ => await client.Hold("a&b<c>\"'", "9860", "2901", 1),
        };

        Assert.Equal(expected, sent);
    }

    [Fact]
    public async Task Reco_requires_authorization_before_confirmation()
    {
        var calls = new List<(string Operation, IReadOnlyList<(string Key, string Value)> Fields, IReadOnlyList<(string Key, string Value)>? Details)>();
        var settings = new Settings { HUMO_PS_USERNAME = "login", HUMO_TERMINAL_ID = "12345678" };
        var client = new RecordingPaymentClient(settings, calls);

        var result = await client.Reconcile("reco-1");

        Assert.Equal("10", result.Result["action"]!.GetValue<string>());
        Assert.Contains(("billerRef", "SOAP_RECO"), calls[0].Fields);
        Assert.Equal([("terminal_id", "12345678")], calls[0].Details!);
        Assert.Contains(("confirmed", "true"), calls[1].Fields);
        Assert.Contains(("finished", "true"), calls[1].Fields);
    }

    private sealed class RecordingPaymentClient(
        Settings settings,
        List<(string Operation, IReadOnlyList<(string Key, string Value)> Fields, IReadOnlyList<(string Key, string Value)>? Details)> calls)
        : HumoPaymentClient(new HttpClient(), Options.Create(settings))
    {
        public override Task<HumoPaymentResult> Call(
            string operation, IReadOnlyList<(string Key, string Value)> fields, IReadOnlyList<(string Key, string Value)>? details = null, bool directItems = false)
        {
            calls.Add((operation, fields, details));
            return Task.FromResult(new HumoPaymentResult(true, new JsonObject { ["action"] = calls.Count == 1 ? "4" : "10" }));
        }
    }

    // Not in the Python suite: the stored card is bound to its session, so a ciphertext copied to
    // another session's key does not decrypt.
    [Fact]
    public async Task The_stored_card_is_bound_to_its_session()
    {
        var (sessionId, _, _) = await OpenHumoSession();
        var encrypted = await App.Redis.Get($"humo:card:{sessionId}");
        var other = Guid.NewGuid();
        await App.Redis.SetValue($"humo:card:{other}", encrypted!, 60);

        using var scope = App.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<HumoService>();

        Assert.NotNull(await service.LoadCard(Guid.Parse(sessionId)));
        Assert.Null(await service.LoadCard(other));
    }

    // `/expire` decides to cancel while the session is HELD; by the time it acts, `/verify` has claimed
    // CREDITING and the Operator is being credited. Cancelling then would release the hold of a
    // top-up that is going through.
    [Fact]
    public async Task Cancel_decided_on_a_held_snapshot_leaves_a_crediting_hold_alone()
    {
        var (sessionId, _, _) = await OpenHumoSession();
        await MoveTo(sessionId, PaymentSessionStatus.CREDITING, PaymentStatus.CREDITING);

        using var scope = App.Services.CreateScope();
        var session = await scope.ServiceProvider.GetRequiredService<PaymentService.Infrastructure.Repositories.PaymentSessionRepository>().GetById(Guid.Parse(sessionId));
        var payment = await scope.ServiceProvider.GetRequiredService<PaymentService.Infrastructure.Repositories.PaymentRepository>().GetBySessionId(Guid.Parse(sessionId));
        var result = await scope.ServiceProvider.GetRequiredService<HumoService>().Cancel(session!, payment!, PaymentSessionStatus.HELD);

        Assert.Equal("INVALID_STATE", Assert.IsType<PaymentService.Domain.Schemas.ErrorResponse>(result).Code);
        Assert.Equal(0, ps.Cancels);
        Assert.Equal(PaymentSessionStatus.CREDITING, (await Stored(sessionId)).Session.Status);
    }

    // ElementTree keeps an element that holds only whitespace as "" after strip(), and "" is not an
    // approval. Dropping the element instead would read a blank action code as "no objection".
    [Fact]
    public void A_blank_soap_field_is_kept_as_an_empty_value()
    {
        var response = """
            <Envelope xmlns="http://schemas.xmlsoap.org/soap/envelope/">
              <Body><PaymentResponse xmlns="urn:PaymentServer"><paymentID>123</paymentID><auth_action_code> </auth_action_code>
              <details><item><name>auth_ref_number</name><value>
              </value></item></details></PaymentResponse></Body></Envelope>
            """u8.ToArray();

        var parsed = HumoPaymentClient.ParsePaymentResponse(response, "Payment");

        Assert.Equal("""{"paymentID":"123","auth_action_code":"","auth_ref_number":""}""", parsed.Result.ToJsonString());
    }
}
