using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Core.Dependencies;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Services;
using StatusCodes = PaymentService.Domain.Enums.StatusCodes;

namespace PaymentService.Api.Controllers;

[ApiController]
public sealed class SessionController(SessionService service, IOptions<Settings> options) : ControllerBase
{
    [HttpPost("api/uzcard/session")]
    public Task<BaseResponse> CreateUzcardSession(
        [ModelBinder(typeof(JsonBodyBinder<SessionCreateSchema>))] SessionCreateSchema reqBody) =>
        CreateSession(reqBody, ProcessingSystem.UZCARD);

    [HttpPost("api/humo/session")]
    public Task<BaseResponse> CreateHumoSession(
        [ModelBinder(typeof(JsonBodyBinder<SessionCreateSchema>))] SessionCreateSchema reqBody) =>
        options.Value.HUMO_ENABLED
            ? CreateSession(reqBody, ProcessingSystem.HUMO)
            : Task.FromResult<BaseResponse>(new ErrorResponse(StatusCodes.PROCESSING_UNAVAILABLE));

    private Task<BaseResponse> CreateSession(SessionCreateSchema reqBody, ProcessingSystem processing) =>
        service.Create(
            reqBody,
            ClientIp.Of(HttpContext),
            processing,
            Request.Headers.UserAgent.Count > 0 ? Request.Headers.UserAgent[0] : null);
}
