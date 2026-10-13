using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using PaymentService.Admin;
using PaymentService.Core.Configurations;
using PaymentService.Core.Dependencies;
using PaymentService.Core.Handlers;
using PaymentService.Core.Middlewares;
using PaymentService.Domain.Enums;
using PaymentService.Infrastructure.ExternalServices;
using PaymentService.Infrastructure.ExternalServices.Humo;
using PaymentService.Infrastructure.ExternalServices.Uzcard;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
DotEnv.AddTo(builder.Configuration, ".env");
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.Services.AddOptions<Settings>()
    .Configure<IConfiguration>((settings, configuration) =>
    {
        var loaded = Settings.Load(configuration);
        foreach (var property in typeof(Settings).GetProperties().Where(property => property.CanWrite))
        {
            property.SetValue(settings, property.GetValue(loaded));
        }
        settings.LoadErrors.AddRange(loaded.LoadErrors);
    })
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<Settings>, SettingsValidator>();
builder.Services.AddSingleton<IConfigureOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>, ForwardedHeadersSetup>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSerilog((services, logger) =>
    LoggingConfig.SetupLogging(logger, services.GetRequiredService<IOptions<Settings>>().Value));

builder.Services.AddDbContext<PaymentDbContext>((services, database) =>
    database.UseSqlServer(services.GetRequiredService<IOptions<Settings>>().Value.DATABASE_URL)
        .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.RowLimitingOperationWithoutOrderByWarning)));

builder.Services.AddSingleton<RedisClient>();
builder.Services.AddScoped<PaymentSessionRepository>();
builder.Services.AddScoped<PaymentRepository>();
builder.Services.AddScoped<ReceiptRepository>();
builder.Services.AddScoped<SvgateLogRepository>();
builder.Services.AddScoped<HumoLogRepository>();
builder.Services.AddScoped<IdempotencyClaims>();
builder.Services.AddScoped<IdempotencyService>();
builder.Services.AddScoped<RateLimitService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<ReceiptService>();
builder.Services.AddScoped<SessionGuard>();
builder.Services.AddScoped<UzcardService>();
builder.Services.AddKeyedScoped<IPaymentFlow>(ProcessingSystem.UZCARD, (services, _) => services.GetRequiredService<UzcardService>());
builder.Services.AddSingleton<UzcardRecovery>();
builder.Services.AddHostedService(services => services.GetRequiredService<UzcardRecovery>());
builder.Services.AddScoped<HumoService>();
builder.Services.AddKeyedScoped<IPaymentFlow>(ProcessingSystem.HUMO, (services, _) => services.GetRequiredService<HumoService>());
builder.Services.AddSingleton<HumoRecovery>();
builder.Services.AddHostedService(services => services.GetRequiredService<HumoRecovery>());

builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false }));
builder.Services.AddHttpClient<SvgateClient>(http => http.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient<OperatorClient>(http => http.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient<HumoMiddlewareClient>(http => http.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient<HumoSmsClient>(http => http.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient<HumoPaymentClient>(http => http.Timeout = Timeout.InfiniteTimeSpan);

builder.Services.AddOpenApi("v1", openApi => openApi.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Multipay e-com";
    return Task.CompletedTask;
}));

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(api => api.InvalidModelStateResponseFactory = ExceptionHandler.ValidationExceptionHandler);
builder.Services.AddScoped<AdminAuth>();
builder.Services.AddRazorPages(razor => razor.RootDirectory = "/Admin/Pages")
    .AddMvcOptions(mvc => mvc.Filters.Add<AdminAuthFilter>());

var app = builder.Build();

app.UseMiddleware<AccessLogMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<UnhandledExceptionMiddleware>();
app.UseMiddleware<ApiCorsMiddleware>();
app.UseForwardedHeaders();
app.UseStatusCodePages(ExceptionHandler.HttpExceptionHandler);
app.UseMiddleware<StarletteRoutingMiddleware>();
app.UseRouting();
app.MapControllers();
if (app.Services.GetRequiredService<IOptions<Settings>>().Value.ADMIN_ENABLED)
{
    app.MapRazorPages();
    app.MapGet("/admin/statics/tabler.min.css", AdminViews.Stylesheet).ExcludeFromDescription();
}
if (app.Services.GetRequiredService<IOptions<Settings>>().Value.DOCS_ENABLED)
{
    app.MapOpenApi("/api/openapi.json");
    app.MapGet("/api/docs", () => Results.Content(Docs.SwaggerUiHtml("/api/openapi.json", "Multipay e-com - Swagger UI"), "text/html; charset=utf-8"))
        .ExcludeFromDescription();
}

app.Run();

public partial class Program;
