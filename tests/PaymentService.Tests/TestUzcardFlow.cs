using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaymentService.Domain.Enums;
using PaymentService.Infrastructure.Services;
using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

[Collection("app")]
public sealed class TestUzcardFlow : AppTest
{
    private const string PAN = "8600123412341239";
    private const string EXPIRY = "12/29";

    private readonly FakeSvgate gateway;
    private readonly FakeOperator @operator;

    public TestUzcardFlow(Containers containers) : base(containers)
    {
        gateway = new FakeSvgate(App);
        @operator = new FakeOperator(App);
    }

    private async Task<(string SessionId, string Token)> OpenedSession()
    {
        var result = (await (await CreateSession(Client, MakeHash())).Json())["result"]!;
        return (result["session_id"]!.GetValue<string>(), result["session_token"]!.GetValue<string>());
    }

    private Task<HttpResponseMessage> Post(string path, string? token, object? body = null, string? key = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }
        return (client ?? Client).SendAsync(request);
    }

    private Task<HttpResponseMessage> Card(string sessionId, string token, string? key = null) =>
        Post($"/api/uzcard/session/{sessionId}/card", token, new { pan = PAN, expiry = EXPIRY }, key ?? Guid.NewGuid().ToString());

    private Task<HttpResponseMessage> Verify(string sessionId, string token) =>
        Post($"/api/uzcard/session/{sessionId}/verify", token, new { code = "A12B34", payment_confirmed = true }, Guid.NewGuid().ToString());

    private Task<HttpResponseMessage> Status(string sessionId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/uzcard/session/{sessionId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Client.SendAsync(request);
    }

    private static async Task<string?> StatusOf(HttpResponseMessage response) =>
        (await response.Json())["result"]?["status"]?.GetValue<string>();

    private Task Recover() => App.Service<UzcardRecovery>().RecoverStaleUzcardPayments();

    [Fact]
    public async Task Card_requires_bearer_token()
    {
        var (sessionId, _) = await OpenedSession();

        var response = await Post($"/api/uzcard/session/{sessionId}/card", null, new { pan = PAN, expiry = EXPIRY }, Guid.NewGuid().ToString());

        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("SESSION_UNAUTHORIZED", await ErrorOf(response));
    }

    // Not in the Python suite (found by mutation): the guard must insist on the Bearer scheme and on
    // the token of *this* session, not merely on some Authorization header of the right length.
    [Theory]
    [InlineData("Token: {0}")]
    [InlineData("bearer {0}")]
    [InlineData("Bearer {0}x")]
    [InlineData("Bearer ")]
    public async Task Card_requires_this_sessions_bearer_token(string header)
    {
        var (sessionId, token) = await OpenedSession();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/uzcard/session/{sessionId}/card") { Content = JsonContent.Create(new { pan = PAN, expiry = EXPIRY }) };
        request.Headers.TryAddWithoutValidation("Authorization", string.Format(System.Globalization.CultureInfo.InvariantCulture, header, token));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await Client.SendAsync(request);

        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("SESSION_UNAUTHORIZED", await ErrorOf(response));
        Assert.Equal(0, gateway.OtpCalls);
    }

    [Fact]
    public async Task Another_sessions_token_is_refused()
    {
        var (sessionId, _) = await OpenedSession();
        var (_, otherToken) = await OpenedSession();

        var response = await Post($"/api/uzcard/session/{sessionId}/card", otherToken, new { pan = PAN, expiry = EXPIRY }, Guid.NewGuid().ToString());

        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal(0, gateway.OtpCalls);
    }

    [Fact]
    public async Task Card_idempotency_sends_one_otp()
    {
        var (sessionId, token) = await OpenedSession();
        var key = Guid.NewGuid().ToString();

        var first = await Card(sessionId, token, key);
        var second = await Card(sessionId, token, key);

        Assert.Equal(200, (int)first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(1, gateway.OtpCalls);
        Assert.Equal(59, (await first.Json())["result"]!["otp_ttl"]!.GetValue<int>());
    }

    [Fact]
    public async Task Parallel_card_requests_with_different_keys_send_one_otp()
    {
        var (sessionId, token) = await OpenedSession();

        var responses = await Task.WhenAll(Card(sessionId, token), Card(sessionId, token));

        Assert.Equal([200, 409], responses.Select(response => (int)response.StatusCode).Order());
        Assert.Equal(1, gateway.OtpCalls);
    }

    [Fact]
    public async Task Expired_otp_never_reaches_svgate()
    {
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        await App.Redis.Delete($"otp:{sessionId}");

        var response = await Verify(sessionId, token);

        Assert.Equal(409, (int)response.StatusCode);
        Assert.Equal("OTP_EXPIRED", await ErrorOf(response));
        Assert.Equal(0, gateway.VerifyCalls);
    }

    [Fact]
    public async Task Three_wrong_codes_lock_the_card()
    {
        gateway.VerifyError = -269;
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);

        var responses = new List<HttpResponseMessage>();
        for (var index = 0; index < 3; index++)
        {
            responses.Add(await Verify(sessionId, token));
        }

        Assert.Equal(["OTP_WRONG", "OTP_WRONG", "OTP_LOCKED"], await Task.WhenAll(responses.Select(ErrorOf)));
        Assert.Equal(2, (await responses[0].Json())["error"]!["details"]!["attempts_left"]!.GetValue<int>());
        Assert.Equal(3, gateway.VerifyCalls);
    }

    [Fact]
    public async Task Complete_uzcard_flow_creates_public_receipt()
    {
        var (sessionId, token) = await OpenedSession();
        var cardResponse = await Card(sessionId, token);

        var verifyResponse = await Verify(sessionId, token);

        Assert.Equal("UZCARD", (await cardResponse.Json())["result"]!["processing"]!.GetValue<string>());
        Assert.Equal(200, (int)verifyResponse.StatusCode);
        Assert.Equal("SUCCESS", await StatusOf(verifyResponse));
        Assert.Equal((1, 1, 1), (gateway.OtpCalls, gateway.VerifyCalls, gateway.PayCalls));
        Assert.Equal(1, gateway.RemoveTokenCalls);
        Assert.Equal(1, @operator.Calls);

        var receiptId = (await verifyResponse.Json())["result"]!["receipt"]!["id"]!.GetValue<string>();
        var receipt = await Client.GetAsync($"/api/receipt/{receiptId}");
        var result = (await receipt.Json())["result"]!;

        Assert.Equal(200, (int)receipt.StatusCode);
        Assert.Equal("UZCARD", result["processing"]!.GetValue<string>());
        Assert.Equal("123456789012", result["reference"]!.GetValue<string>());
        Assert.Equal("+998 90 *** ** 67", result["phone"]!.GetValue<string>());
        Assert.Equal("noindex, nofollow", receipt.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Equal(
            ["id", "number", "date", "phone", "amount", "commission", "total", "card", "processing", "reference", "status"],
            result.AsObject().Select(item => item.Key));
        Assert.Matches(@"\APAY-\d{8}-[0-9A-F]{8}\z", result["number"]!.GetValue<string>());
        Assert.EndsWith("+05:00", result["date"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operator_failure_reverses_the_payment()
    {
        @operator.Ok = false;
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);

        var response = await Verify(sessionId, token);

        Assert.Equal("REVERSED", await StatusOf(response));
        Assert.Equal(1, @operator.Calls);
        Assert.Equal(1, gateway.ReverseCalls);
    }

    [Fact]
    public async Task Uncertain_operator_result_does_not_reverse()
    {
        @operator.Ok = false;
        @operator.Uncertain = true;
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);

        var response = await Verify(sessionId, token);

        Assert.Equal("NEEDS_MANUAL", await StatusOf(response));
        Assert.Equal(1, @operator.Calls);
        Assert.Equal(0, gateway.ReverseCalls);
    }

    [Fact]
    public async Task Parallel_verify_requests_charge_once()
    {
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Verify(sessionId, token)));

        Assert.Equal(1, gateway.VerifyCalls);
        Assert.Equal(1, gateway.PayCalls);
        Assert.Equal(1, @operator.Calls);
        var statuses = await Task.WhenAll(responses.Select(StatusOf));
        Assert.Equal(1, statuses.Count(status => status == "SUCCESS"));
    }

    [Fact]
    public async Task Payment_timeout_is_recovered_by_status_polling()
    {
        gateway.PayResult = FakeSvgate.Result(transportError: true);
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        var verification = await Verify(sessionId, token);

        Assert.Equal("""{"status":"PROCESSING","poll_after":2,"receipt":null}""", (await verification.Json())["result"]!.ToJsonString());

        App.Clock.Advance(TimeSpan.FromSeconds(5));
        var status = await Status(sessionId, token);

        Assert.Equal("SUCCESS", await StatusOf(status));
        Assert.Equal(1, gateway.ExtCalls);
        Assert.Equal(1, @operator.Calls);
    }

    [Fact]
    public async Task Parallel_status_polling_credits_only_once()
    {
        gateway.PayResult = FakeSvgate.Result(transportError: true);
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        await Verify(sessionId, token);
        App.Clock.Advance(TimeSpan.FromSeconds(5));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Status(sessionId, token)));

        Assert.Equal(1, @operator.Calls);
    }

    [Fact]
    public async Task Late_failed_recovery_does_not_override_success()
    {
        gateway.PayResult = FakeSvgate.Result(transportError: true);
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        await Verify(sessionId, token);
        App.Clock.Advance(TimeSpan.FromSeconds(5));
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var calls = 0;
        gateway.TransExt = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
                return FakeSvgate.Result(errorCode: -207);
            }
            return FakeSvgate.Result(new JsonObject { ["status"] = "OK", ["resp"] = 0, ["refNum"] = "123456789012" });
        };

        var first = Status(sessionId, token);
        await firstStarted.Task;
        var second = await Status(sessionId, token);
        releaseFirst.SetResult();
        await first;
        var final = await Status(sessionId, token);

        Assert.Equal("SUCCESS", await StatusOf(second));
        Assert.Equal("SUCCESS", await StatusOf(final));
        Assert.Equal(1, @operator.Calls);
    }

    // SV-GATE 1.0.35 defines -207 as "Transaction already exists": the debit may have happened, so a
    // pay reply carrying it is pending, never FAILED (Python TODO #8; found missing by mutation).
    [Fact]
    public async Task A_duplicate_code_on_pay_is_pending_not_failed()
    {
        gateway.PayResult = FakeSvgate.Result(errorCode: -207);
        gateway.ExtResult = FakeSvgate.Result(errorCode: -207);
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);

        var verification = await Verify(sessionId, token);

        Assert.Equal("""{"status":"PROCESSING","poll_after":2,"receipt":null}""", (await verification.Json())["result"]!.ToJsonString());
        Assert.Equal("PENDING_CHECK", await StatusOf(await Status(sessionId, token)));
        Assert.Equal(0, @operator.Calls);
    }

    // Several app instances scan the same PAID session; the PAID → CREDITING claim must let exactly
    // one of them call the Operator.
    [Fact]
    public async Task Parallel_recovery_claims_credit_once()
    {
        gateway.PayResult = FakeSvgate.Result(transportError: true);
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        await Verify(sessionId, token);
        await using (var db = App.Db())
        {
            await db.PaymentSessions.Where(session => session.Id == Guid.Parse(sessionId))
                .ExecuteUpdateAsync(setter => setter.SetProperty(session => session.Status, PaymentSessionStatus.PAID));
            await db.Payments.Where(payment => payment.SessionId == Guid.Parse(sessionId))
                .ExecuteUpdateAsync(setter => setter
                    .SetProperty(payment => payment.Status, PaymentStatus.PAID)
                    .SetProperty(payment => payment.SvRefNum, "123456789012"));
        }
        var release = new TaskCompletionSource();
        App.Stubs.Route("operator.test", async _ =>
        {
            Interlocked.Increment(ref @operator.Calls);
            await release.Task;
            return Http.Json(new JsonObject { ["success"] = true, ["transactionId"] = "operator-1" });
        });

        var scans = Enumerable.Range(0, 4).Select(_ => Task.Run(Recover)).ToList();
        await Task.Delay(500);
        release.SetResult();
        await Task.WhenAll(scans);

        Assert.Equal(1, @operator.Calls);
        Assert.Equal("SUCCESS", await StatusOf(await Status(sessionId, token)));
    }

    [Fact]
    public async Task Duplicate_gateway_code_does_not_credit()
    {
        gateway.PayResult = FakeSvgate.Result(transportError: true);
        gateway.ExtResult = FakeSvgate.Result(errorCode: -207);
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        await Verify(sessionId, token);
        App.Clock.Advance(TimeSpan.FromSeconds(5));

        var response = await Status(sessionId, token);

        Assert.Equal("PENDING_CHECK", await StatusOf(response));
        Assert.Equal(0, @operator.Calls);
    }

    public static TheoryData<string> IncompleteReversals => new()
    {
        """{"tranType":"REVERSAL","status":"ROK","resp":0}""",
        """{"tranType":"REVERSAL","status":"ROK","resp":5,"refNum":"123456789012"}""",
    };

    [Theory]
    [MemberData(nameof(IncompleteReversals))]
    public async Task Incomplete_reversal_row_stays_pending(string reversal)
    {
        gateway.PayResult = FakeSvgate.Result(transportError: true);
        gateway.ExtResult = FakeSvgate.Result(new JsonObject { ["transactions"] = new JsonArray(JsonNode.Parse(reversal)) });
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        await Verify(sessionId, token);
        App.Clock.Advance(TimeSpan.FromSeconds(5));

        var response = await Status(sessionId, token);

        Assert.Equal("PENDING_CHECK", await StatusOf(response));
        Assert.Equal(0, @operator.Calls);
    }

    [Fact]
    public async Task Empty_reverse_reply_needs_manual_review()
    {
        @operator.Ok = false;
        gateway.ReverseResult = FakeSvgate.Result(new JsonObject());
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);

        var response = await Verify(sessionId, token);

        Assert.Equal("NEEDS_MANUAL", await StatusOf(response));
        Assert.Equal(1, gateway.ReverseCalls);
    }

    [Fact]
    public async Task Status_polling_is_rate_limited()
    {
        var (sessionId, token) = await OpenedSession();
        var responses = new List<HttpResponseMessage>();
        for (var index = 0; index < 31; index++)
        {
            responses.Add(await Status(sessionId, token));
        }

        Assert.All(responses.Take(30), response => Assert.Equal(200, (int)response.StatusCode));
        Assert.Equal(429, (int)responses[30].StatusCode);
        Assert.NotEmpty(responses[30].Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task Background_recovery_credits_without_polling()
    {
        gateway.PayResult = FakeSvgate.Result(transportError: true);
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        await Verify(sessionId, token);
        App.Clock.Advance(TimeSpan.FromSeconds(5));

        await Recover();

        Assert.Equal(1, gateway.ExtCalls);
        Assert.Equal(1, @operator.Calls);
        Assert.Equal("SUCCESS", await StatusOf(await Status(sessionId, token)));
    }

    [Fact]
    public async Task Stale_paying_is_recovered_without_second_debit()
    {
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        var payStarted = new TaskCompletionSource();
        var releasePay = new TaskCompletionSource();
        gateway.TransPayPurpose = async () =>
        {
            Interlocked.Increment(ref gateway.PayCalls);
            payStarted.TrySetResult();
            await releasePay.Task;
            return FakeSvgate.Result(new JsonObject { ["status"] = "FAILED", ["resp"] = 51 });
        };

        var verification = Verify(sessionId, token);
        await payStarted.Task;
        await Recover();
        Assert.Equal(0, gateway.ExtCalls);
        Assert.Equal(0, gateway.RemoveTokenCalls);
        Assert.Equal("PAYING", await StatusOf(await Status(sessionId, token)));
        var stale = App.Clock.GetUtcNow() - TimeSpan.FromSeconds(25 * 2 + 20);
        await using (var db = App.Db())
        {
            await db.Payments.Where(payment => payment.SessionId == Guid.Parse(sessionId))
                .ExecuteUpdateAsync(setter => setter.SetProperty(payment => payment.CreatedAt, stale));
            var stored = await db.PaymentSessions.AsNoTracking().SingleAsync(session => session.Id == Guid.Parse(sessionId));
            Assert.Equal(PaymentSessionStatus.PAYING, stored.Status);
            Assert.True(stored.UpdatedAt > App.Clock.GetUtcNow() - TimeSpan.FromSeconds(25 * 2 + 15));
        }
        await Recover();
        releasePay.SetResult();
        await verification;
        var status = await Status(sessionId, token);

        Assert.Equal(1, gateway.PayCalls);
        Assert.Equal(1, gateway.ExtCalls);
        Assert.Equal(1, @operator.Calls);
        Assert.Equal("SUCCESS", await StatusOf(status));
    }

    [Fact]
    public async Task Background_retries_failed_card_token_removal()
    {
        gateway.RemoveTokenFailures = 1;
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        var verified = await Verify(sessionId, token);
        Assert.Equal("SUCCESS", await StatusOf(verified));
        Assert.Equal(1, gateway.RemoveTokenCalls);
        Assert.Equal(1, @operator.Calls);

        await Recover();

        Assert.Equal(2, gateway.RemoveTokenCalls);
        Assert.Equal(1, @operator.Calls);
    }

    [Fact]
    public async Task Expire_during_otp_verification_does_not_override_payment()
    {
        gateway.VerifyEntered = new TaskCompletionSource();
        gateway.VerifyRelease = new TaskCompletionSource();
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);

        var verifying = Verify(sessionId, token);
        await gateway.VerifyEntered.Task;
        var expired = await Post($"/api/uzcard/session/{sessionId}/expire", token, key: Guid.NewGuid().ToString());
        gateway.VerifyRelease.SetResult();
        var verified = await verifying;

        Assert.Equal(409, (int)expired.StatusCode);
        Assert.Equal("INVALID_STATE", await ErrorOf(expired));
        Assert.Equal("SUCCESS", await StatusOf(verified));
        Assert.Equal(1, @operator.Calls);
    }

    // The shapes docs/FRONTEND_API_HANDOFF.md promises the frontend, key order included. The Python
    // suite has no resend case; this is the only test of that path.
    [Fact]
    public async Task The_frontend_handoff_shapes_hold_for_a_whole_flow()
    {
        var (sessionId, token) = await OpenedSession();

        var card = await Card(sessionId, token);
        Assert.Equal(
            """{"success":true,"result":{"processing":"UZCARD","phone_mask":"****1234","otp_ttl":59,"resend_after":59,"attempts_left":3,"resends_left":3}}""",
            await card.Content.ReadAsStringAsync());

        var tooSoon = await Post($"/api/uzcard/session/{sessionId}/resend", token, new { pan = PAN, expiry = EXPIRY }, Guid.NewGuid().ToString());
        Assert.Equal(429, (int)tooSoon.StatusCode);
        Assert.Equal("OTP_RESEND_TOO_SOON", await ErrorOf(tooSoon));
        var retryAfter = (await tooSoon.Json())["error"]!["details"]!["retry_after"]!.GetValue<long>();
        Assert.InRange(retryAfter, 1, 59);
        Assert.Equal(retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture), tooSoon.Headers.GetValues("Retry-After").Single());

        var otherCard = await Post($"/api/uzcard/session/{sessionId}/resend", token, new { pan = "8600000000000007", expiry = EXPIRY }, Guid.NewGuid().ToString());
        Assert.Equal("CARD_INVALID", await ErrorOf(otherCard));

        await App.Redis.Delete($"otp:{sessionId}");
        var resent = await Post($"/api/uzcard/session/{sessionId}/resend", token, new { pan = PAN, expiry = EXPIRY }, Guid.NewGuid().ToString());
        Assert.Equal(
            """{"success":true,"result":{"processing":"UZCARD","phone_mask":"****1234","otp_ttl":59,"resend_after":59,"attempts_left":3,"resends_left":2}}""",
            await resent.Content.ReadAsStringAsync());
        Assert.Equal(2, gateway.OtpCalls);

        var verification = await Verify(sessionId, token);
        Assert.True(verification.IsSuccessStatusCode, await verification.Content.ReadAsStringAsync());
        var verified = (await verification.Json())["result"]!.AsObject();
        Assert.Equal(["status", "poll_after", "receipt"], verified.Select(item => item.Key));
        Assert.Null(verified["poll_after"]);
        Assert.Equal(["id", "number"], verified["receipt"]!.AsObject().Select(item => item.Key));

        var status = (await (await Status(sessionId, token)).Json())["result"]!.AsObject();
        Assert.Equal(["status", "processing", "updated_at", "receipt", "error"], status.Select(item => item.Key));
        Assert.Equal(("SUCCESS", "UZCARD"), (status["status"]!.GetValue<string>(), status["processing"]!.GetValue<string>()));
        Assert.Null(status["error"]);
        Assert.Matches(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{6})?Z\z", status["updated_at"]!.GetValue<string>());

        var late = await Post($"/api/uzcard/session/{sessionId}/expire", token, key: Guid.NewGuid().ToString());
        Assert.Equal(409, (int)late.StatusCode);
        Assert.Equal("""{"status":"SUCCESS"}""", (await late.Json())["error"]!["details"]!.ToJsonString());
    }

    [Fact]
    public async Task Expire_answers_with_the_documented_body_and_needs_an_idempotency_key()
    {
        var (sessionId, token) = await OpenedSession();

        var withoutKey = await Post($"/api/uzcard/session/{sessionId}/expire", token);
        var expired = await Post($"/api/uzcard/session/{sessionId}/expire", token, key: Guid.NewGuid().ToString());
        var afterwards = await Card(sessionId, token);

        Assert.Equal(422, (int)withoutKey.StatusCode);
        Assert.Equal("IDEMPOTENCY_REQUIRED", await ErrorOf(withoutKey));
        Assert.Equal("""{"success":true,"result":{"status":"EXPIRED","poll_after":null,"receipt":null}}""", await expired.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_STATE", await ErrorOf(afterwards));
    }

    // 180 s without activity, or the 600 s lifetime, ends a CREATED/OTP_SENT session; status still answers.
    [Fact]
    public async Task An_idle_session_expires_and_reports_it()
    {
        var (sessionId, token) = await OpenedSession();
        App.Clock.Advance(TimeSpan.FromSeconds(181));

        var card = await Card(sessionId, token);
        var status = await Status(sessionId, token);

        Assert.Equal(409, (int)card.StatusCode);
        Assert.Equal("SESSION_EXPIRED", await ErrorOf(card));
        Assert.Equal("EXPIRED", await StatusOf(status));
        Assert.Equal(0, gateway.OtpCalls);
    }

    // Not in the Python suite: the request that actually reaches SV-GATE, so a renamed JSON-RPC field fails here.
    [Fact]
    public async Task The_svgate_requests_keep_the_json_rpc_shape()
    {
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);
        await Verify(sessionId, token);

        var methods = gateway.Requests.Select(request => request["method"]!.GetValue<string>()).ToList();
        Assert.Equal(["cards.new.otp", "cards.new.verify", "trans.pay.purpose", "cards.remove.token"], methods);
        var otp = gateway.Requests[0];
        Assert.Equal("2.0", otp["jsonrpc"]!.GetValue<string>());
        Assert.Equal("""{"card":{"pan":"8600123412341239","expiry":"2912","sms":{"templateId":1,"serviceName":"Multipay"}}}""", otp["params"]!.ToJsonString());
        Assert.Equal("""{"otp":{"id":123456,"code":"A12B34"}}""", gateway.Requests[1]["params"]!.ToJsonString());
        Assert.Equal(
            ["purpose", "receiverId", "amount", "cardId", "commission", "currency", "ext", "merchantId", "terminalId", "merchantInfo"],
            gateway.Requests[2]["params"]!["tran"]!.AsObject().Select(item => item.Key));
    }

    // Found by a live test of the Python service: SV-GATE down at `cards.new.verify` answered 422
    // PAYMENT_FAILED although no payment had been attempted. The card step already answers 503 for
    // the same outage; verify must too, and leave the session in OTP_SENT so the user can retry.
    [Fact]
    public async Task An_unreachable_gateway_during_verify_is_not_reported_as_a_failed_payment()
    {
        gateway.VerifyResult = FakeSvgate.Result(transportError: true);
        var (sessionId, token) = await OpenedSession();
        await Card(sessionId, token);

        var response = await Verify(sessionId, token);
        var status = await Status(sessionId, token);

        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal("PROCESSING_UNAVAILABLE", await ErrorOf(response));
        Assert.Equal("OTP_SENT", await StatusOf(status));
        Assert.Equal(0, gateway.PayCalls);
        Assert.Equal(0, @operator.Calls);
    }

    // A process that dies between claiming CREDITING/REVERSING and finishing leaves nobody to move the
    // session on. Whether the Operator was credited is unknown, so recovery repeats nothing and hands
    // it to a human once the Operator and SV-GATE timeouts have certainly passed.
    [Theory]
    [InlineData(PaymentSessionStatus.CREDITING, PaymentStatus.CREDITING)]
    [InlineData(PaymentSessionStatus.REVERSING, PaymentStatus.REVERSING)]
    public async Task Settlement_interrupted_by_a_crash_is_handed_to_manual_review(PaymentSessionStatus stuckSession, PaymentStatus stuckPayment)
    {
        var (sessionId, _) = await OpenedSession();
        var id = Guid.Parse(sessionId);
        var staleSeconds = 10 + 25 * 2 + 15;
        await using (var db = App.Db())
        {
            var session = await db.PaymentSessions.SingleAsync(item => item.Id == id);
            db.Payments.Add(new PaymentService.Domain.Models.Payment
            {
                Id = Guid.NewGuid(),
                SessionId = id,
                Amount = session.Amount,
                Commission = session.Commission,
                Status = stuckPayment,
                SvRefNum = "REF1",
            });
            await db.SaveChangesAsync();
            var updatedAt = App.Clock.GetUtcNow() - TimeSpan.FromSeconds(staleSeconds - 5);
            await db.PaymentSessions.Where(item => item.Id == id).ExecuteUpdateAsync(setter => setter
                .SetProperty(item => item.Status, stuckSession)
                .SetProperty(item => item.UpdatedAt, updatedAt));
        }

        await Recover();

        await using (var db = App.Db())
        {
            Assert.Equal(stuckSession, (await db.PaymentSessions.AsNoTracking().SingleAsync(item => item.Id == id)).Status);
        }

        App.Clock.Advance(TimeSpan.FromSeconds(10));
        await Recover();

        await using (var db = App.Db())
        {
            Assert.Equal(PaymentSessionStatus.NEEDS_MANUAL, (await db.PaymentSessions.AsNoTracking().SingleAsync(item => item.Id == id)).Status);
            Assert.Equal(PaymentStatus.NEEDS_MANUAL, (await db.Payments.AsNoTracking().SingleAsync(item => item.SessionId == id)).Status);
        }
        Assert.Equal(0, @operator.Calls);
        Assert.Equal(0, gateway.ExtCalls);
    }

    // A proxy in front of SV-GATE can answer 200 with a bare JSON string. It is logged like any other
    // reply and treated as an unusable answer, not as a server error that strands the session.
    [Theory]
    [InlineData("\"Service unavailable\"")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task A_json_scalar_from_the_gateway_is_logged_and_handled(string reply)
    {
        gateway.RawReply = reply;
        var (sessionId, token) = await OpenedSession();

        var response = await Card(sessionId, token);

        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal("PROCESSING_UNAVAILABLE", await ErrorOf(response));
        Assert.Equal("CREATED", await StatusOf(await Status(sessionId, token)));
        await using var db = App.Db();
        Assert.Equal(1, await db.SvgateLogs.CountAsync());
    }
}
