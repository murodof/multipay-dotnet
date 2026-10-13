using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using PaymentService.Admin;
using PaymentService.Domain.Models;

namespace PaymentService.Tests;

[Collection("app")]
public sealed class TestAdmin : AppTest
{
    private const string Password = "test-admin-password-123";

    public TestAdmin(Containers containers) : base(containers)
    {
        App.Settings["ADMIN_ENABLED"] = "true";
        App.Settings["ADMIN_USERNAME"] = "admin";
        App.Settings["ADMIN_PASSWORD"] = Password;
        App.Settings["ADMIN_SECRET_KEY"] = "test-admin-secret-key-at-least-32-chars";
        App.Settings["ADMIN_COOKIE_SECURE"] = "false";
    }

    private HttpClient Browser() => App.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("http://localhost:8010"),
    });

    private static async Task<string> Csrf(HttpClient client)
    {
        var page = await client.GetAsync("/admin/login");
        return Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"csrftoken\" value=\"([^\"]+)\"").Groups[1].Value;
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string password, string csrf) =>
        client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = password,
            ["csrftoken"] = csrf,
        }));

    [Fact]
    public async Task Admin_requires_login_and_is_read_only()
    {
        var client = Browser();

        var response = await client.GetAsync("/admin/");
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.SeeOther, (await client.GetAsync("/admin/payment-session/list")).StatusCode);

        response = await client.GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("script-src 'self'", TestSecurityHeaders.Header(response, "content-security-policy")!, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", TestSecurityHeaders.Header(response, "content-security-policy")!, StringComparison.Ordinal);
        var csrf = await Csrf(client);

        response = await Login(client, "wrong", csrf);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        response = await Login(client, Password, "forged-csrf-token");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        response = await Login(client, Password, csrf);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var cookie = string.Join("\n", response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Contains("multipay_admin_session=", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/admin", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", cookie, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/payment-session/create")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/admin/payment-session/delete", null)).StatusCode);

        var fields = AdminViews.All.Single(view => view.Identity == "payment-session").Fields;
        Assert.DoesNotContain("session_token_hash", fields);
        Assert.DoesNotContain("sv_card_token", fields);
        Assert.DoesNotContain("phone", fields);
    }

    [Fact]
    public async Task Admin_displays_model_utc_dates_in_tashkent()
    {
        await using (var db = App.Db())
        {
            db.PaymentSessions.Add(new PaymentSession
            {
                SessionTokenHash = new string('a', 64),
                OrderId = "ADMIN-TZ-1",
                Phone = "998901234567",
                Amount = 5_000_000,
                Commission = 0,
                Total = 5_000_000,
                Processing = "UZCARD",
                SvCardToken = "card-token-secret",
                CreatedAt = new DateTimeOffset(2026, 9, 28, 17, 28, 50, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2026, 9, 28, 17, 28, 50, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2026, 9, 28, 17, 38, 50, TimeSpan.Zero),
            });
            await db.SaveChangesAsync();
        }
        var client = Browser();
        Assert.Equal(HttpStatusCode.SeeOther, (await Login(client, Password, await Csrf(client))).StatusCode);

        var response = await client.GetAsync("/admin/payment-session/list");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ADMIN-TZ-1", text, StringComparison.Ordinal);
        Assert.Contains("September 28, 2026 22:28:50", text, StringComparison.Ordinal);
        Assert.Contains("September 28, 2026 22:38:50", text, StringComparison.Ordinal);
        Assert.DoesNotContain("998901234567", text, StringComparison.Ordinal);
        Assert.DoesNotContain("card-token-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 64), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_admin_view_lists_without_error()
    {
        var client = Browser();
        await Login(client, Password, await Csrf(client));

        foreach (var view in AdminViews.All)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/admin/{view.Identity}/list?q=x&page=2")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/unknown/list")).StatusCode);
    }

    // The admin CSP allows styles from 'self' only, so the Tabler stylesheet is served by the app
    // itself (embedded in the assembly), and the login page needs it before anyone is signed in.
    [Fact]
    public async Task The_stylesheet_is_served_locally_and_without_login()
    {
        var response = await Browser().GetAsync("/admin/statics/tabler.min.css");
        var login = await (await Browser().GetAsync("/admin/login")).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("Tabler", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/statics/tabler.min.css\"", login, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", login, StringComparison.Ordinal);
    }

    // The overview counts rows and groups sessions by status; like the lists, it shows no phone.
    [Fact]
    public async Task The_overview_counts_without_exposing_customer_data()
    {
        await Conftest.CreateSession(Client, MakeHash());
        var client = Browser();
        await Login(client, Password, await Csrf(client));

        var text = await (await client.GetAsync("/admin/")).Content.ReadAsStringAsync();

        Assert.Contains("CREATED", text, StringComparison.Ordinal);
        Assert.Contains("/admin/payment-session/list", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Conftest.PHONE, text, StringComparison.Ordinal);
    }

    // A session cookie is an HMAC over the username and expiry; one that is forged or past its
    // hour must not open the panel.
    [Fact]
    public async Task A_forged_or_expired_admin_cookie_is_refused()
    {
        var client = Browser();
        await Login(client, Password, await Csrf(client));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/")).StatusCode);

        App.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.SeeOther, (await client.GetAsync("/admin/")).StatusCode);

        var forged = Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/");
        request.Headers.Add("Cookie", "multipay_admin_session=YWRtaW4.99999999999.AAAA");
        Assert.Equal(HttpStatusCode.SeeOther, (await forged.SendAsync(request)).StatusCode);
    }
}

[Collection("app")]
public sealed class TestAdminDisabled(Containers containers) : AppTest(containers)
{
    [Fact]
    public async Task The_admin_does_not_exist_unless_enabled()
    {
        foreach (var path in new[] { "/admin", "/admin/", "/admin/login", "/admin/payment-session/list", "/admin/statics/tabler.min.css" })
        {
            Assert.Equal(404, (int)(await Client.GetAsync(path)).StatusCode);
        }
    }
}
