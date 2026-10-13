using PaymentService.Core.Middlewares;
using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// Security headers on every response, and the CSP frame-ancestors list built from ALLOWED_ORIGINS.
[Collection("app")]
public sealed class TestSecurityHeaders(Containers containers) : AppTest(containers)
{
    private static readonly Dictionary<string, string> EXPECTED = new()
    {
        ["content-security-policy"] = "default-src 'none'; frame-ancestors https://operator.example.uz https://*.partner.uz; base-uri 'none'; form-action 'none'",
        ["strict-transport-security"] = "max-age=31536000; includeSubDomains",
        ["x-content-type-options"] = "nosniff",
        ["referrer-policy"] = "no-referrer",
        ["cache-control"] = "no-store",
    };

    public static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values)
        : response.Content.Headers.TryGetValues(name, out var content) ? string.Join(", ", content)
        : null;

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        foreach (var (name, value) in EXPECTED)
        {
            Assert.Equal(value, Header(response, name));
        }
        Assert.NotNull(Header(response, "permissions-policy"));
    }

    // `Cache-Control: no-store` matters most here: this body holds the session token.
    [Fact]
    public async Task A_created_session_carries_the_headers() => AssertSecurityHeaders(await CreateSession(Client, MakeHash()));

    [Fact]
    public async Task Error_responses_carry_them_too()
    {
        var malformed = new ByteArrayContent("{"u8.ToArray());
        malformed.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        var responses = new[]
        {
            await CreateSession(Client, "garbage"),
            await Client.GetAsync("/nope"),
            await Client.GetAsync("/api/uzcard/session"),
            await Client.PostAsync("/api/uzcard/session", malformed),
        };

        Assert.Equal([401, 404, 405, 422], responses.Select(response => (int)response.StatusCode));
        foreach (var response in responses)
        {
            AssertSecurityHeaders(response);
            Assert.False((await response.Json())["success"].IsTrue());
        }
        Assert.Equal("POST", Header(responses[2], "allow"));
    }

    // DOCS_ENABLED turns the schema on for local work.
    [Fact]
    public async Task Docs_are_not_published_by_default()
    {
        foreach (var path in new[] { "/api/docs", "/api/openapi.json", "/docs", "/openapi.json" })
        {
            Assert.Equal(404, (int)(await Client.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task Api_preflight_accepts_configured_origins()
    {
        foreach (var origin in new[] { "https://operator.example.uz", "https://shop.partner.uz" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/api/uzcard/session");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key");

            var response = await Client.SendAsync(request);

            Assert.Equal(200, (int)response.StatusCode);
            Assert.Equal(origin, Header(response, "access-control-allow-origin"));
            Assert.Equal("GET, POST, OPTIONS", Header(response, "access-control-allow-methods"));
            Assert.Equal("Accept, Accept-Language, Authorization, Content-Language, Content-Type, Idempotency-Key", Header(response, "access-control-allow-headers"));
            Assert.Equal("600", Header(response, "access-control-max-age"));
            Assert.Equal("OK", await response.Content.ReadAsStringAsync());
            AssertSecurityHeaders(response);
        }
    }

    [Fact]
    public async Task Api_preflight_rejects_unconfigured_origin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/uzcard/session");
        request.Headers.Add("Origin", "https://attacker.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await Client.SendAsync(request);

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Null(Header(response, "access-control-allow-origin"));
        Assert.Equal("Disallowed CORS origin", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_simple_api_request_from_an_allowed_origin_exposes_retry_after()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/uzcard/session") { Content = JsonContent.Create(new { hash = "garbage" }) };
        request.Headers.Add("Origin", "https://operator.example.uz");

        var response = await Client.SendAsync(request);

        Assert.Equal("https://operator.example.uz", Header(response, "access-control-allow-origin"));
        Assert.Equal("Retry-After", Header(response, "access-control-expose-headers"));
        Assert.Equal("Origin", Header(response, "vary"));
    }

    // Swagger UI is a CDN bundle started by an inline script; `default-src 'none'` left the page
    // blank. The looser policy is scoped to the docs page only and still forbids framing.
    [Fact]
    public async Task Swagger_page_gets_a_policy_that_lets_it_render()
    {
        App.Settings["DOCS_ENABLED"] = "true";

        var response = await Client.GetAsync("/api/docs");
        var csp = Header(response, "content-security-policy")!;

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Contains("script-src https://cdn.jsdelivr.net 'unsafe-inline'", csp, StringComparison.Ordinal);
        Assert.Contains("style-src https://cdn.jsdelivr.net 'unsafe-inline'", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", Header(response, "x-content-type-options"));
        Assert.Equal("no-store", Header(response, "cache-control"));
    }

    [Fact]
    public async Task The_schema_next_to_the_docs_keeps_the_strict_policy()
    {
        App.Settings["DOCS_ENABLED"] = "true";

        var response = await Client.GetAsync("/api/openapi.json");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(EXPECTED["content-security-policy"], Header(response, "content-security-policy"));
    }

    // With DOCS_ENABLED off there is no docs route, so the relaxed CSP must not exist either.
    [Fact]
    public async Task Without_docs_nothing_gets_the_looser_policy()
    {
        var response = await Client.GetAsync("/api/docs");

        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal(EXPECTED["content-security-policy"], Header(response, "content-security-policy"));
    }

    [Fact]
    public void No_origins_means_nobody_may_frame_the_page()
    {
        var settings = TestPanPepperSettings.SettingsFor(new() { ["ALLOWED_ORIGINS"] = "" });

        Assert.Contains("frame-ancestors 'none';", SecurityHeaders.Default(settings)["Content-Security-Policy"], StringComparison.Ordinal);
    }

    // The value is pasted into a header, so anything but a bare https origin could inject directives.
    [Theory]
    [InlineData("http://operator.example.uz")]
    [InlineData("https://operator.example.uz/checkout")]
    [InlineData("https://operator.example.uz; script-src *")]
    [InlineData("https://operator.example.uz 'unsafe-inline'")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("https://*")]
    public void An_origin_that_could_rewrite_the_policy_is_refused(string origin) =>
        Assert.NotNull(SecurityOrigins.Parse(origin, "development").Error);

    // `*` and http://localhost exist for local frontends; on a deployed server they would let any
    // site frame the checkout and call the API, so production must refuse to start with them.
    [Theory]
    [InlineData("*")]
    [InlineData("http://localhost:5173")]
    public void Production_refuses_origins_that_are_only_for_development(string origin)
    {
        Assert.NotNull(SecurityOrigins.Parse($"https://operator.example.uz,{origin}", "production").Error);
        Assert.Null(SecurityOrigins.Parse($"https://operator.example.uz,{origin}", "development").Error);
    }

    [Fact]
    public void Production_accepts_https_origins() =>
        Assert.Null(SecurityOrigins.Parse("https://operator.example.uz,https://*.partner.uz", "production").Error);

    private async Task<HttpResponseMessage> Preflight(string origin, params (string Name, string Value)[] extra)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/uzcard/session");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        foreach (var (name, value) in extra)
        {
            request.Headers.Add(name, value);
        }
        return await Client.SendAsync(request);
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://localhost:5174")]
    public async Task Cors_allows_local_frontend_origins(string origin)
    {
        App.Settings["ALLOWED_ORIGINS"] = "http://localhost:5173,http://localhost:5174,https://*.ngrok-free.app";

        var response = await Preflight(origin);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(origin, Header(response, "access-control-allow-origin"));
    }

    [Fact]
    public async Task Cors_allows_ngrok_subdomain_origin()
    {
        App.Settings["ALLOWED_ORIGINS"] = "https://*.ngrok-free.app";

        var response = await Preflight("https://frontend-demo.ngrok-free.app");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("https://frontend-demo.ngrok-free.app", Header(response, "access-control-allow-origin"));
    }

    // Starlette answers a configured `*` with a literal `*` and no `Vary: Origin`, on preflights and
    // on ordinary responses alike; the frame-ancestors list gets the same `*`.
    [Fact]
    public async Task Cors_allows_any_origin_when_wildcard_is_configured()
    {
        App.Settings["ALLOWED_ORIGINS"] = "*";

        var preflight = await Preflight("https://arbitrary.example");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/uzcard/session") { Content = JsonContent.Create(new { hash = "garbage" }) };
        request.Headers.Add("Origin", "https://arbitrary.example");
        var simple = await Client.SendAsync(request);

        Assert.Equal(200, (int)preflight.StatusCode);
        Assert.Equal("*", Header(preflight, "access-control-allow-origin"));
        Assert.Null(Header(preflight, "vary"));
        Assert.Equal("*", Header(simple, "access-control-allow-origin"));
        Assert.Null(Header(simple, "vary"));
        Assert.Equal("Retry-After", Header(simple, "access-control-expose-headers"));
        Assert.Contains("frame-ancestors *;", Header(simple, "content-security-policy"), StringComparison.Ordinal);
    }

    // Starlette refuses a preflight that asks for private-network access unless it was enabled.
    [Fact]
    public async Task Api_preflight_refuses_private_network_access()
    {
        var response = await Preflight("https://operator.example.uz", ("Access-Control-Request-Private-Network", "true"));

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal("Disallowed CORS private-network", await response.Content.ReadAsStringAsync());
        Assert.Null(Header(response, "access-control-allow-private-network"));
    }

    // Starlette's RedirectResponse percent-encodes the target; a decoded path pasted into `Location`
    // would be a different URL, and a non-ASCII one cannot be written into a header at all.
    [Theory]
    [InlineData("/api/uzcard/session/a%20b/", "https://pay.test/api/uzcard/session/a%20b")]
    [InlineData("/api/uzcard/session/%D1%84/?next=%2Fx&q=1", "https://pay.test/api/uzcard/session/%D1%84?next=%2Fx&q=1")]
    public async Task The_slash_redirect_keeps_the_target_percent_encoded(string path, string location)
    {
        var response = await Client.GetAsync(path);

        Assert.Equal(307, (int)response.StatusCode);
        Assert.Equal(location, response.Headers.TryGetValues("Location", out var values) ? values.Single() : null);
    }

    // Starlette redirects a trailing slash to the route without it, and routes are case-sensitive;
    // ASP.NET would otherwise match both silently.
    [Fact]
    public async Task Routing_follows_starlette()
    {
        var slash = await Client.GetAsync("/api/uzcard/session/");
        var upper = await CreateSessionAt("/API/UZCARD/SESSION");

        Assert.Equal(307, (int)slash.StatusCode);
        Assert.Equal("https://pay.test/api/uzcard/session", Header(slash, "location"));
        Assert.Equal(404, (int)upper.StatusCode);
        Assert.Equal("NOT_FOUND", await ErrorOf(upper));
    }

    private Task<HttpResponseMessage> CreateSessionAt(string path) =>
        Client.PostAsync(path, JsonContent.Create(new { hash = "garbage" }));
}
