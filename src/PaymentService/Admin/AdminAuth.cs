using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;

namespace PaymentService.Admin;

public sealed class AdminAuth(IOptions<Settings> options, TimeProvider clock)
{
    public const string SessionCookie = "multipay_admin_session";
    public const string CsrfCookie = "multipay_admin_csrf";

    private Settings settings => options.Value;

    public bool CredentialsMatch(string username, string password) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(username), Encoding.UTF8.GetBytes(settings.ADMIN_USERNAME))
        & CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(settings.ADMIN_PASSWORD));

    public string? Authenticated(HttpRequest request)
    {
        var value = request.Cookies[SessionCookie];
        var parts = value?.Split('.');
        if (parts is not { Length: 3 } || !Signature($"{parts[0]}.{parts[1]}").Equals(parts[2], StringComparison.Ordinal))
        {
            return null;
        }
        string username;
        try
        {
            username = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[0]));
        }
        catch (FormatException)
        {
            return null;
        }
        return long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            && expires > clock.GetUtcNow().ToUnixTimeSeconds()
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(username), Encoding.UTF8.GetBytes(settings.ADMIN_USERNAME))
            ? username
            : null;
    }

    public void SignIn(HttpResponse response)
    {
        var expires = clock.GetUtcNow().AddHours(1);
        var payload = $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(settings.ADMIN_USERNAME))}.{expires.ToUnixTimeSeconds()}";
        response.Cookies.Append(SessionCookie, $"{payload}.{Signature(payload)}", Cookie(expires));
        response.Cookies.Delete(CsrfCookie, Cookie(null));
    }

    public void SignOut(HttpResponse response) => response.Cookies.Delete(SessionCookie, Cookie(null));

    public string IssueCsrf(HttpContext context)
    {
        var nonce = context.Request.Cookies[CsrfCookie];
        if (string.IsNullOrEmpty(nonce))
        {
            nonce = Infrastructure.Utils.Defaults.TokenUrlSafe(32);
            context.Response.Cookies.Append(CsrfCookie, nonce, Cookie(null));
        }
        return Signature($"csrf.{nonce}");
    }

    public bool CsrfValid(HttpRequest request, string? token)
    {
        var nonce = request.Cookies[CsrfCookie];
        return !string.IsNullOrEmpty(nonce) && token is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Signature($"csrf.{nonce}")), Encoding.ASCII.GetBytes(token));
    }

    private string Signature(string payload) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.ADMIN_SECRET_KEY), Encoding.UTF8.GetBytes(payload)));

    private CookieOptions Cookie(DateTimeOffset? expires) => new()
    {
        Path = "/admin",
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = settings.ADMIN_COOKIE_SECURE,
        Expires = expires,
        MaxAge = expires is null ? null : TimeSpan.FromHours(1),
    };
}

public sealed class AdminAuthFilter(AdminAuth auth) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (context.HandlerInstance is Pages.LoginModel || auth.Authenticated(context.HttpContext.Request) is not null)
        {
            return next();
        }
        context.Result = new SeeOther("/admin/login");
        return Task.CompletedTask;
    }
}

public sealed class SeeOther(string location) : IActionResult
{
    public Task ExecuteResultAsync(ActionContext context)
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.HttpContext.Response.Headers.Location = location;
        return Task.CompletedTask;
    }
}
