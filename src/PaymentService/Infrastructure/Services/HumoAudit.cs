using System.Diagnostics;
using System.Text.Json.Nodes;
using PaymentService.Domain.Models;
using PaymentService.Infrastructure.ExternalServices.Humo;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Utils;

namespace PaymentService.Infrastructure.Services;

public static class HumoAudit
{
    public static async Task<T> RecordHumoCall<T>(
        HumoLogRepository logs,
        Guid sessionId,
        string system,
        string method,
        JsonObject requestMasked,
        Func<Task<T>> operation) where T : IHumoResult
    {
        var started = Stopwatch.StartNew();
        var result = await operation();
        var response = new JsonObject { ["ok"] = result.Ok, ["transport_error"] = result.TransportError };
        foreach (var key in new[] { "status", "action", "auth_action_code", "auth_action_code_final" })
        {
            if (result.Result.Has(key))
            {
                var text = Py.Str(result.Result.Get(key));
                response[key] = text.Length > 32 ? text[..32] : text;
            }
        }
        var errorCode = result.ErrorCode is null || !Py.Truthy(result.ErrorCode) ? "" : Py.Str(result.ErrorCode);
        await logs.Create(new HumoLog
        {
            SessionId = sessionId,
            System = system,
            Method = method,
            RequestMasked = requestMasked,
            ResponseMasked = response,
            DurationMs = (int)started.ElapsedMilliseconds,
            ErrorCode = errorCode.Length == 0 ? null : errorCode.Length > 32 ? errorCode[..32] : errorCode,
        });
        return result;
    }
}
