using Microsoft.AspNetCore.Routing.Patterns;

namespace PaymentService.Core.Middlewares;

public sealed class StarletteRoutingMiddleware(RequestDelegate next, EndpointDataSource endpoints)
{
    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path == "/admin" || path.StartsWith("/admin/", StringComparison.Ordinal) || Matches(path))
        {
            return next(context);
        }
        if (path != "/")
        {
            var toggled = path.EndsWith('/') ? path.TrimEnd('/') : path + "/";
            if (toggled.Length > 0 && Matches(toggled))
            {
                var request = context.Request;
                context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = Quote($"{request.Scheme}://{request.Host}{request.PathBase}{toggled}{request.QueryString}");
                context.Response.ContentLength = 0;
                return Task.CompletedTask;
            }
        }
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    private static string Quote(string url)
    {
        var quoted = new System.Text.StringBuilder();
        foreach (var octet in System.Text.Encoding.UTF8.GetBytes(url))
        {
            var character = (char)octet;
            if (char.IsAsciiLetterOrDigit(character) || "_.-~:/%#?=@[]!$&'()*+,;".Contains(character, StringComparison.Ordinal))
            {
                quoted.Append(character);
            }
            else
            {
                quoted.Append('%').Append(octet.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return quoted.ToString();
    }

    private bool Matches(string path)
    {
        var segments = path.Split('/')[1..];
        return endpoints.Endpoints.OfType<RouteEndpoint>().Any(endpoint => Matches(endpoint.RoutePattern, segments));
    }

    private static bool Matches(RoutePattern pattern, string[] segments)
    {
        var parts = pattern.PathSegments;
        if (parts.Count == 0)
        {
            return segments is [""];
        }
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Parts is [RoutePatternParameterPart { IsCatchAll: true }])
            {
                return true;
            }
            if (index >= segments.Length)
            {
                return part.Parts is [RoutePatternParameterPart { IsOptional: true }] && index == parts.Count - 1;
            }
            var segment = segments[index];
            var matched = part.Parts switch
            {
                [RoutePatternLiteralPart literal] => string.Equals(literal.Content, segment, StringComparison.Ordinal),
                [RoutePatternParameterPart parameter] => segment.Length > 0 || parameter.IsOptional,
                _ => segment.Length > 0,
            };
            if (!matched)
            {
                return false;
            }
        }
        return segments.Length == parts.Count;
    }
}
