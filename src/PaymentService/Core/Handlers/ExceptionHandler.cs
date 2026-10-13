using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Core.Dependencies;
using PaymentService.Domain.Enums;
using StatusCodes = PaymentService.Domain.Enums.StatusCodes;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Services;

namespace PaymentService.Core.Handlers;

public static class ExceptionHandler
{
    public static async Task ErrorResponse(HttpContext context, Domain.Schemas.ErrorResponse error, int? statusCode = null)
    {
        var response = context.Response;
        error.ApplyTo(response);
        if (statusCode is not null)
        {
            response.StatusCode = statusCode.Value;
        }
        var body = error.ToJsonBytes();
        response.ContentType = "application/json";
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, context.RequestAborted);
    }

    public static IActionResult ValidationExceptionHandler(ActionContext context)
    {
        var undecodable = context.ModelState.ContainsKey(JsonBodyBinder<SessionCreateSchema>.UndecodableBody);
        return new StatusOverrideResult(
            new Domain.Schemas.ErrorResponse(undecodable ? StatusCodes.UNKNOWN_ERROR : StatusCodes.INVALID_REQUEST),
            undecodable ? 400 : null);
    }

    public static Task HttpExceptionHandler(StatusCodeContext context)
    {
        var status = context.HttpContext.Response.StatusCode;
        var code = status switch
        {
            404 => StatusCodes.NOT_FOUND,
            405 => StatusCodes.METHOD_NOT_ALLOWED,
            _ => StatusCodes.UNKNOWN_ERROR,
        };
        return ErrorResponse(context.HttpContext, new Domain.Schemas.ErrorResponse(code), status);
    }

    private sealed class StatusOverrideResult(Domain.Schemas.ErrorResponse error, int? statusCode) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context) => ErrorResponse(context.HttpContext, error, statusCode);
    }
}

public sealed class UnhandledExceptionMiddleware(RequestDelegate next, ILogger<UnhandledExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            logger.LogError(exception, "Unhandled request error on {Method} {Path}", context.Request.Method, context.Request.Path.Value);
            try
            {
                await context.RequestServices.GetRequiredService<IdempotencyClaims>().AbortOpenClaims();
            }
            catch (Exception cleanup)
            {
                logger.LogError(cleanup, "Idempotency claim cleanup failed");
            }
            if (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            context.Response.Clear();
            await ExceptionHandler.ErrorResponse(context, new Domain.Schemas.ErrorResponse(StatusCodes.UNKNOWN_ERROR));
        }
    }
}
