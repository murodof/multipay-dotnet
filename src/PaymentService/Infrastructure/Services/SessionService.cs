using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Models;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.ExternalServices;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Utils;
using StatusCodes = PaymentService.Domain.Enums.StatusCodes;

namespace PaymentService.Infrastructure.Services;

public class SessionService(
    PaymentSessionRepository sessionRepo,
    RateLimitService rateLimit,
    RedisClient redisClient,
    TimeProvider clock,
    IOptions<Settings> options)
{
    private Settings settings => options.Value;

    public static string Hash(string token) => Defaults.Sha256Hex(token);

    public virtual async Task<BaseResponse> Create(
        SessionCreateSchema req,
        string clientIp,
        ProcessingSystem processing,
        string? userAgent = null)
    {
        var now = clock.UtcNow();

        var wait = await rateLimit.IpWait(clientIp, now);
        if (wait != 0)
        {
            return new ErrorResponse(StatusCodes.RATE_LIMITED, details: new Dictionary<string, object?> { ["retry_after"] = wait });
        }

        var verified = OperatorHash.VerifyHash(req.Hash, now, SigningKeys.Parse(settings.SECRET_KEYS), sessionRepo.OrderIdLength);
        if (verified is ErrorResponse error)
        {
            return error;
        }
        var payload = (HashPayload)verified;

        if (!OperatorHash.IsFresh(payload, now, settings.HASH_TS_WINDOW_SECONDS))
        {
            return new ErrorResponse(StatusCodes.HASH_EXPIRED);
        }

        wait = await rateLimit.PhoneWait(payload.Phone, now);
        if (wait != 0)
        {
            return new ErrorResponse(StatusCodes.RATE_LIMITED, details: new Dictionary<string, object?> { ["retry_after"] = wait });
        }

        if (!await redisClient.SetNx($"nonce:{payload.Nonce}", settings.NONCE_TTL_SECONDS))
        {
            return new ErrorResponse(StatusCodes.NONCE_USED);
        }

        if (await sessionRepo.GetByOrderId(payload.OrderId) is not null)
        {
            return new ErrorResponse(StatusCodes.ORDER_EXISTS);
        }

        if (!Phone.IsValidPhone(payload.Phone))
        {
            return new ErrorResponse(StatusCodes.PHONE_INVALID);
        }

        if (!(settings.MIN_AMOUNT <= payload.Amount && payload.Amount <= settings.MAX_AMOUNT))
        {
            return new ErrorResponse(
                StatusCodes.AMOUNT_OUT_OF_RANGE,
                details: new Dictionary<string, object?> { ["min_amount"] = settings.MIN_AMOUNT, ["max_amount"] = settings.MAX_AMOUNT });
        }

        var token = Defaults.TokenUrlSafe(16);
        var commission = Money.CommissionFor(payload.Amount, settings.COMMISSION_PERCENT);
        var paymentSession = await sessionRepo.Create(new PaymentSession
        {
            SessionTokenHash = Hash(token),
            OrderId = payload.OrderId,
            Phone = payload.Phone,
            Amount = payload.Amount,
            Commission = commission,
            Total = payload.Amount + commission,
            Processing = processing.ToString(),
            ExpiresAt = now + TimeSpan.FromSeconds(settings.SESSION_TTL_SECONDS),
            Ip = clientIp,
            UserAgent = userAgent,
            LastActivityAt = now,
        });
        if (paymentSession is null)
        {
            return new ErrorResponse(StatusCodes.ORDER_EXISTS);
        }

        return new SuccessResponse(new SessionCreatedResponse(
            paymentSession.Id,
            token,
            Phone.MaskPhone(paymentSession.Phone),
            paymentSession.Amount,
            paymentSession.Commission,
            paymentSession.Total,
            paymentSession.ExpiresAt,
            settings.SESSION_IDLE_TIMEOUT_SECONDS));
    }
}
