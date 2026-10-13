using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Core.Dependencies;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Models;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.ExternalServices;
using PaymentService.Infrastructure.ExternalServices.Humo;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Utils;
using StatusCodes = PaymentService.Domain.Enums.StatusCodes;

namespace PaymentService.Infrastructure.Services;

public sealed class HumoService(
    PaymentSessionRepository sessions,
    PaymentRepository payments,
    ReceiptRepository receipts,
    RedisClient redis,
    RateLimitService limiter,
    IdempotencyService idempotency,
    HumoMiddlewareClient middleware,
    HumoSmsClient sms,
    HumoPaymentClient ps,
    OperatorClient @operator,
    HumoLogRepository logs,
    TimeProvider clock,
    IOptions<Settings> options) : IPaymentFlow
{
    private static readonly HashSet<PaymentSessionStatus> Fresh = [PaymentSessionStatus.CREATED, PaymentSessionStatus.OTP_SENT];

    private Settings settings => options.Value;

    public static string CardKey(Guid sessionId) => $"humo:card:{sessionId}";

    public static string OtpKey(Guid sessionId) => $"humo:otp:{sessionId}";

    private static ErrorResponse Error(StatusCodes code, string key, object? value) =>
        new(code, details: new Dictionary<string, object?> { [key] = value });

    private static SuccessResponse Processing(string status, int? pollAfter = null, object? receipt = null) =>
        new(new ProcessingResponse(status, pollAfter, receipt));

    private static JsonObject Masked(params (string Key, object? Value)[] fields)
    {
        var masked = new JsonObject();
        foreach (var (key, value) in fields)
        {
            masked[key] = JsonSerializer.SerializeToNode(value);
        }
        return masked;
    }

    private Task<T> Record<T>(Guid sessionId, string system, string method, JsonObject request, Func<Task<T>> operation) where T : IHumoResult =>
        HumoAudit.RecordHumoCall(logs, sessionId, system, method, request, operation);

    private PanCipher? Cipher() => PanCipher.From(settings.HUMO_PAN_ENCRYPTION_KEY);

    public async Task<bool> StoreCard(PaymentSession session, string pan, string expiry)
    {
        var cipher = Cipher();
        if (cipher is null)
        {
            return false;
        }
        var remaining = (int)Math.Truncate((session.ExpiresAt - clock.UtcNow()).TotalSeconds);
        if (remaining <= 0)
        {
            return false;
        }
        var value = cipher.Encrypt(new JsonObject { ["pan"] = pan, ["expiry"] = expiry }.ToJsonString(), CardKey(session.Id));
        return await redis.SetValue(CardKey(session.Id), value, remaining);
    }

    public async Task<(string Pan, string Expiry)?> LoadCard(Guid sessionId)
    {
        var value = await redis.Get(CardKey(sessionId));
        var cipher = Cipher();
        if (value is null || cipher is null)
        {
            return null;
        }
        var plaintext = cipher.Decrypt(value, CardKey(sessionId));
        JsonNode? card;
        try
        {
            card = plaintext is null ? null : JsonNode.Parse(plaintext);
        }
        catch (JsonException)
        {
            return null;
        }
        if (card is not JsonObject item || item.Get("pan") is not JsonValue pan || pan.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }
        return (pan.GetValue<string>(), Py.Str(item.Get("expiry")));
    }

    public async Task ClearCard(Guid sessionId)
    {
        await redis.Delete(CardKey(sessionId));
        await redis.Delete(OtpKey(sessionId));
    }

    public async Task<ErrorResponse?> CheckAccess(SessionAccess access, IReadOnlySet<PaymentSessionStatus>? allowed = null)
    {
        if (access.Error is not null)
        {
            return access.Error;
        }
        var session = access.PaymentSession;
        if (session is null)
        {
            return new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var now = clock.UtcNow();
        var idle = session.LastActivityAt is not null && now - session.LastActivityAt > TimeSpan.FromSeconds(settings.SESSION_IDLE_TIMEOUT_SECONDS);
        if (now >= session.ExpiresAt || idle)
        {
            if (Fresh.Contains(session.Status))
            {
                var transitioned = await sessions.SetStatus(session.Id, Fresh, PaymentSessionStatus.EXPIRED);
                if (transitioned is not null)
                {
                    await ClearCard(session.Id);
                }
            }
            return new ErrorResponse(StatusCodes.SESSION_EXPIRED);
        }
        if (allowed is not null && !allowed.Contains(session.Status))
        {
            return Error(StatusCodes.INVALID_STATE, "status", session.Status.ToString());
        }
        session.LastActivityAt = now;
        await sessions.Save(session);
        return null;
    }

    public static StatusCodes CardError(JsonNode? code) => Py.Str(code) switch
    {
        "200" or "210" or "424" or "428" => StatusCodes.CARD_INVALID,
        "423" or "429" or "430" => StatusCodes.CARD_TEMP_BLOCKED,
        "208" => StatusCodes.OTP_RESEND_TOO_SOON,
        "401" or "426" => StatusCodes.NO_PHONE,
        "125" or "425" => StatusCodes.CARD_BLOCKED,
        "402" or "500" => StatusCodes.SMS_FAILED,
        _ => StatusCodes.PROCESSING_UNAVAILABLE,
    };

    public static StatusCodes Card2Error(JsonObject result, string expiry)
    {
        if (result.Get("card") is not JsonObject card)
        {
            return StatusCodes.PROCESSING_UNAVAILABLE;
        }
        return !Py.StrEquals(card.Get("expiry"), expiry) ? StatusCodes.CARD_INVALID : StatusCodes.CARD_BLOCKED;
    }

    public static bool CardActive(JsonObject result, string expiry)
    {
        if (result.Get("card") is not JsonObject card || !Py.StrEquals(card.Get("expiry"), expiry))
        {
            return false;
        }
        if ((card.Get("statuses") as JsonObject).Get("item") is not JsonArray items || items.Count == 0)
        {
            return false;
        }
        if (items.Any(item => item is not JsonObject status || Py.Str(status.Get("actionCode")) != "000"))
        {
            return false;
        }
        return result.Get("account") is JsonArray accounts
            && accounts.Any(account => account is JsonObject item && Py.StrEquals(item.Get("status"), "Active"));
    }

    public async Task<BaseResponse> SendCard(SessionAccess access, CardSchema card, string clientIp, string? idempotencyKey)
    {
        var session = access.PaymentSession;
        if (session is null)
        {
            return access.Error ?? new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var started = await idempotency.Start($"{session.Id}:card", idempotencyKey, card.ModelDump());
        if (started.Response is not null)
        {
            return started.Response;
        }
        var result = await SendCardChecked(access, card, clientIp);
        await idempotency.Complete(started.CacheKey, result);
        return result;
    }

    private async Task<BaseResponse> SendCardChecked(SessionAccess access, CardSchema card, string clientIp)
    {
        var error = await CheckAccess(access, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.CREATED });
        if (error is not null)
        {
            return error;
        }
        if (!settings.HUMO_ENABLED || !Card.HumoPan(card.Pan))
        {
            return new ErrorResponse(StatusCodes.CARD_NOT_SUPPORTED);
        }
        var session = access.PaymentSession!;
        var expiry = Card.ExpiryToYymm(card.Expiry, clock.UtcNow());
        if (!Card.LuhnValid(card.Pan) || expiry is null)
        {
            return new ErrorResponse(StatusCodes.CARD_INVALID);
        }
        var digest = Card.PanDigest(card.Pan, settings.PAN_HASH_PEPPER);
        var phoneHash = Defaults.Sha256Hex(session.Phone)[..32];
        if (!string.IsNullOrEmpty(await redis.Get($"otp_lock:{digest}")) || !string.IsNullOrEmpty(await redis.Get($"otp_lock:phone:{phoneHash}")))
        {
            return new ErrorResponse(StatusCodes.CARD_TEMP_BLOCKED);
        }
        var wait = await limiter.CardWait(digest, clientIp, clock.UtcNow());
        if (wait != 0)
        {
            return Error(StatusCodes.RATE_LIMITED, "retry_after", wait);
        }
        var cardAttempts = session.CardAttempts + 1;
        var transitioned = await sessions.SetStatus(
            session.Id, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.CREATED }, PaymentSessionStatus.OTP_SENDING,
            item => item.CardAttempts = cardAttempts);
        if (transitioned is null)
        {
            return new ErrorResponse(StatusCodes.INVALID_STATE);
        }
        session = transitioned;
        var check = await Record(session.Id, "MW", "card2", Masked(("primaryAccountNumber", "***")), () => middleware.Card2(card.Pan));
        if (!check.Ok || !CardActive(check.Result, expiry))
        {
            session.Status = PaymentSessionStatus.CREATED;
            await sessions.Save(session);
            return new ErrorResponse(!check.Ok ? CardError(check.ErrorCode) : Card2Error(check.Result, expiry));
        }
        var smsWait = await SmsWait(digest, clientIp);
        if (smsWait != 0)
        {
            session.Status = PaymentSessionStatus.CREATED;
            await sessions.Save(session);
            return Error(StatusCodes.RATE_LIMITED, "retry_after", smsWait);
        }
        var sent = await Record(session.Id, "SMS", "sms2", Masked(("pan", "***"), ("expiry", "***")), () => sms.SendSms2(card.Pan, expiry));
        var phoneMask = sent.Ok ? sent.Result.Get("msisdn") : null;
        if (phoneMask is not JsonValue maskValue || maskValue.GetValueKind() != JsonValueKind.String || maskValue.GetValue<string>().Length == 0)
        {
            session.Status = PaymentSessionStatus.CREATED;
            await sessions.Save(session);
            return new ErrorResponse(CardError(sent.ErrorCode));
        }
        if (!await StoreCard(session, card.Pan, expiry))
        {
            session.Status = PaymentSessionStatus.FAILED;
            await sessions.Save(session);
            return new ErrorResponse(StatusCodes.PROCESSING_UNAVAILABLE);
        }
        session.Processing = "HUMO";
        session.Status = PaymentSessionStatus.OTP_SENT;
        session.PanHash = digest;
        session.PanMasked = Card.MaskPan(card.Pan);
        var holder = (check.Result["card"] as JsonObject).Get("nameOnCard");
        session.CardHolder = holder is null ? null : Py.Str(holder);
        session.OtpSentAt = clock.UtcNow();
        session.OtpAttempts = 0;
        session.OtpResends = 0;
        await sessions.Save(session);
        await redis.SetValue(OtpKey(session.Id), "1", settings.OTP_TTL_SECONDS);
        return new SuccessResponse(new OtpSentResponse(
            "HUMO", maskValue.GetValue<string>(), settings.OTP_TTL_SECONDS, settings.OTP_TTL_SECONDS, 3, 3));
    }

    public async Task<BaseResponse> Resend(SessionAccess access, CardSchema card, string clientIp, string? idempotencyKey)
    {
        var session = access.PaymentSession;
        if (session is null)
        {
            return access.Error ?? new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var started = await idempotency.Start($"{session.Id}:resend", idempotencyKey, card.ModelDump());
        if (started.Response is not null)
        {
            return started.Response;
        }
        var result = await ResendChecked(access, card, clientIp);
        await idempotency.Complete(started.CacheKey, result);
        return result;
    }

    private async Task<BaseResponse> ResendChecked(SessionAccess access, CardSchema card, string clientIp)
    {
        var error = await CheckAccess(access, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.OTP_SENT });
        if (error is not null)
        {
            return error;
        }
        var session = access.PaymentSession!;
        var expiry = Card.ExpiryToYymm(card.Expiry, clock.UtcNow());
        if (Card.PanDigest(card.Pan, settings.PAN_HASH_PEPPER) != session.PanHash || expiry is null)
        {
            return new ErrorResponse(StatusCodes.CARD_INVALID);
        }
        var ttl = await redis.Ttl(OtpKey(session.Id));
        if (ttl > 0)
        {
            return Error(StatusCodes.OTP_RESEND_TOO_SOON, "retry_after", ttl);
        }
        if (session.OtpResends >= 3)
        {
            return new ErrorResponse(StatusCodes.OTP_RESEND_LIMIT);
        }
        var wait = await limiter.CardWait(session.PanHash!, clientIp, clock.UtcNow());
        if (wait != 0)
        {
            return Error(StatusCodes.RATE_LIMITED, "retry_after", wait);
        }
        var smsWait = await SmsWait(session.PanHash!, clientIp);
        if (smsWait != 0)
        {
            return Error(StatusCodes.RATE_LIMITED, "retry_after", smsWait);
        }
        var transitioned = await sessions.SetStatus(
            session.Id, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.OTP_SENT }, PaymentSessionStatus.OTP_SENDING);
        if (transitioned is null)
        {
            return new ErrorResponse(StatusCodes.INVALID_STATE);
        }
        session = transitioned;
        var sent = await Record(session.Id, "SMS", "sms2", Masked(("pan", "***"), ("expiry", "***")), () => sms.SendSms2(card.Pan, expiry));
        if (!sent.Ok || !Py.Truthy(sent.Result.Get("msisdn")))
        {
            session.Status = PaymentSessionStatus.OTP_SENT;
            await sessions.Save(session);
            return new ErrorResponse(CardError(sent.ErrorCode));
        }
        if (!await StoreCard(session, card.Pan, expiry))
        {
            session.Status = PaymentSessionStatus.FAILED;
            await sessions.Save(session);
            return new ErrorResponse(StatusCodes.PROCESSING_UNAVAILABLE);
        }
        session.Status = PaymentSessionStatus.OTP_SENT;
        session.OtpSentAt = clock.UtcNow();
        session.OtpResends += 1;
        await sessions.Save(session);
        await redis.SetValue(OtpKey(session.Id), "1", settings.OTP_TTL_SECONDS);
        return new SuccessResponse(new OtpSentResponse(
            "HUMO", Py.Str(sent.Result.Get("msisdn")), settings.OTP_TTL_SECONDS, settings.OTP_TTL_SECONDS,
            3 - session.OtpAttempts, 3 - session.OtpResends));
    }

    public async Task<long> SmsWait(string digest, string clientIp)
    {
        var now = clock.UtcNow();
        var panWait = await limiter.Hit($"humo-sms-pan:{digest}", now, [("1h", 3, 3600)]);
        if (panWait != 0)
        {
            return panWait;
        }
        return await limiter.Hit($"humo-sms-ip:{clientIp}", now, [("1h", 10, 3600)]);
    }

    public async Task<BaseResponse> Verify(SessionAccess access, VerifySchema request, string? idempotencyKey)
    {
        var session = access.PaymentSession;
        if (session is null)
        {
            return access.Error ?? new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var started = await idempotency.Start($"{session.Id}:verify", idempotencyKey, request.ModelDump());
        if (started.Response is not null)
        {
            return started.Response;
        }
        var result = await VerifyChecked(access, request);
        await idempotency.Complete(started.CacheKey, result);
        return result;
    }

    private async Task<BaseResponse> VerifyChecked(SessionAccess access, VerifySchema request)
    {
        var error = await CheckAccess(access, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.OTP_SENT });
        if (error is not null)
        {
            return error;
        }
        if (!request.PaymentConfirmed)
        {
            return new ErrorResponse(StatusCodes.INVALID_REQUEST);
        }
        var session = access.PaymentSession!;
        if (string.IsNullOrEmpty(await redis.Get(OtpKey(session.Id))))
        {
            return new ErrorResponse(StatusCodes.OTP_EXPIRED);
        }
        var card = await LoadCard(session.Id);
        if (card is null)
        {
            return new ErrorResponse(StatusCodes.PROCESSING_UNAVAILABLE);
        }
        var transitioned = await sessions.SetStatus(
            session.Id, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.OTP_SENT }, PaymentSessionStatus.OTP_VERIFYING);
        if (transitioned is null)
        {
            return new ErrorResponse(StatusCodes.INVALID_STATE);
        }
        session = transitioned;
        var verified = await Record(
            session.Id, "SMS", "code2", Masked(("pan", "***"), ("code", "***")),
            () => sms.VerifyCode2(card.Value.Pan, request.Code.ToUpperInvariant()));
        if (!verified.Ok)
        {
            ErrorResponse result;
            session.Status = PaymentSessionStatus.OTP_SENT;
            var code = Py.Str(verified.ErrorCode);
            if (code is "404" or "423")
            {
                session.OtpAttempts += 1;
                if (session.OtpAttempts >= 3 || code == "423")
                {
                    session.Status = PaymentSessionStatus.FAILED;
                    session.FailCode = StatusCodes.OTP_LOCKED.Name;
                    await redis.SetValue($"otp_lock:{session.PanHash ?? "None"}", "1", settings.OTP_LOCK_SECONDS);
                    var phoneHash = Defaults.Sha256Hex(session.Phone)[..32];
                    await redis.SetValue($"otp_lock:phone:{phoneHash}", "1", settings.OTP_LOCK_SECONDS);
                    await ClearCard(session.Id);
                    result = new ErrorResponse(StatusCodes.OTP_LOCKED);
                }
                else
                {
                    result = Error(StatusCodes.OTP_WRONG, "attempts_left", 3 - session.OtpAttempts);
                }
            }
            else
            {
                result = new ErrorResponse(CardError(verified.ErrorCode));
            }
            await sessions.Save(session);
            return result;
        }
        if (!Py.Truthy(verified.Result.Get("token")))
        {
            session.Status = PaymentSessionStatus.FAILED;
            await sessions.Save(session);
            await ClearCard(session.Id);
            return new ErrorResponse(StatusCodes.PROCESSING_UNAVAILABLE);
        }
        await redis.Delete(OtpKey(session.Id));
        if (settings.HUMO_BALANCE_PRECHECK)
        {
            var balanceResult = await Record(
                session.Id, "MW", "card", Masked(("primaryAccountNumber", "***")), () => middleware.Card(card.Value.Pan));
            if (balanceResult.Ok && balanceResult.Result.Get("balance") is JsonObject balance)
            {
                if (Py.Str(balance.Get("currency")) is not ("860" or "UZS"))
                {
                    session.Status = PaymentSessionStatus.FAILED;
                    await sessions.Save(session);
                    await ClearCard(session.Id);
                    return new ErrorResponse(StatusCodes.CARD_NOT_SUPPORTED);
                }
                var available = balance.Has("availableAmount") ? Py.Int(balance.Get("availableAmount")) ?? 0 : 0;
                if (available > 0 && available < session.Total)
                {
                    session.Status = PaymentSessionStatus.FAILED;
                    await sessions.Save(session);
                    await ClearCard(session.Id);
                    return new ErrorResponse(StatusCodes.INSUFFICIENT_FUNDS);
                }
            }
        }
        session.Status = PaymentSessionStatus.CARD_VERIFIED;
        await sessions.Save(session);
        return await Hold(session, card.Value);
    }

    public async Task<BaseResponse> Hold(PaymentSession session, (string Pan, string Expiry) card)
    {
        var transitioned = await sessions.SetStatus(
            session.Id, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.CARD_VERIFIED }, PaymentSessionStatus.PAYING);
        if (transitioned is null)
        {
            return new ErrorResponse(StatusCodes.INVALID_STATE);
        }
        session = transitioned;
        var payment = await payments.Create(new Payment
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Amount = session.Amount,
            Commission = session.Commission,
            Status = PaymentStatus.PAYING,
        });
        var paymentRef = payment.Id.ToString();
        IHumoResult response = await Record(
            session.Id, "PS", "Payment.hold",
            Masked(("paymentRef", paymentRef), ("pan", "***"), ("expiry", "***"), ("amount", session.Amount)),
            () => ps.Hold(paymentRef, card.Pan, card.Expiry, session.Amount));
        await Record(session.Id, "SMS", "deactivate", Masked(("pan", "***")), () => sms.Deactivate(card.Pan));
        await ClearCard(session.Id);
        if (response.TransportError)
        {
            response = await Record(session.Id, "PS", "GetPayment", Masked(("paymentRef", paymentRef)), () => ps.GetPayment(paymentRef));
        }
        var active = response.Ok && (Py.StrEquals(response.Result.Get("action"), "4") || Py.StrEquals(response.Result.Get("status"), "active"));
        var authCode = response.Result.Get("auth_action_code");
        var approved = authCode is null || Py.StrEquals(authCode, "000");
        if (!active || !approved)
        {
            BaseResponse result;
            if (!response.Ok)
            {
                session.Status = PaymentSessionStatus.NEEDS_MANUAL;
                payment.Status = PaymentStatus.NEEDS_MANUAL;
                result = Processing("NEEDS_MANUAL");
            }
            else
            {
                var code = Py.StrEquals(authCode, "116") ? StatusCodes.INSUFFICIENT_FUNDS : StatusCodes.PAYMENT_FAILED;
                session.Status = PaymentSessionStatus.FAILED;
                session.FailCode = code.Name;
                payment.Status = PaymentStatus.FAILED;
                result = new ErrorResponse(code);
            }
            await sessions.Save(session);
            await payments.Save(payment);
            return result;
        }
        payment.HumoPaymentId = Py.StrOrNone(response.Result.Get("paymentID"));
        payment.HumoRrn = Py.StrOrNone(response.Result.Get("auth_ref_number"));
        payment.HumoApprCode = Py.StrOrNone(response.Result.Get("auth_appr_code"));
        payment.HumoAction = "4";
        var cardholder = response.Result.Get("cardholder_amount1a");
        payment.AmountCardholder = Py.Int(Py.Truthy(cardholder) ? cardholder : response.Result.Get("amount1a"));
        payment.Status = PaymentStatus.HELD;
        session.Status = PaymentSessionStatus.HELD;
        await payments.Save(payment);
        await sessions.Save(session);
        if (payment.AmountCardholder != session.Total)
        {
            return await Cancel(session, payment, PaymentSessionStatus.HELD);
        }
        return await Credit(session, payment);
    }

    public async Task<BaseResponse> Credit(PaymentSession session, Payment payment)
    {
        var claimed = await sessions.SetStatus(
            session.Id, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.HELD }, PaymentSessionStatus.CREDITING);
        if (claimed is null)
        {
            return Processing("PROCESSING", 2);
        }
        session = claimed;
        payment.Status = PaymentStatus.CREDITING;
        await payments.Save(payment);
        var operatorResult = await @operator.Credit(
            session.OrderId, session.Phone, session.Amount, string.IsNullOrEmpty(payment.HumoRrn) ? payment.Id.ToString() : payment.HumoRrn);
        if (operatorResult.Uncertain)
        {
            session.Status = PaymentSessionStatus.NEEDS_MANUAL;
            payment.Status = PaymentStatus.NEEDS_MANUAL;
            await sessions.Save(session);
            await payments.Save(payment);
            return Processing("NEEDS_MANUAL");
        }
        if (!operatorResult.Ok)
        {
            return await Cancel(session, payment, PaymentSessionStatus.CREDITING);
        }
        payment.OperatorTxId = operatorResult.TransactionId;
        session.Status = PaymentSessionStatus.CONFIRMING;
        payment.Status = PaymentStatus.CONFIRMING;
        await sessions.Save(session);
        await payments.Save(payment);
        var paymentRef = payment.Id.ToString();
        IHumoResult confirmed = await Record(session.Id, "PS", "Payment.confirm", Masked(("paymentRef", paymentRef)), () => ps.Confirm(paymentRef));
        if (confirmed.TransportError)
        {
            confirmed = await Record(session.Id, "PS", "GetPayment", Masked(("paymentRef", paymentRef)), () => ps.GetPayment(paymentRef));
        }
        if (confirmed.Ok && (Py.StrEquals(confirmed.Result.Get("action"), "10") || Py.StrEquals(confirmed.Result.Get("status"), "finished")))
        {
            return await Finish(session, payment, confirmed);
        }
        session.Status = PaymentSessionStatus.PENDING_CHECK;
        payment.Status = PaymentStatus.PENDING_CHECK;
        payment.NextCheckAt = clock.UtcNow() + TimeSpan.FromSeconds(5);
        await sessions.Save(session);
        await payments.Save(payment);
        return Processing("PROCESSING", 2);
    }

    public async Task<BaseResponse> Finish(PaymentSession session, Payment payment, IHumoResult response)
    {
        var transitioned = await sessions.SetStatus(
            session.Id,
            new HashSet<PaymentSessionStatus> { PaymentSessionStatus.CONFIRMING, PaymentSessionStatus.PENDING_CHECK },
            PaymentSessionStatus.PAID);
        if (transitioned is null)
        {
            return Processing("PROCESSING", 2);
        }
        session = transitioned;
        payment.HumoAction = "10";
        var rrn = response.Result.Get("auth_ref_number");
        payment.HumoRrn = Py.Truthy(rrn) ? Py.Str(rrn) : payment.HumoRrn;
        payment.Status = PaymentStatus.PAID;
        await payments.Save(payment);
        var local = clock.UtcNow().ToOffset(Defaults.TASHKENT);
        var receipt = await receipts.Create(new Receipt
        {
            PaymentId = payment.Id,
            Number = $"PAY-{local:yyyyMMdd}-{payment.Id.ToString()[..8].ToUpperInvariant()}",
            Data = new JsonObject
            {
                ["date"] = Defaults.IsoFormat(clock.UtcNow().ToOffset(Defaults.TASHKENT)),
                ["phone"] = Phone.MaskPhone(session.Phone),
                ["amount"] = payment.Amount,
                ["commission"] = payment.Commission,
                ["total"] = payment.Amount + payment.Commission,
                ["card"] = session.PanMasked,
                ["processing"] = "HUMO",
                ["reference"] = string.IsNullOrEmpty(payment.HumoRrn) ? payment.HumoPaymentId : payment.HumoRrn,
                ["status"] = "SUCCESS",
            },
        });
        payment.Status = PaymentStatus.SUCCESS;
        session.Status = PaymentSessionStatus.SUCCESS;
        await payments.Save(payment);
        await sessions.Save(session);
        return Processing("SUCCESS", receipt: new Dictionary<string, object?> { ["id"] = receipt.Id.ToString(), ["number"] = receipt.Number });
    }

    public async Task<BaseResponse> Cancel(PaymentSession session, Payment payment, PaymentSessionStatus expected)
    {
        var transitioned = await sessions.SetStatus(
            session.Id, new HashSet<PaymentSessionStatus> { expected }, PaymentSessionStatus.CANCELLING);
        if (transitioned is null)
        {
            return new ErrorResponse(StatusCodes.INVALID_STATE);
        }
        session = transitioned;
        payment.Status = PaymentStatus.CANCELLING;
        await payments.Save(payment);
        var paymentRef = payment.Id.ToString();
        IHumoResult cancelled = await Record(session.Id, "PS", "CancelRequest", Masked(("paymentRef", paymentRef)), () => ps.Cancel(paymentRef));
        if (!cancelled.Ok)
        {
            var checkedPayment = await Record(session.Id, "PS", "GetPayment", Masked(("paymentRef", paymentRef)), () => ps.GetPayment(paymentRef));
            if (checkedPayment.Ok && Py.StrEquals(checkedPayment.Result.Get("status"), "cancelled"))
            {
                cancelled = checkedPayment;
            }
            else if (checkedPayment.Ok && Py.StrEquals(checkedPayment.Result.Get("status"), "finished"))
            {
                return await ReturnFinished(session, payment, checkedPayment);
            }
        }
        var cancelCode = cancelled.Result.Get("auth_action_code_final");
        BaseResponse result;
        if (cancelled.Ok && (cancelCode is null || Py.StrEquals(cancelCode, "000") || Py.StrEquals(cancelCode, "0")))
        {
            session.Status = PaymentSessionStatus.CANCELLED;
            payment.Status = PaymentStatus.CANCELLED;
            result = Processing("CANCELLED");
        }
        else
        {
            session.Status = PaymentSessionStatus.NEEDS_MANUAL;
            payment.Status = PaymentStatus.NEEDS_MANUAL;
            result = Processing("NEEDS_MANUAL");
        }
        await sessions.Save(session);
        await payments.Save(payment);
        return result;
    }

    public async Task<BaseResponse> ReturnFinished(PaymentSession session, Payment payment, IHumoResult checkedPayment)
    {
        var paymentId = !string.IsNullOrEmpty(payment.HumoPaymentId) ? payment.HumoPaymentId : Py.StrOrNone(checkedPayment.Result.Get("paymentID"));
        if (paymentId is null)
        {
            session.Status = PaymentSessionStatus.NEEDS_MANUAL;
            payment.Status = PaymentStatus.NEEDS_MANUAL;
            await sessions.Save(session);
            await payments.Save(payment);
            return Processing("NEEDS_MANUAL");
        }
        session.Status = PaymentSessionStatus.RETURNING;
        payment.Status = PaymentStatus.RETURNING;
        payment.CheckAttempts = 0;
        payment.NextCheckAt = clock.UtcNow() + TimeSpan.FromSeconds(15);
        await sessions.Save(session);
        await payments.Save(payment);
        var paymentRef = payment.Id.ToString();
        var returned = await Record(
            session.Id, "PS", "ReturnPayment", Masked(("paymentRef", paymentRef), ("paymentID", paymentId)),
            () => ps.ReturnPayment(paymentRef, paymentId));
        var returnCode = returned.Result.Get("auth_action_code_final");
        BaseResponse result;
        if (returned.Ok && (returnCode is null || Py.StrEquals(returnCode, "000") || Py.StrEquals(returnCode, "0")))
        {
            result = Processing("RETURNING");
        }
        else
        {
            session.Status = PaymentSessionStatus.NEEDS_MANUAL;
            payment.Status = PaymentStatus.NEEDS_MANUAL;
            result = Processing("NEEDS_MANUAL");
        }
        await sessions.Save(session);
        await payments.Save(payment);
        return result;
    }

    public async Task<BaseResponse> Status(SessionAccess access, string clientIp)
    {
        if (access.PaymentSession is not null)
        {
            var wait = await limiter.StatusWait("humo", access.PaymentSession.Id.ToString(), clientIp, clock.UtcNow());
            if (wait != 0)
            {
                return Error(StatusCodes.RATE_LIMITED, "retry_after", wait);
            }
        }
        var error = await CheckAccess(access);
        if (error is not null && error.Code != StatusCodes.SESSION_EXPIRED.Name)
        {
            return error;
        }
        var session = access.PaymentSession;
        if (session is null)
        {
            return new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var payment = await payments.GetBySessionId(session.Id);
        if (payment is not null && session.Status == PaymentSessionStatus.PENDING_CHECK
            && (payment.NextCheckAt is null || clock.UtcNow() >= payment.NextCheckAt))
        {
            var paymentRef = payment.Id.ToString();
            var checkedPayment = await Record(session.Id, "PS", "GetPayment", Masked(("paymentRef", paymentRef)), () => ps.GetPayment(paymentRef));
            if (checkedPayment.Ok && Py.StrEquals(checkedPayment.Result.Get("status"), "finished"))
            {
                return await Finish(session, payment, checkedPayment);
            }
            if (checkedPayment.Ok && Py.StrEquals(checkedPayment.Result.Get("status"), "active") && payment.CheckAttempts == 0)
            {
                var confirmed = await Record(session.Id, "PS", "Payment.confirm", Masked(("paymentRef", paymentRef)), () => ps.Confirm(paymentRef));
                if (confirmed.Ok && Py.StrEquals(confirmed.Result.Get("action"), "10"))
                {
                    return await Finish(session, payment, confirmed);
                }
            }
            payment.CheckAttempts += 1;
            if (payment.CheckAttempts >= 3)
            {
                session.Status = PaymentSessionStatus.NEEDS_MANUAL;
                payment.Status = PaymentStatus.NEEDS_MANUAL;
                await sessions.Save(session);
            }
            else
            {
                payment.NextCheckAt = clock.UtcNow() + TimeSpan.FromSeconds(new[] { 15, 30 }[payment.CheckAttempts - 1]);
            }
            await payments.Save(payment);
        }
        object? receiptData = null;
        if (payment is not null)
        {
            var receipt = await receipts.GetByPaymentId(payment.Id);
            if (receipt is not null)
            {
                receiptData = new Dictionary<string, object?> { ["id"] = receipt.Id.ToString(), ["number"] = receipt.Number };
            }
        }
        return new SuccessResponse(new SessionStatusResponse(
            session.Status.ToString(),
            session.Processing,
            session.UpdatedAt,
            receiptData,
            session.FailCode is { Length: > 0 } failCode ? new Dictionary<string, object?> { ["code"] = failCode } : null));
    }

    public async Task<BaseResponse> Expire(SessionAccess access, string? idempotencyKey)
    {
        var session = access.PaymentSession;
        if (session is null)
        {
            return access.Error ?? new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var started = await idempotency.Start($"{session.Id}:expire", idempotencyKey);
        if (started.Response is not null)
        {
            return started.Response;
        }
        BaseResponse result;
        if (session.Status == PaymentSessionStatus.HELD)
        {
            var payment = await payments.GetBySessionId(session.Id);
            result = payment is not null ? await Cancel(session, payment, PaymentSessionStatus.HELD) : new ErrorResponse(StatusCodes.INVALID_STATE);
        }
        else if (Fresh.Contains(session.Status))
        {
            var transitioned = await sessions.SetStatus(session.Id, Fresh, PaymentSessionStatus.EXPIRED);
            if (transitioned is null)
            {
                result = new ErrorResponse(StatusCodes.INVALID_STATE);
            }
            else
            {
                await ClearCard(session.Id);
                result = Processing("EXPIRED");
            }
        }
        else
        {
            result = Error(StatusCodes.INVALID_STATE, "status", session.Status.ToString());
        }
        await idempotency.Complete(started.CacheKey, result);
        return result;
    }
}
