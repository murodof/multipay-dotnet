using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Services;
using Serilog;
using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

[Collection("app")]
public sealed class TestExceptionLogging(Containers containers) : AppTest(containers)
{
    [Fact]
    public async Task Unhandled_error_is_logged_without_exposing_details()
    {
        var log = new StringWriter();
        App.Overrides.Add(services =>
        {
            services.AddScoped<SessionService, Exploding>();
            services.AddSerilog((provider, logger) =>
                LoggingConfig.SetupLogging(logger, provider.GetRequiredService<IOptions<Settings>>().Value, log, "INFO"));
        });

        var response = await Client.PostAsync("/api/uzcard/session?hash=secret", JsonContent.Create(new { hash = "x" }));

        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal("UNKNOWN_ERROR", await ErrorOf(response));
        Assert.DoesNotContain("internal marker", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("internal marker", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("Unhandled request error on POST /api/uzcard/session", log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hash=secret", log.ToString(), StringComparison.Ordinal);
    }

    // The security middleware is outermost, so the 500 written by the exception middleware inside
    // it carries the headers as well.
    [Fact]
    public async Task The_500_carries_the_security_headers()
    {
        App.Overrides.Add(services => services.AddScoped<SessionService, Exploding>());

        var response = await Client.PostAsync("/api/uzcard/session", JsonContent.Create(new { hash = "x" }));

        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    // The browser hides a cross-origin response without CORS headers, so the frontend would see a
    // network error instead of the 500 envelope.
    [Fact]
    public async Task The_500_carries_security_and_cors_headers()
    {
        App.Overrides.Add(services => services.AddScoped<SessionService, Exploding>());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/uzcard/session") { Content = JsonContent.Create(new { hash = "x" }) };
        request.Headers.Add("Origin", "https://operator.example.uz");

        var response = await Client.SendAsync(request);
        var next = await Client.GetAsync("/nope");

        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal("https://operator.example.uz", TestSecurityHeaders.Header(response, "access-control-allow-origin"));
        Assert.Contains("frame-ancestors https://operator.example.uz", TestSecurityHeaders.Header(response, "content-security-policy"), StringComparison.Ordinal);
        Assert.Equal(404, (int)next.StatusCode);
    }

    // A client that hangs up before the failure must not leave its idempotency key claimed: the
    // retry would be told IN_PROGRESS until the claim's TTL ran out.
    [Fact]
    public async Task A_failure_after_the_client_left_still_releases_the_idempotency_claim()
    {
        using var scope = App.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IdempotencyService>();
        var key = Guid.NewGuid().ToString();
        var claim = (await service.Start("session:verify", key)).CacheKey!;
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = aborted.Token };
        var middleware = new PaymentService.Core.Handlers.UnhandledExceptionMiddleware(
            _ => throw new InvalidOperationException("internal marker"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentService.Core.Handlers.UnhandledExceptionMiddleware>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => middleware.InvokeAsync(context));

        Assert.Null(await App.Redis.Get(claim.Key));
    }

    private sealed class Exploding(
        PaymentSessionRepository sessionRepo,
        RateLimitService rateLimit,
        PaymentService.Infrastructure.ExternalServices.RedisClient redisClient,
        TimeProvider clock,
        IOptions<Settings> options) : SessionService(sessionRepo, rateLimit, redisClient, clock, options)
    {
        public override Task<BaseResponse> Create(SessionCreateSchema req, string clientIp, ProcessingSystem processing, string? userAgent = null) =>
            throw new InvalidOperationException("internal marker");
    }
}
