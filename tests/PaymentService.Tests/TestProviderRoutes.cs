using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Core.Dependencies;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Models;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Services;
using PaymentService.Infrastructure.Utils;

namespace PaymentService.Tests;

[Collection("app")]
public sealed class TestProviderRoutes(Containers containers) : AppTest(containers)
{
    private sealed class FakeFlow(string provider) : IPaymentFlow
    {
        public List<string> Calls { get; } = [];

        private Task<BaseResponse> Called(string name)
        {
            Calls.Add(name);
            return Task.FromResult<BaseResponse>(new SuccessResponse(new Dictionary<string, object?> { ["processing"] = provider }));
        }

        public Task<BaseResponse> SendCard(SessionAccess access, CardSchema card, string clientIp, string? idempotencyKey) => Called("card");

        public Task<BaseResponse> Resend(SessionAccess access, CardSchema card, string clientIp, string? idempotencyKey) => Called("resend");

        public Task<BaseResponse> Verify(SessionAccess access, VerifySchema request, string? idempotencyKey) => Called("verify");

        public Task<BaseResponse> Status(SessionAccess access, string clientIp) => Called("status");

        public Task<BaseResponse> Expire(SessionAccess access, string? idempotencyKey) => Called("expire");
    }

    private sealed class FakeGuard(PaymentSessionRepository repo, Dictionary<string, string> current) : SessionGuard(repo)
    {
        public override Task<SessionAccess> Inject(Guid sessionId, string? authorization) =>
            Task.FromResult(new SessionAccess(new PaymentSession { Processing = current["processing"] }, null));
    }

    [Fact]
    public async Task Provider_routes_keep_sessions_and_services_separate()
    {
        var uzcard = new FakeFlow("UZCARD");
        var humo = new FakeFlow("HUMO");
        var current = new Dictionary<string, string> { ["processing"] = "UZCARD" };
        App.Overrides.Add(services =>
        {
            services.AddScoped<SessionGuard>(provider => new FakeGuard(provider.GetRequiredService<PaymentSessionRepository>(), current));
            services.RemoveAllKeyed<IPaymentFlow>(ProcessingSystem.UZCARD);
            services.RemoveAllKeyed<IPaymentFlow>(ProcessingSystem.HUMO);
            services.AddKeyedSingleton<IPaymentFlow>(ProcessingSystem.UZCARD, uzcard);
            services.AddKeyedSingleton<IPaymentFlow>(ProcessingSystem.HUMO, humo);
        });
        const string id = "00000000-0000-0000-0000-000000000001";

        var uzcardStatus = await Client.GetAsync($"/api/uzcard/session/{id}");
        var wrongHumo = await Client.GetAsync($"/api/humo/session/{id}");
        current["processing"] = "HUMO";
        var humoStatus = await Client.GetAsync($"/api/humo/session/{id}");
        var wrongUzcard = await Client.GetAsync($"/api/uzcard/session/{id}");
        var oldRoute = await Client.GetAsync($"/api/session/{id}");

        Assert.Equal(200, (int)uzcardStatus.StatusCode);
        Assert.Equal(200, (int)humoStatus.StatusCode);
        Assert.Equal(401, (int)wrongHumo.StatusCode);
        Assert.Equal(401, (int)wrongUzcard.StatusCode);
        Assert.Equal(404, (int)oldRoute.StatusCode);
        Assert.Equal(["status"], uzcard.Calls);
        Assert.Equal(["status"], humo.Calls);
    }

    [Fact]
    public void Provider_prefixes()
    {
        Assert.True(Card.UzcardPan("8600123412341239"));
        Assert.True(Card.UzcardPan("5614123412341234"));
        Assert.True(Card.HumoPan("9860123412341234"));
        Assert.False(Card.HumoPan("8600123412341239"));
    }

    private sealed class FakeSessionService(
        PaymentSessionRepository sessionRepo,
        RateLimitService rateLimit,
        PaymentService.Infrastructure.ExternalServices.RedisClient redisClient,
        TimeProvider clock,
        IOptions<Settings> options,
        List<string> providers) : SessionService(sessionRepo, rateLimit, redisClient, clock, options)
    {
        public override Task<BaseResponse> Create(SessionCreateSchema req, string clientIp, ProcessingSystem processing, string? userAgent = null)
        {
            providers.Add(processing.ToString());
            return Task.FromResult<BaseResponse>(new SuccessResponse(new Dictionary<string, object?> { ["processing"] = processing.ToString() }));
        }
    }

    [Fact]
    public async Task Session_creation_binds_the_chosen_provider()
    {
        var providers = new List<string>();
        App.Settings["HUMO_ENABLED"] = "true";
        App.Overrides.Add(services => services.AddScoped<SessionService>(provider => ActivatorUtilities.CreateInstance<FakeSessionService>(provider, providers)));

        var uzcard = await Client.PostAsync("/api/uzcard/session", JsonContent.Create(new { hash = "signed" }));
        var humo = await Client.PostAsync("/api/humo/session", JsonContent.Create(new { hash = "signed" }));

        Assert.Equal(200, (int)uzcard.StatusCode);
        Assert.Equal(200, (int)humo.StatusCode);
        Assert.Equal(["UZCARD", "HUMO"], providers);
    }

    [Fact]
    public async Task Humo_session_is_unavailable_while_humo_is_disabled()
    {
        var response = await Client.PostAsync("/api/humo/session", JsonContent.Create(new { hash = "garbage" }));

        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal("PROCESSING_UNAVAILABLE", await Conftest.ErrorOf(response));
    }
}
