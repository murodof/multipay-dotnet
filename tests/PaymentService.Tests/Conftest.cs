using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Models;
using PaymentService.Infrastructure.ExternalServices;
using PaymentService.Infrastructure.Utils;
using Testcontainers.MsSql;
using Testcontainers.Redis;

namespace PaymentService.Tests;

public static class Conftest
{
    public const string KID = "k1";
    public const string SECRET = "k1-test-secret-0123456789abcdefghijklmnop";
    public const string OTHER_SECRET = "k2-other-secret-0123456789abcdefghijklmn";
    public const string OPERATOR_IP = "203.0.113.10";
    public const string TRUSTED_PROXY_IP = "10.0.0.1";
    public const string PHONE = "998901234567";
    public static readonly object DROP = new();

    public static Dictionary<string, string?> Settings(Containers containers) => new()
    {
        ["DATABASE_URL"] = containers.DatabaseUrl,
        ["REDIS_URL"] = containers.RedisUrl,
        ["ENVIRONMENT"] = "development",
        ["SECRET_KEYS"] = $"{KID}:{SECRET}",
        ["ALLOWED_ORIGINS"] = "https://operator.example.uz,https://*.partner.uz",
        ["FORWARDED_ALLOW_IPS"] = TRUSTED_PROXY_IP,
        ["MIN_AMOUNT"] = "100000",
        ["MAX_AMOUNT"] = "100000000",
        ["COMMISSION_PERCENT"] = "1.5",
        ["HASH_TS_WINDOW_SECONDS"] = "300",
        ["NONCE_TTL_SECONDS"] = "900",
        ["SESSION_TTL_SECONDS"] = "600",
        ["SESSION_IDLE_TIMEOUT_SECONDS"] = "180",
        ["IDEMPOTENCY_TTL_SECONDS"] = "600",
        ["OTP_TTL_SECONDS"] = "59",
        ["OTP_LOCK_SECONDS"] = "14400",
        ["PAN_HASH_PEPPER"] = "development-pan-pepper-change-in-production",
        ["SVGATE_URL"] = "https://svgate.test/",
        ["SVGATE_USERNAME"] = "svgate-user",
        ["SVGATE_PASSWORD"] = "svgate-password",
        ["SVGATE_TIMEOUT_SECONDS"] = "25",
        ["SVGATE_TEMPLATE_ID"] = "1",
        ["SVGATE_SERVICE_NAME"] = "Multipay",
        ["SVGATE_MERCHANT_ID"] = "merchant",
        ["SVGATE_TERMINAL_ID"] = "terminal",
        ["SVGATE_MCC"] = "4814",
        ["SVGATE_LEGAL_NAME"] = "Legal",
        ["SVGATE_LEGAL_TYPE"] = "1",
        ["SVGATE_LEGAL_ID"] = "legal-id",
        ["SVGATE_LEGAL_OKED"] = "oked",
        ["HUMO_ENABLED"] = "false",
        ["HUMO_MW_URL"] = "https://humo-mw.test",
        ["HUMO_SMS_URL"] = "https://humo-sms.test",
        ["HUMO_BEARER_TOKEN"] = "humo-token",
        ["HUMO_PS_URL"] = "https://humo-ps.test/PaymentServer",
        ["HUMO_PS_USERNAME"] = "login",
        ["HUMO_PS_PASSWORD"] = "password",
        ["HUMO_MERCHANT_ID"] = "123456789012345",
        ["HUMO_TERMINAL_ID"] = "12345678",
        ["HUMO_CENTRE_ID"] = "bank",
        ["HUMO_SERVICE_NAME"] = "Multipay",
        ["HUMO_PAN_ENCRYPTION_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["HUMO_BALANCE_PRECHECK"] = "false",
        ["OPERATOR_URL"] = "https://operator.test/api/top-up",
        ["OPERATOR_TOKEN"] = "operator-token",
        ["OPERATOR_TIMEOUT_SECONDS"] = "10",
        ["RATE_LIMIT_IP_PER_MINUTE"] = "10",
        ["RATE_LIMIT_IP_PER_HOUR"] = "60",
        ["RATE_LIMIT_PHONE_PER_10_MINUTES"] = "5",
        ["HSTS_MAX_AGE_SECONDS"] = "31536000",
        ["DOCS_ENABLED"] = "false",
        ["ADMIN_ENABLED"] = "false",
        ["ADMIN_USERNAME"] = "admin",
        ["ADMIN_PASSWORD"] = "",
        ["ADMIN_SECRET_KEY"] = "",
        ["ADMIN_COOKIE_SECURE"] = "true",
        ["LOG_LEVEL"] = "WARNING",
        ["LOG_FILE"] = "",
        ["LOG_MAX_BYTES"] = "10485760",
        ["LOG_BACKUP_COUNT"] = "5",
    };

    public static async Task<HttpResponseMessage> CreateSession(HttpClient client, string? hash, string provider = "uzcard", IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{provider}/session")
        {
            Content = JsonContent.Create(new JsonObject { ["hash"] = hash }),
        };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return await client.SendAsync(request);
    }

    public static async Task<JsonObject> Json(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    public static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Json())["error"]!["code"]!.GetValue<string>();

    public static bool IsTrue(this JsonNode? node) => node?.GetValue<bool>() == true;
}

[CollectionDefinition("app")]
public sealed class AppCollection : ICollectionFixture<Containers>;

public sealed class Containers : IAsyncLifetime
{
    private readonly MsSqlContainer sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly RedisContainer redis = new RedisBuilder("redis:7-alpine").Build();

    public string DatabaseUrl { get; private set; } = "";

    public string RedisUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(sql.StartAsync(), redis.StartAsync());
        DatabaseUrl = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sql.GetConnectionString())
        {
            InitialCatalog = "payment_test",
            TrustServerCertificate = true,
        }.ConnectionString;
        RedisUrl = $"redis://{redis.Hostname}:{redis.GetMappedPublicPort(6379)}/0";
        await using var db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    public PaymentDbContext NewDbContext(TimeProvider? clock = null) => new(
        new DbContextOptionsBuilder<PaymentDbContext>().UseSqlServer(DatabaseUrl).Options,
        clock ?? TimeProvider.System);

    public async Task Reset()
    {
        await using var db = NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM humo_log; DELETE FROM svgate_log; DELETE FROM receipts; DELETE FROM payments; DELETE FROM payment_sessions;");
        var connection = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync($"{redis.Hostname}:{redis.GetMappedPublicPort(6379)},allowAdmin=true");
        await connection.GetDatabase(0).ExecuteAsync("FLUSHDB");
        await connection.DisposeAsync();
    }

    public async Task DisposeAsync()
    {
        await sql.DisposeAsync();
        await redis.DisposeAsync();
    }
}

public sealed class TestApp(Containers containers) : WebApplicationFactory<Program>
{
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public Dictionary<string, string?> Settings { get; } = Conftest.Settings(containers);

    public StubRouter Stubs { get; } = new();

    public List<Action<IServiceCollection>> Overrides { get; } = [];

    public long Ts => Clock.GetUtcNow().ToUnixTimeSeconds();

    public HttpClient Client(string? ip = Conftest.OPERATOR_IP)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://pay.test"),
        });
        if (ip is not null)
        {
            client.DefaultRequestHeaders.Add(PeerIpStartupFilter.Header, ip);
        }
        return client;
    }

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    public RedisClient Redis => Service<RedisClient>();

    public PaymentDbContext Db() => containers.NewDbContext(Clock);

    public async Task<List<PaymentSession>> StoredSessions()
    {
        await using var db = Db();
        return await db.PaymentSessions.AsNoTracking().OrderBy(session => session.CreatedAt).ToListAsync();
    }

    public string MakeHash(string kid = Conftest.KID, string secret = Conftest.SECRET, Dictionary<string, object?>? fields = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["order_id"] = $"order-{Guid.NewGuid():N}"[..18],
            ["phone"] = Conftest.PHONE,
            ["amount"] = 5_000_000L,
            ["ts"] = Ts,
            ["nonce"] = Defaults.TokenUrlSafe(16),
        };
        foreach (var (key, value) in fields ?? [])
        {
            payload[key] = value;
        }
        var json = new JsonObject();
        foreach (var (key, value) in payload)
        {
            if (!ReferenceEquals(value, Conftest.DROP))
            {
                json[key] = System.Text.Json.JsonSerializer.SerializeToNode(value);
            }
        }
        return OperatorHash.SignHash(json, kid, secret);
    }

    public string MakeHash(params (string Key, object? Value)[] fields) =>
        MakeHash(fields: fields.ToDictionary(field => field.Key, field => field.Value));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IHostedService>();
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, PeerIpStartupFilter>();
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => Stubs));
            foreach (var apply in Overrides)
            {
                apply(services);
            }
        });
    }
}

public sealed class PeerIpStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public const string Header = "X-Test-Peer";

    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        app.Use(async (Microsoft.AspNetCore.Http.HttpContext context, Func<Task> call) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var peer))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                context.Connection.RemotePort = 50000;
                context.Request.Headers.Remove(Header);
            }
            await call();
        });
        next(app);
    };
}

public sealed class StubRouter : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> hosts = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Host, string Body)> Requests { get; } = [];

    public void Route(string host, Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => hosts[host] = handler;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request.RequestUri!.Host, body));
        }
        if (!hosts.TryGetValue(request.RequestUri!.Host, out var handler))
        {
            throw new HttpRequestException($"no stub for {request.RequestUri.Host}");
        }
        return await handler(request);
    }
}

public abstract class AppTest(Containers containers) : IAsyncLifetime
{
    protected Containers Containers { get; } = containers;

    protected TestApp App { get; } = new(containers);

    private HttpClient? client;

    protected HttpClient Client => client ??= App.Client();

    protected HttpClient ClientFrom(string ip) => App.Client(ip);

    protected string MakeHash(params (string Key, object? Value)[] fields) => App.MakeHash(fields);

    public virtual async Task InitializeAsync() => await Containers.Reset();

    public virtual async Task DisposeAsync() => await App.DisposeAsync();
}
