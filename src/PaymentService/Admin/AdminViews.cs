using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Models;
using PaymentService.Infrastructure.Utils;

namespace PaymentService.Admin;

public sealed record AdminView(
    string Identity,
    string Label,
    IReadOnlyList<string> Fields,
    string? SearchField,
    Func<PaymentDbContext, string?, IQueryable<Base>> Query);

public static class AdminViews
{
    public static readonly IReadOnlyList<AdminView> All =
    [
        new("payment-session", "Sessiyalar",
            ["id", "order_id", "processing", "status", "amount", "commission", "total", "pan_masked", "fail_code", "created_at", "updated_at", "expires_at"],
            "order_id",
            (db, search) => db.PaymentSessions.AsNoTracking().Where(item => search == null || item.OrderId.Contains(search))),
        new("payment", "To‘lovlar",
            ["id", "session_id", "status", "amount", "commission", "currency", "sv_ref_num", "humo_payment_id", "operator_tx_id", "created_at", "updated_at"],
            null,
            (db, _) => db.Payments.AsNoTracking()),
        new("receipt", "Cheklar",
            ["id", "payment_id", "number", "created_at"],
            "number",
            (db, search) => db.Receipts.AsNoTracking().Where(item => search == null || item.Number.Contains(search))),
        new("svgate-log", "Gateway loglari",
            ["id", "session_id", "method", "duration_ms", "error_code", "created_at"],
            null,
            (db, _) => db.SvgateLogs.AsNoTracking()),
        new("humo-log", "HUMO loglari",
            ["id", "session_id", "method", "duration_ms", "error_code", "created_at"],
            null,
            (db, _) => db.HumoLogs.AsNoTracking()),
    ];

    public static IResult Stylesheet() =>
        Results.Stream(typeof(AdminViews).Assembly.GetManifestResourceStream("admin/tabler.min.css")!, "text/css; charset=utf-8");

    public static string Heading(string field) => field.Replace('_', ' ');

    public static string? Tone(string field, string value) => field switch
    {
        "status" => value switch
        {
            "SUCCESS" or "PAID" => "green",
            "FAILED" => "red",
            "NEEDS_MANUAL" => "orange",
            "CREATED" or "EXPIRED" or "CANCELLED" or "REVERSED" or "RETURNED" => "secondary",
            _ => "azure",
        },
        "processing" => value == "HUMO" ? "purple" : "blue",
        "fail_code" or "error_code" => value.Length > 0 ? "red" : null,
        _ => null,
    };

    public static string Shape(string field) => field switch
    {
        "id" or "session_id" or "payment_id" => "id",
        "amount" or "commission" or "total" or "duration_ms" => "number",
        _ => "text",
    };

    public static string Display(Base row, string field)
    {
        var property = row.GetType().GetProperties().Single(item => PaymentDbContext.SnakeCase(item.Name) == field);
        return property.GetValue(row) switch
        {
            null => "",
            DateTimeOffset instant => instant.ToOffset(Defaults.TASHKENT).ToString("MMMM d, yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            var value => value.ToString() ?? "",
        };
    }
}
