using PaymentService.Core.Dependencies;
using PaymentService.Domain.Schemas;

namespace PaymentService.Infrastructure.Services;

public interface IPaymentFlow
{
    Task<BaseResponse> SendCard(SessionAccess access, CardSchema card, string clientIp, string? idempotencyKey);

    Task<BaseResponse> Resend(SessionAccess access, CardSchema card, string clientIp, string? idempotencyKey);

    Task<BaseResponse> Verify(SessionAccess access, VerifySchema request, string? idempotencyKey);

    Task<BaseResponse> Status(SessionAccess access, string clientIp);

    Task<BaseResponse> Expire(SessionAccess access, string? idempotencyKey);
}
