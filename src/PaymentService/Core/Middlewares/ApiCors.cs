using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;

namespace PaymentService.Core.Middlewares;

public sealed class ApiCorsMiddleware
{
    private static readonly string[] AllowMethods = ["GET", "POST", "OPTIONS"];
    private static readonly string[] SafelistedHeaders = ["Accept", "Accept-Language", "Content-Language", "Content-Type"];

    private readonly RequestDelegate next;
    private readonly HashSet<string> exactOrigins;
    private readonly bool allowAllOrigins;
    private readonly Regex? originPattern;
    private readonly HashSet<string> allowHeaders;
    private readonly Dictionary<string, string> preflightHeaders;

    public ApiCorsMiddleware(RequestDelegate next, IOptions<Settings> options)
    {
        this.next = next;
        var origins = SecurityOrigins.Parse(options.Value.ALLOWED_ORIGINS, options.Value.ENVIRONMENT).Origins;
        allowAllOrigins = origins.Contains("*");
        exactOrigins = origins.Where(origin => !origin.Contains("*.", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var wildcard = origins
            .Where(origin => origin.Contains("*.", StringComparison.Ordinal))
            .Select(origin => Regex.Escape(origin).Replace(@"\*\.", @"[A-Za-z0-9-]+\.", StringComparison.Ordinal))
            .ToList();
        originPattern = wildcard.Count > 0 ? new Regex(@"\A(?:" + string.Join("|", wildcard) + @")\z") : null;
        var headers = SafelistedHeaders.Concat(["Authorization", "Content-Type", "Idempotency-Key"])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        allowHeaders = headers.Select(header => header.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        preflightHeaders = new Dictionary<string, string>
        {
            [allowAllOrigins ? "Access-Control-Allow-Origin" : "Vary"] = allowAllOrigins ? "*" : "Origin",
            ["Access-Control-Allow-Methods"] = string.Join(", ", AllowMethods),
            ["Access-Control-Max-Age"] = "600",
            ["Access-Control-Allow-Headers"] = string.Join(", ", headers),
        };
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var origin = context.Request.Headers.Origin;
        if (!path.StartsWith("/api/", StringComparison.Ordinal) || origin.Count == 0)
        {
            await next(context);
            return;
        }
        var requestOrigin = origin[0]!;
        if (HttpMethods.IsOptions(context.Request.Method) && context.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            await Preflight(context, requestOrigin);
            return;
        }
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["Access-Control-Expose-Headers"] = "Retry-After";
            if (allowAllOrigins)
            {
                headers.AccessControlAllowOrigin = "*";
            }
            else if (IsAllowedOrigin(requestOrigin))
            {
                headers.AccessControlAllowOrigin = requestOrigin;
                var vary = headers.Vary;
                headers.Vary = vary.Count > 0 ? string.Join(", ", vary.ToString(), "Origin") : "Origin";
            }
            return Task.CompletedTask;
        });
        await next(context);
    }

    private bool IsAllowedOrigin(string origin) =>
        allowAllOrigins || (originPattern is not null && originPattern.IsMatch(origin)) || exactOrigins.Contains(origin);

    private async Task Preflight(HttpContext context, string origin)
    {
        var requestedMethod = context.Request.Headers.AccessControlRequestMethod.ToString();
        var requestedHeaders = context.Request.Headers.AccessControlRequestHeaders;
        var failures = new List<string>();
        var response = context.Response;
        foreach (var (name, value) in preflightHeaders)
        {
            response.Headers[name] = value;
        }
        if (!IsAllowedOrigin(origin))
        {
            failures.Add("origin");
        }
        else if (!allowAllOrigins)
        {
            response.Headers.AccessControlAllowOrigin = origin;
        }
        if (!AllowMethods.Contains(requestedMethod, StringComparer.Ordinal))
        {
            failures.Add("method");
        }
        if (requestedHeaders.Count > 0
            && requestedHeaders.ToString().Split(',').Any(header => !allowHeaders.Contains(header.ToLowerInvariant().Trim())))
        {
            failures.Add("headers");
        }
        if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
        {
            failures.Add("private-network");
        }
        var body = Encoding.UTF8.GetBytes(failures.Count > 0 ? "Disallowed CORS " + string.Join(", ", failures) : "OK");
        response.StatusCode = failures.Count > 0 ? 400 : 200;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, context.RequestAborted);
    }
}
