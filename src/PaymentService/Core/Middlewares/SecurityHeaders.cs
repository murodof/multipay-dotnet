using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;

namespace PaymentService.Core.Middlewares;

public sealed record SecurityOrigins(IReadOnlyList<string> Origins, string? Error)
{
    public static SecurityOrigins Parse(string value, string environment)
    {
        var origins = value.Split(',').Select(origin => origin.Trim()).Where(origin => origin.Length > 0).ToList();
        foreach (var origin in origins)
        {
            var validHttps = Regex.IsMatch(origin, @"\Ahttps://(\*\.)?[A-Za-z0-9.-]+(:[0-9]{1,5})?\z");
            var validLocalhost = Regex.IsMatch(origin, @"\Ahttp://localhost(:[0-9]{1,5})?\z");
            if (origin != "*" && !validHttps && !validLocalhost)
            {
                return new SecurityOrigins([], $"ALLOWED_ORIGINS: '{origin}' is not an https://host[:port] origin");
            }
            if (environment == "production" && !validHttps)
            {
                return new SecurityOrigins([], $"ALLOWED_ORIGINS: '{origin}' is not allowed in production");
            }
        }
        return new SecurityOrigins(origins, null);
    }
}

public static class SecurityHeaders
{
    public static Dictionary<string, string> Default(Settings settings)
    {
        var origins = SecurityOrigins.Parse(settings.ALLOWED_ORIGINS, settings.ENVIRONMENT).Origins;
        var frameAncestors = origins.Count > 0 ? string.Join(" ", origins) : "'none'";
        return new Dictionary<string, string>
        {
            ["Content-Security-Policy"] = $"default-src 'none'; frame-ancestors {frameAncestors}; base-uri 'none'; form-action 'none'",
            ["Strict-Transport-Security"] = $"max-age={settings.HSTS_MAX_AGE_SECONDS}; includeSubDomains",
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
            ["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()",
            ["Cache-Control"] = "no-store",
            ["Pragma"] = "no-cache",
        };
    }

    public static Dictionary<string, string> Docs(Settings settings) => new(Default(settings))
    {
        ["Content-Security-Policy"] = "default-src 'none'; "
            + "script-src https://cdn.jsdelivr.net 'unsafe-inline'; "
            + "style-src https://cdn.jsdelivr.net 'unsafe-inline'; "
            + "img-src 'self' data: https://fastapi.tiangolo.com; "
            + "connect-src 'self'; "
            + "frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
    };

    public static Dictionary<string, string> Admin(Settings settings) => new(Default(settings))
    {
        ["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; "
            + "style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
            + "font-src 'self' data:; connect-src 'self'; "
            + "frame-ancestors 'none'; base-uri 'self'; form-action 'self'",
    };
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<Settings> options)
{
    private readonly Dictionary<string, string> headers = SecurityHeaders.Default(options.Value);
    private readonly Dictionary<string, string> docsHeaders = SecurityHeaders.Docs(options.Value);
    private readonly Dictionary<string, string> adminHeaders = SecurityHeaders.Admin(options.Value);
    private readonly string? docsPath = options.Value.DOCS_ENABLED ? "/api/docs" : null;
    private readonly string? adminPath = options.Value.ADMIN_ENABLED ? "/admin" : null;

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var onDocs = docsPath is not null && path.StartsWith(docsPath, StringComparison.Ordinal);
        var onAdmin = adminPath is not null && (path == adminPath || path.StartsWith(adminPath + "/", StringComparison.Ordinal));
        var selected = onDocs ? docsHeaders : onAdmin ? adminHeaders : headers;
        context.Response.OnStarting(() =>
        {
            foreach (var (name, value) in selected)
            {
                context.Response.Headers[name] = value;
            }
            return Task.CompletedTask;
        });
        return next(context);
    }
}
