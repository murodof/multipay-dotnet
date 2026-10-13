using System.Globalization;

namespace PaymentService.Core.Middlewares;

public sealed class AccessLogMiddleware(RequestDelegate next, ILoggerFactory loggers)
{
    private readonly ILogger logger = loggers.CreateLogger("PaymentService.Access");

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        finally
        {
            var request = context.Request;
            var version = request.Protocol.StartsWith("HTTP/", StringComparison.Ordinal) ? request.Protocol[5..] : request.Protocol;
            var client = Dependencies.ClientIp.Of(context);
            var port = context.Connection.RemotePort.ToString(CultureInfo.InvariantCulture);
            logger.LogInformation(
                "{Client} - \"{Method} {Path} HTTP/{Version}\" {Status}",
                $"{client}:{port}",
                request.Method,
                request.Path.ToUriComponent() + request.QueryString.Value,
                version,
                context.Response.StatusCode);
        }
    }
}
