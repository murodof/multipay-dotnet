using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Models;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Utils;
using StatusCodes = PaymentService.Domain.Enums.StatusCodes;

namespace PaymentService.Core.Dependencies;

public sealed record SessionAccess(PaymentSession? PaymentSession, ErrorResponse? Error);

[AttributeUsage(AttributeTargets.Class)]
public sealed class ProviderAttribute(ProcessingSystem provider) : Attribute
{
    public ProcessingSystem Provider { get; } = provider;
}

public class SessionGuard(PaymentSessionRepository sessionRepo)
{
    public virtual async Task<SessionAccess> Inject(Guid sessionId, string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return new SessionAccess(null, new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED));
        }
        var token = authorization[7..];
        if (token.Length == 0)
        {
            return new SessionAccess(null, new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED));
        }
        var paymentSession = await sessionRepo.GetByIdAndTokenHash(sessionId, Defaults.Sha256Hex(token));
        if (paymentSession is null)
        {
            return new SessionAccess(null, new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED));
        }
        return new SessionAccess(paymentSession, null);
    }

    public static SessionAccess ProviderAccess(SessionAccess access, ProcessingSystem provider)
    {
        if (access.PaymentSession is not null && access.PaymentSession.Processing != provider.ToString())
        {
            return new SessionAccess(null, new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED));
        }
        return access;
    }
}

public sealed class SessionAccessBinder : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var context = bindingContext.HttpContext;
        var provider = (bindingContext.ActionContext.ActionDescriptor as ControllerActionDescriptor)?
            .ControllerTypeInfo.GetCustomAttribute<ProviderAttribute>()?.Provider;
        if (provider is null
            || !Guid.TryParse(bindingContext.ValueProvider.GetValue("session_id").FirstValue, out var sessionId))
        {
            bindingContext.Result = ModelBindingResult.Success(
                new SessionAccess(null, new ErrorResponse(StatusCodes.SESSION_UNAUTHORIZED)));
            return;
        }
        var authorization = context.Request.Headers.Authorization.Count > 0 ? context.Request.Headers.Authorization[0] : null;
        var access = await context.RequestServices.GetRequiredService<SessionGuard>().Inject(sessionId, authorization);
        bindingContext.Result = ModelBindingResult.Success(SessionGuard.ProviderAccess(access, provider.Value));
    }
}
