using Microsoft.AspNetCore.Mvc;
using PaymentService.Core.Dependencies;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Services;

namespace PaymentService.Api.Controllers;

[ApiController]
public abstract class ProviderController(IPaymentFlow service) : ControllerBase
{
    [HttpPost("session/{session_id}/card")]
    public Task<BaseResponse> SendCard(
        [FromRoute(Name = "session_id")] Guid sessionId,
        [ModelBinder(typeof(JsonBodyBinder<CardSchema>))] CardSchema reqBody,
        [ModelBinder(typeof(SessionAccessBinder))] SessionAccess access,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey) =>
        access.Error is not null
            ? Task.FromResult<BaseResponse>(access.Error)
            : service.SendCard(access, reqBody, ClientIp.Of(HttpContext), idempotencyKey);

    [HttpPost("session/{session_id}/resend")]
    public Task<BaseResponse> ResendOtp(
        [FromRoute(Name = "session_id")] Guid sessionId,
        [ModelBinder(typeof(JsonBodyBinder<CardSchema>))] CardSchema reqBody,
        [ModelBinder(typeof(SessionAccessBinder))] SessionAccess access,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey) =>
        access.Error is not null
            ? Task.FromResult<BaseResponse>(access.Error)
            : service.Resend(access, reqBody, ClientIp.Of(HttpContext), idempotencyKey);

    [HttpPost("session/{session_id}/verify")]
    public Task<BaseResponse> VerifyOtp(
        [FromRoute(Name = "session_id")] Guid sessionId,
        [ModelBinder(typeof(JsonBodyBinder<VerifySchema>))] VerifySchema reqBody,
        [ModelBinder(typeof(SessionAccessBinder))] SessionAccess access,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey) =>
        access.Error is not null
            ? Task.FromResult<BaseResponse>(access.Error)
            : service.Verify(access, reqBody, idempotencyKey);

    [HttpPost("session/{session_id}/expire")]
    public Task<BaseResponse> ExpireSession(
        [FromRoute(Name = "session_id")] Guid sessionId,
        [ModelBinder(typeof(SessionAccessBinder))] SessionAccess access,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey) =>
        access.Error is not null
            ? Task.FromResult<BaseResponse>(access.Error)
            : service.Expire(access, idempotencyKey);

    [HttpGet("session/{session_id}")]
    public Task<BaseResponse> GetSessionStatus(
        [FromRoute(Name = "session_id")] Guid sessionId,
        [ModelBinder(typeof(SessionAccessBinder))] SessionAccess access) =>
        access.Error is not null
            ? Task.FromResult<BaseResponse>(access.Error)
            : service.Status(access, ClientIp.Of(HttpContext));
}
