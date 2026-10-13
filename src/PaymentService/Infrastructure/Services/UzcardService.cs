using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Core.Dependencies;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Models;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.ExternalServices;
using PaymentService.Infrastructure.ExternalServices.Uzcard;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Utils;
using StatusCodes = PaymentService.Domain.Enums.StatusCodes;

namespace PaymentService.Infrastructure.Services;

public sealed class UzcardService(
    PaymentSessionRepository sessionRepo,
    PaymentRepository paymentRepo,
    ReceiptRepository receiptRepo,
    SvgateLogRepository logRepo,
    RedisClient redisClient,
    RateLimitService rateLimit,
    IdempotencyService idempotency,
    SvgateClient svgate,
    OperatorClient @operator,
    TimeProvider clock,
    IOptions<Settings> options,
    ILogger<UzcardService> logger) : IPaymentFlow
{
    private static readonly HashSet<PaymentSessionStatus> Fresh = [PaymentSessionStatus.CREATED, PaymentSessionStatus.OTP_SENT];

    private Settings settings => options.Value;

    private static ErrorResponse Error(StatusCodes code, string key, object? value) =>
        new(code, details: new Dictionary<string, object?> { [key] = value });

    private static SuccessResponse Processing(string status, int? pollAfter = null, object? receipt = null) =>
        new(new ProcessingResponse(status, pollAfter, receipt));

    public async Task<ErrorResponse?> ValidateAccess(SessionAccess access, IReadOnlySet<PaymentSessionStatus>? allowed = null)
    {
        if (access.Error is not null)
        {
            return access.Error;
        }
        var paymentSession = access.PaymentSession;
        if (paymentSession is null)
        {
            return new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var now = clock.UtcNow();
        var idleExpired = paymentSession.LastActivityAt is not null
            && now - paymentSession.LastActivityAt > TimeSpan.FromSeconds(settings.SESSION_IDLE_TIMEOUT_SECONDS);
        if (now >= paymentSession.ExpiresAt || idleExpired)
        {
            if (Fresh.Contains(paymentSession.Status))
            {
                var transitioned = await sessionRepo.SetStatus(paymentSession.Id, Fresh, PaymentSessionStatus.EXPIRED);
                if (transitioned is not null)
                {
                    await RemoveCardToken(transitioned);
                }
            }
            return new ErrorResponse(StatusCodes.SESSION_EXPIRED);
        }
        if (allowed is not null && !allowed.Contains(paymentSession.Status))
        {
            return Error(StatusCodes.INVALID_STATE, "status", paymentSession.Status.ToString());
        }
        paymentSession.LastActivityAt = now;
        await sessionRepo.Save(paymentSession);
        return null;
    }

    public async Task LogResult(Guid sessionId, string method, SvgateResult result) =>
        await logRepo.Create(new SvgateLog
        {
            SessionId = sessionId,
            Method = method,
            RequestMasked = result.RequestMasked,
            ResponseMasked = result.ResponseMasked,
            DurationMs = result.DurationMs,
            ErrorCode = result.ErrorCode is null ? null : Py.Str(result.ErrorCode),
        });

    public async Task<bool> RemoveCardToken(PaymentSession paymentSession)
    {
        var token = paymentSession.SvCardToken;
        if (string.IsNullOrEmpty(token))
        {
            return true;
        }
        var gateway = await svgate.CardsRemoveToken(token);
        await LogResult(paymentSession.Id, "cards.remove.token", gateway);
        var removed = gateway.Ok && gateway.Result.Get("tokens") is JsonArray rows && rows.Any(row =>
            row is JsonObject item && Py.StrEquals(item.Get("id"), token) && Py.Str(item.Get("Status")) == "1");
        if (removed)
        {
            await sessionRepo.ClearCardToken(paymentSession.Id, token);
            paymentSession.SvCardToken = null;
        }
        else
        {
            logger.LogWarning("UZCARD card token removal needs retry for session {SessionId}", paymentSession.Id);
        }
        return removed;
    }

    public static StatusCodes CardError(SvgateResult result)
    {
        if (Py.CodeIn(result.ErrorCode, -200, -201, -202, -212, -216))
        {
            return StatusCodes.CARD_INVALID;
        }
        if (Py.CodeIn(result.ErrorCode, -204, -205, -346))
        {
            return StatusCodes.CARD_BLOCKED;
        }
        if (Py.CodeIn(result.ErrorCode, -261))
        {
            return StatusCodes.CARD_TEMP_BLOCKED;
        }
        if (Py.CodeIn(result.ErrorCode, -292))
        {
            return StatusCodes.SMS_FAILED;
        }
        if (Py.CodeIn(result.ErrorCode, -258))
        {
            return StatusCodes.CORPORATE_CARD;
        }
        return StatusCodes.PROCESSING_UNAVAILABLE;
    }

    public static StatusCodes PaymentError(SvgateResult result)
    {
        var responseCode = Py.Str(result.Result.Get("resp"));
        if (Py.CodeIn(result.ErrorCode, -240, -260) || responseCode == "51")
        {
            return StatusCodes.INSUFFICIENT_FUNDS;
        }
        if (Py.CodeIn(result.ErrorCode, -262) || responseCode is "61" or "65")
        {
            return StatusCodes.LIMIT_EXCEEDED;
        }
        if (Py.CodeIn(result.ErrorCode, -333))
        {
            return StatusCodes.FRAUD_DECLINED;
        }
        if (Py.CodeIn(result.ErrorCode, -258))
        {
            return StatusCodes.CORPORATE_CARD;
        }
        if (Py.CodeIn(result.ErrorCode, -261))
        {
            return StatusCodes.CARD_TEMP_BLOCKED;
        }
        if (Py.CodeIn(result.ErrorCode, -204, -205, -346) || responseCode is "36" or "41" or "43")
        {
            return StatusCodes.CARD_BLOCKED;
        }
        if (responseCode == "33")
        {
            return StatusCodes.CARD_INVALID;
        }
        return StatusCodes.PAYMENT_FAILED;
    }

    public static bool IsPending(SvgateResult result) =>
        result.TransportError || Py.CodeIn(
            result.ErrorCode, -211, -244, -300, -301, -302, -303, -304, -305, -306, -307, -308, -309, -310, -311, -207);

    public async Task<BaseResponse> SendCard(SessionAccess access, CardSchema card, string clientIp, string? idempotencyKey)
    {
        var paymentSession = access.PaymentSession;
        if (paymentSession is null)
        {
            return access.Error ?? new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var started = await idempotency.Start($"{paymentSession.Id}:card", idempotencyKey, card.ModelDump());
        if (started.Response is not null)
        {
            return started.Response;
        }
        var error = await ValidateAccess(access, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.CREATED });
        if (error is not null)
        {
            await idempotency.Complete(started.CacheKey, error);
            return error;
        }
        var result = await SendCardChecked(paymentSession, card, clientIp);
        await idempotency.Complete(started.CacheKey, result);
        return result;
    }

    private async Task<BaseResponse> SendCardChecked(PaymentSession paymentSession, CardSchema card, string clientIp)
    {
        var now = clock.UtcNow();
        var expiry = Card.ExpiryToYymm(card.Expiry, now);
        if (!Card.LuhnValid(card.Pan) || expiry is null)
        {
            return new ErrorResponse(StatusCodes.CARD_INVALID);
        }
        if (!Card.UzcardPan(card.Pan))
        {
            return new ErrorResponse(StatusCodes.CARD_NOT_SUPPORTED);
        }
        var digest = Card.PanDigest(card.Pan, settings.PAN_HASH_PEPPER);
        var phoneHash = Defaults.Sha256Hex(paymentSession.Phone)[..32];
        if (await redisClient.Get($"otp_lock:{digest}") is not null
            || await redisClient.Get($"otp_lock:phone:{phoneHash}") is not null)
        {
            return new ErrorResponse(StatusCodes.CARD_TEMP_BLOCKED);
        }
        var wait = await rateLimit.CardWait(digest, clientIp, now);
        if (wait != 0)
        {
            return Error(StatusCodes.RATE_LIMITED, "retry_after", wait);
        }
        paymentSession.CardAttempts += 1;
        if (paymentSession.CardAttempts > 3)
        {
            paymentSession.Status = PaymentSessionStatus.FAILED;
            paymentSession.FailCode = StatusCodes.CARD_INVALID.Name;
            await sessionRepo.Save(paymentSession);
            return new ErrorResponse(StatusCodes.CARD_INVALID);
        }
        var cardAttempts = paymentSession.CardAttempts;
        var transitioned = await sessionRepo.SetStatus(
            paymentSession.Id,
            new HashSet<PaymentSessionStatus> { PaymentSessionStatus.CREATED },
            PaymentSessionStatus.OTP_SENDING,
            session => session.CardAttempts = cardAttempts);
        if (transitioned is null)
        {
            return new ErrorResponse(StatusCodes.INVALID_STATE);
        }
        paymentSession = transitioned;
        var gateway = await svgate.CardsNewOtp(card.Pan, expiry);
        await LogResult(paymentSession.Id, "cards.new.otp", gateway);
        if (!gateway.Ok)
        {
            paymentSession.Status = PaymentSessionStatus.CREATED;
            await sessionRepo.Save(paymentSession);
            return new ErrorResponse(CardError(gateway));
        }
        if (Py.IsTrue(gateway.Result.Get("verified")))
        {
            paymentSession.Status = PaymentSessionStatus.FAILED;
            paymentSession.FailCode = StatusCodes.STRONG_AUTH_REQUIRED.Name;
            await sessionRepo.Save(paymentSession);
            return new ErrorResponse(StatusCodes.STRONG_AUTH_REQUIRED);
        }
        var otpId = gateway.Result.Get("id");
        var phoneMask = gateway.Result.StrGet("phoneMask", "");
        if (otpId is null || phoneMask.Length == 0)
        {
            paymentSession.Status = PaymentSessionStatus.CREATED;
            await sessionRepo.Save(paymentSession);
            return new ErrorResponse(StatusCodes.PROCESSING_UNAVAILABLE);
        }
        paymentSession.Status = PaymentSessionStatus.OTP_SENT;
        paymentSession.Processing = ProcessingSystem.UZCARD.ToString();
        paymentSession.PanHash = digest;
        paymentSession.PanMasked = Card.MaskPan(card.Pan);
        paymentSession.OtpId = Py.Str(otpId);
        paymentSession.OtpSentAt = now;
        paymentSession.OtpAttempts = 0;
        paymentSession.OtpResends = 0;
        await sessionRepo.Save(paymentSession);
        await redisClient.SetValue($"otp:{paymentSession.Id}", Py.Str(otpId), settings.OTP_TTL_SECONDS);
        return new SuccessResponse(new OtpSentResponse(
            ProcessingSystem.UZCARD.ToString(), phoneMask, settings.OTP_TTL_SECONDS, settings.OTP_TTL_SECONDS, 3, 3));
    }

    public async Task<BaseResponse> Resend(SessionAccess access, CardSchema card, string clientIp, string? idempotencyKey)
    {
        var paymentSession = access.PaymentSession;
        if (paymentSession is null)
        {
            return access.Error ?? new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var started = await idempotency.Start($"{paymentSession.Id}:resend", idempotencyKey, card.ModelDump());
        if (started.Response is not null)
        {
            return started.Response;
        }
        var error = await ValidateAccess(access, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.OTP_SENT });
        if (error is not null)
        {
            await idempotency.Complete(started.CacheKey, error);
            return error;
        }
        var result = await ResendChecked(paymentSession, card, clientIp);
        await idempotency.Complete(started.CacheKey, result);
        return result;
    }

    private async Task<BaseResponse> ResendChecked(PaymentSession paymentSession, CardSchema card, string clientIp)
    {
        var digest = Card.PanDigest(card.Pan, settings.PAN_HASH_PEPPER);
        var expiry = Card.ExpiryToYymm(card.Expiry, clock.UtcNow());
        if (digest != paymentSession.PanHash || expiry is null || !Card.UzcardPan(card.Pan) || !Card.LuhnValid(card.Pan))
        {
            return new ErrorResponse(StatusCodes.CARD_INVALID);
        }
        var ttl = await redisClient.Ttl($"otp:{paymentSession.Id}");
        if (ttl > 0)
        {
            return Error(StatusCodes.OTP_RESEND_TOO_SOON, "retry_after", ttl);
        }
        if (paymentSession.OtpResends >= 3)
        {
            paymentSession.Status = PaymentSessionStatus.FAILED;
            paymentSession.FailCode = StatusCodes.OTP_RESEND_LIMIT.Name;
            await sessionRepo.Save(paymentSession);
            return new ErrorResponse(StatusCodes.OTP_RESEND_LIMIT);
        }
        var wait = await rateLimit.CardWait(digest, clientIp, clock.UtcNow());
        if (wait != 0)
        {
            return Error(StatusCodes.RATE_LIMITED, "retry_after", wait);
        }
        var transitioned = await sessionRepo.SetStatus(
            paymentSession.Id,
            new HashSet<PaymentSessionStatus> { PaymentSessionStatus.OTP_SENT },
            PaymentSessionStatus.OTP_SENDING);
        if (transitioned is null)
        {
            return new ErrorResponse(StatusCodes.INVALID_STATE);
        }
        paymentSession = transitioned;
        var gateway = await svgate.CardsNewOtp(card.Pan, expiry);
        await LogResult(paymentSession.Id, "cards.new.otp", gateway);
        if (!gateway.Ok || gateway.Result.Get("id") is null)
        {
            paymentSession.Status = PaymentSessionStatus.OTP_SENT;
            await sessionRepo.Save(paymentSession);
            return new ErrorResponse(CardError(gateway));
        }
        paymentSession.Status = PaymentSessionStatus.OTP_SENT;
        paymentSession.OtpId = Py.Str(gateway.Result.Get("id"));
        paymentSession.OtpSentAt = clock.UtcNow();
        paymentSession.OtpResends += 1;
        await sessionRepo.Save(paymentSession);
        await redisClient.SetValue($"otp:{paymentSession.Id}", paymentSession.OtpId!, settings.OTP_TTL_SECONDS);
        return new SuccessResponse(new OtpSentResponse(
            ProcessingSystem.UZCARD.ToString(),
            gateway.Result.StrGet("phoneMask", ""),
            settings.OTP_TTL_SECONDS,
            settings.OTP_TTL_SECONDS,
            3 - paymentSession.OtpAttempts,
            3 - paymentSession.OtpResends));
    }

    public async Task<BaseResponse> Verify(SessionAccess access, VerifySchema request, string? idempotencyKey)
    {
        var paymentSession = access.PaymentSession;
        if (paymentSession is null)
        {
            return access.Error ?? new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var started = await idempotency.Start($"{paymentSession.Id}:verify", idempotencyKey, request.ModelDump());
        if (started.Response is not null)
        {
            return started.Response;
        }
        var error = await ValidateAccess(access, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.OTP_SENT });
        if (error is not null)
        {
            await idempotency.Complete(started.CacheKey, error);
            return error;
        }
        var result = await VerifyChecked(paymentSession, request);
        await idempotency.Complete(started.CacheKey, result);
        return result;
    }

    private async Task<BaseResponse> VerifyChecked(PaymentSession paymentSession, VerifySchema request)
    {
        if (!request.PaymentConfirmed)
        {
            return new ErrorResponse(StatusCodes.INVALID_REQUEST);
        }
        var otpId = await redisClient.Get($"otp:{paymentSession.Id}");
        if (otpId is null || otpId != paymentSession.OtpId)
        {
            return new ErrorResponse(StatusCodes.OTP_EXPIRED);
        }
        var transitioned = await sessionRepo.SetStatus(
            paymentSession.Id,
            new HashSet<PaymentSessionStatus> { PaymentSessionStatus.OTP_SENT },
            PaymentSessionStatus.OTP_VERIFYING);
        if (transitioned is null)
        {
            return new ErrorResponse(StatusCodes.INVALID_STATE);
        }
        paymentSession = transitioned;
        var gateway = await svgate.CardsNewVerify(paymentSession.OtpId!, request.Code.ToUpperInvariant());
        await LogResult(paymentSession.Id, "cards.new.verify", gateway);
        if (!gateway.Ok)
        {
            ErrorResponse result;
            if (Py.CodeIn(gateway.ErrorCode, -269))
            {
                paymentSession.OtpAttempts += 1;
                var attemptsLeft = Math.Max(0, 3 - paymentSession.OtpAttempts);
                if (attemptsLeft == 0)
                {
                    paymentSession.Status = PaymentSessionStatus.FAILED;
                    paymentSession.FailCode = StatusCodes.OTP_LOCKED.Name;
                    await sessionRepo.Save(paymentSession);
                    await redisClient.Delete($"otp:{paymentSession.Id}");
                    if (paymentSession.PanHash is not null)
                    {
                        await redisClient.SetValue($"otp_lock:{paymentSession.PanHash}", "1", settings.OTP_LOCK_SECONDS);
                    }
                    var phoneHash = Defaults.Sha256Hex(paymentSession.Phone)[..32];
                    await redisClient.SetValue($"otp_lock:phone:{phoneHash}", "1", settings.OTP_LOCK_SECONDS);
                    result = new ErrorResponse(StatusCodes.OTP_LOCKED);
                }
                else
                {
                    paymentSession.Status = PaymentSessionStatus.OTP_SENT;
                    await sessionRepo.Save(paymentSession);
                    result = Error(StatusCodes.OTP_WRONG, "attempts_left", attemptsLeft);
                }
            }
            else if (Py.CodeIn(gateway.ErrorCode, -270, -294, -314))
            {
                paymentSession.Status = PaymentSessionStatus.OTP_SENT;
                await sessionRepo.Save(paymentSession);
                await redisClient.Delete($"otp:{paymentSession.Id}");
                result = new ErrorResponse(StatusCodes.OTP_EXPIRED);
            }
            else if (IsPending(gateway))
            {
                paymentSession.Status = PaymentSessionStatus.OTP_SENT;
                await sessionRepo.Save(paymentSession);
                result = new ErrorResponse(StatusCodes.PROCESSING_UNAVAILABLE);
            }
            else
            {
                paymentSession.Status = PaymentSessionStatus.OTP_SENT;
                await sessionRepo.Save(paymentSession);
                result = new ErrorResponse(PaymentError(gateway));
            }
            return result;
        }
        var cardToken = gateway.Result.Get("id");
        if (cardToken is null)
        {
            return new ErrorResponse(StatusCodes.PROCESSING_UNAVAILABLE);
        }
        var balance = gateway.Result.Has("balance") ? Py.Int(gateway.Result.Get("balance")) ?? 0 : 0;
        if (balance > 0 && balance < paymentSession.Total)
        {
            paymentSession.Status = PaymentSessionStatus.CREATED;
            paymentSession.SvCardToken = Py.Str(cardToken);
            paymentSession.OtpId = null;
            await sessionRepo.Save(paymentSession);
            await RemoveCardToken(paymentSession);
            await redisClient.Delete($"otp:{paymentSession.Id}");
            return Error(StatusCodes.INSUFFICIENT_FUNDS, "balance", balance);
        }
        paymentSession.Status = PaymentSessionStatus.CARD_VERIFIED;
        paymentSession.SvCardToken = Py.Str(cardToken);
        var pan = gateway.Result.Get("pan");
        paymentSession.PanMasked = Py.Truthy(pan) ? Py.Str(pan) : paymentSession.PanMasked ?? "None";
        var fullName = gateway.Result.Get("fullName");
        paymentSession.CardHolder = fullName is null ? null : Py.Str(fullName);
        await sessionRepo.Save(paymentSession);
        await redisClient.Delete($"otp:{paymentSession.Id}");
        return await Pay(paymentSession);
    }

    public async Task<BaseResponse> Pay(PaymentSession paymentSession)
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            SessionId = paymentSession.Id,
            Amount = paymentSession.Amount,
            Commission = paymentSession.Commission,
            Status = PaymentStatus.PAYING,
        };
        var transitioned = await paymentRepo.BeginUzcardPay(paymentSession.Id, payment);
        if (transitioned is null)
        {
            return new ErrorResponse(StatusCodes.INVALID_STATE);
        }
        var ready = await sessionRepo.SetStatus(
            transitioned.Id, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.PAYING }, PaymentSessionStatus.PAYING);
        if (ready is null)
        {
            return Processing("PROCESSING", 2);
        }
        var gateway = await svgate.TransPayPurpose(
            payment.Id.ToString(), transitioned.Phone, transitioned.Amount, transitioned.Commission, transitioned.SvCardToken!);
        await LogResult(transitioned.Id, "trans.pay.purpose", gateway);
        await RemoveCardToken(transitioned);
        if (IsPending(gateway))
        {
            await paymentRepo.TransitionPaying(
                transitioned.Id,
                PaymentSessionStatus.PENDING_CHECK,
                PaymentStatus.PENDING_CHECK,
                paymentValues: item => item.NextCheckAt = clock.UtcNow() + TimeSpan.FromSeconds(5));
            return Processing("PROCESSING", 2);
        }
        var responseCode = gateway.Result.Get("resp");
        var successful = gateway.Ok && Py.StrEquals(gateway.Result.Get("status"), "OK") && Py.Str(responseCode) is "0" or "00";
        if (!successful)
        {
            var code = PaymentError(gateway);
            var failed = await paymentRepo.TransitionPaying(
                transitioned.Id,
                PaymentSessionStatus.FAILED,
                PaymentStatus.FAILED,
                code.Name,
                item =>
                {
                    item.SvResp = responseCode is null ? null : Py.Str(responseCode);
                    item.SvStatus = gateway.Result.StrGet("status", "");
                });
            return failed is null ? Processing("PROCESSING", 2) : new ErrorResponse(code);
        }
        ApplyGatewayPayment(payment, gateway.Result);
        if (string.IsNullOrEmpty(payment.SvRefNum))
        {
            var manual = await paymentRepo.TransitionPaying(transitioned.Id, PaymentSessionStatus.NEEDS_MANUAL, PaymentStatus.NEEDS_MANUAL);
            return manual is null ? Processing("PROCESSING", 2) : Processing("NEEDS_MANUAL");
        }
        var (refNum, stan, date12, resp, status) = (payment.SvRefNum, payment.SvStan, payment.SvDate12, payment.SvResp, payment.SvStatus);
        var finalized = await paymentRepo.TransitionPaying(
            transitioned.Id,
            PaymentSessionStatus.PAID,
            PaymentStatus.PAID,
            paymentValues: item =>
            {
                item.SvRefNum = refNum;
                item.SvStan = stan;
                item.SvDate12 = date12;
                item.SvResp = resp;
                item.SvStatus = status;
            });
        if (finalized is null)
        {
            return Processing("PROCESSING", 2);
        }
        return await Credit(finalized.Value.Session, finalized.Value.Payment);
    }

    public static void ApplyGatewayPayment(Payment payment, JsonObject result)
    {
        payment.SvRefNum = result.StrGet("refNum", "");
        payment.SvStan = result.StrGet("stan", "");
        payment.SvDate12 = result.StrGet("date12", "");
        payment.SvResp = result.StrGet("resp", "");
        payment.SvStatus = result.StrGet("status", "");
    }

    public static IReadOnlyList<JsonNode?> GatewayTransactions(JsonObject result) =>
        result.Get("transactions") is JsonArray rows ? [.. rows] : [result];

    public static bool SuccessfulReversal(JsonObject result, string refNum) =>
        GatewayTransactions(result).Any(row => row is JsonObject item
            && Py.StrEquals(item.Get("tranType"), "REVERSAL")
            && Py.StrEquals(item.Get("status"), "ROK")
            && Py.Str(item.Get("resp")) is "0" or "00"
            && Py.Str(item.Get("refNum")) == refNum);

    public async Task<BaseResponse> Credit(PaymentSession paymentSession, Payment payment)
    {
        var claimed = await sessionRepo.SetStatus(
            paymentSession.Id, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.PAID }, PaymentSessionStatus.CREDITING);
        if (claimed is null)
        {
            return Processing("PROCESSING", 2);
        }
        paymentSession = claimed;
        payment.Status = PaymentStatus.CREDITING;
        await paymentRepo.Save(payment);
        var operatorResult = await @operator.Credit(paymentSession.OrderId, paymentSession.Phone, paymentSession.Amount, payment.SvRefNum ?? "");
        if (operatorResult.Ok)
        {
            payment.OperatorTxId = operatorResult.TransactionId;
            payment.Status = PaymentStatus.SUCCESS;
            paymentSession.Status = PaymentSessionStatus.SUCCESS;
            var local = clock.UtcNow().ToOffset(Defaults.TASHKENT);
            var receipt = await receiptRepo.Create(new Receipt
            {
                PaymentId = payment.Id,
                Number = $"PAY-{local:yyyyMMdd}-{payment.Id.ToString()[..8].ToUpperInvariant()}",
                Data = new JsonObject
                {
                    ["date"] = Defaults.IsoFormat(clock.UtcNow().ToOffset(Defaults.TASHKENT)),
                    ["phone"] = Phone.MaskPhone(paymentSession.Phone),
                    ["amount"] = payment.Amount,
                    ["commission"] = payment.Commission,
                    ["total"] = payment.Amount + payment.Commission,
                    ["card"] = paymentSession.PanMasked,
                    ["processing"] = ProcessingSystem.UZCARD.ToString(),
                    ["reference"] = payment.SvRefNum,
                    ["status"] = "SUCCESS",
                },
            });
            await paymentRepo.Save(payment);
            await sessionRepo.Save(paymentSession);
            return Processing("SUCCESS", receipt: new Dictionary<string, object?> { ["id"] = receipt.Id.ToString(), ["number"] = receipt.Number });
        }
        if (operatorResult.Uncertain)
        {
            return await Manual(paymentSession, payment);
        }
        payment.Status = PaymentStatus.REVERSING;
        paymentSession.Status = PaymentSessionStatus.REVERSING;
        await paymentRepo.Save(payment);
        await sessionRepo.Save(paymentSession);
        if (string.IsNullOrEmpty(payment.SvRefNum))
        {
            return await Manual(paymentSession, payment);
        }
        var gateway = await svgate.TransReverse(payment.SvRefNum);
        await LogResult(paymentSession.Id, "trans.reverse", gateway);
        var reverseSuccessful = gateway.Ok && SuccessfulReversal(gateway.Result, payment.SvRefNum);
        if (reverseSuccessful)
        {
            var confirmed = await svgate.TransExt(payment.Id.ToString());
            await LogResult(paymentSession.Id, "trans.ext", confirmed);
            reverseSuccessful = confirmed.Ok && SuccessfulReversal(confirmed.Result, payment.SvRefNum);
        }
        if (reverseSuccessful)
        {
            payment.Status = PaymentStatus.REVERSED;
            paymentSession.Status = PaymentSessionStatus.REVERSED;
            await paymentRepo.Save(payment);
            await sessionRepo.Save(paymentSession);
            return Processing("REVERSED");
        }
        return await Manual(paymentSession, payment);
    }

    private async Task<BaseResponse> Manual(PaymentSession paymentSession, Payment payment)
    {
        payment.Status = PaymentStatus.NEEDS_MANUAL;
        paymentSession.Status = PaymentSessionStatus.NEEDS_MANUAL;
        await paymentRepo.Save(payment);
        await sessionRepo.Save(paymentSession);
        return Processing("NEEDS_MANUAL");
    }

    public async Task<BaseResponse?> Recover(PaymentSession paymentSession, Payment payment)
    {
        if (payment.NextCheckAt is not null && clock.UtcNow() < payment.NextCheckAt)
        {
            return null;
        }
        var gateway = await svgate.TransExt(payment.Id.ToString());
        await LogResult(paymentSession.Id, "trans.ext", gateway);
        var rows = GatewayTransactions(gateway.Result);
        if (gateway.Ok && rows.Any(row => row is JsonObject item
                && Py.StrEquals(item.Get("tranType"), "REVERSAL")
                && Py.StrEquals(item.Get("status"), "ROK")
                && Py.Str(item.Get("resp")) is "0" or "00"
                && Py.Truthy(item.Get("refNum"))
                && (string.IsNullOrEmpty(payment.SvRefNum) || Py.Str(item.Get("refNum")) == payment.SvRefNum)))
        {
            var reversed = await sessionRepo.SetStatus(
                paymentSession.Id, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.PENDING_CHECK }, PaymentSessionStatus.REVERSED);
            if (reversed is null)
            {
                return null;
            }
            payment.Status = PaymentStatus.REVERSED;
            await paymentRepo.Save(payment);
            return Processing("REVERSED");
        }
        var debit = rows.OfType<JsonObject>().FirstOrDefault(row =>
            (row.Get("tranType") is null || Py.StrEquals(row.Get("tranType"), "DEBIT"))
            && Py.StrEquals(row.Get("status"), "OK")
            && Py.Str(row.Get("resp")) is "0" or "00");
        if (gateway.Ok && debit is not null)
        {
            var claimed = await sessionRepo.SetStatus(
                paymentSession.Id, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.PENDING_CHECK }, PaymentSessionStatus.PAID);
            if (claimed is null)
            {
                return null;
            }
            paymentSession = claimed;
            ApplyGatewayPayment(payment, debit);
            if (string.IsNullOrEmpty(payment.SvRefNum))
            {
                return await Manual(paymentSession, payment);
            }
            payment.Status = PaymentStatus.PAID;
            await paymentRepo.Save(payment);
            return await Credit(paymentSession, payment);
        }
        var failed = await paymentRepo.RecordRecoveryFailure(paymentSession.Id, clock.UtcNow());
        return failed == true ? Processing("NEEDS_MANUAL") : null;
    }

    public async Task<BaseResponse> Status(SessionAccess access, string clientIp)
    {
        if (access.PaymentSession is not null)
        {
            var wait = await rateLimit.StatusWait("uzcard", access.PaymentSession.Id.ToString(), clientIp, clock.UtcNow());
            if (wait != 0)
            {
                return Error(StatusCodes.RATE_LIMITED, "retry_after", wait);
            }
        }
        var error = await ValidateAccess(access);
        if (error is not null && error.Code != StatusCodes.SESSION_EXPIRED.Name)
        {
            return error;
        }
        var paymentSession = access.PaymentSession;
        if (paymentSession is null)
        {
            return new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var payment = await paymentRepo.GetBySessionId(paymentSession.Id);
        if (paymentSession.Status == PaymentSessionStatus.PENDING_CHECK && payment is not null)
        {
            var sessionId = paymentSession.Id;
            var recovered = await Recover(paymentSession, payment);
            if (recovered is not null)
            {
                return recovered;
            }
            paymentSession = (await sessionRepo.GetById(sessionId))!;
            payment = await paymentRepo.GetBySessionId(sessionId);
        }
        object? receiptData = null;
        if (payment is not null)
        {
            var receipt = await receiptRepo.GetByPaymentId(payment.Id);
            if (receipt is not null)
            {
                receiptData = new Dictionary<string, object?> { ["id"] = receipt.Id.ToString(), ["number"] = receipt.Number };
            }
        }
        return new SuccessResponse(new SessionStatusResponse(
            paymentSession.Status.ToString(),
            paymentSession.Processing,
            paymentSession.UpdatedAt,
            receiptData,
            paymentSession.FailCode is { Length: > 0 } failCode ? new Dictionary<string, object?> { ["code"] = failCode } : null));
    }

    public async Task<BaseResponse> Expire(SessionAccess access, string? idempotencyKey)
    {
        var paymentSession = access.PaymentSession;
        if (paymentSession is null)
        {
            return access.Error ?? new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED);
        }
        var started = await idempotency.Start($"{paymentSession.Id}:expire", idempotencyKey);
        if (started.Response is not null)
        {
            return started.Response;
        }
        if (access.Error is not null)
        {
            await idempotency.Complete(started.CacheKey, access.Error);
            return access.Error;
        }
        BaseResponse result;
        if (!Fresh.Contains(paymentSession.Status))
        {
            result = Error(StatusCodes.INVALID_STATE, "status", paymentSession.Status.ToString());
        }
        else
        {
            var transitioned = await sessionRepo.SetStatus(paymentSession.Id, Fresh, PaymentSessionStatus.EXPIRED);
            if (transitioned is null)
            {
                result = new ErrorResponse(StatusCodes.INVALID_STATE);
            }
            else
            {
                await RemoveCardToken(transitioned);
                await redisClient.Delete($"otp:{paymentSession.Id}");
                result = Processing("EXPIRED");
            }
        }
        await idempotency.Complete(started.CacheKey, result);
        return result;
    }
}
