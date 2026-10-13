using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Enums;
using PaymentService.Infrastructure.ExternalServices.Humo;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Utils;

namespace PaymentService.Infrastructure.Services;

public sealed class HumoRecovery(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IOptions<Settings> options,
    ILogger<HumoRecovery> logger) : BackgroundService
{
    private static readonly HashSet<PaymentSessionStatus> Stale =
        [PaymentSessionStatus.HELD, PaymentSessionStatus.CREDITING, PaymentSessionStatus.CONFIRMING];

    private static readonly HashSet<PaymentSessionStatus> Pending =
        [PaymentSessionStatus.PENDING_CHECK, PaymentSessionStatus.RETURNING];

    public async Task RecoverStaleHumoPayments()
    {
        var now = clock.UtcNow();
        var idleBefore = now - TimeSpan.FromSeconds(options.Value.SESSION_IDLE_TIMEOUT_SECONDS);
        var stale = Stale.ToArray();
        List<Guid> sessionIds;
        await using (var scope = scopes.CreateAsyncScope())
        {
            sessionIds = await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().PaymentSessions
                .Where(session => session.Processing == "HUMO"
                    && stale.Contains(session.Status)
                    && (session.ExpiresAt <= now || session.LastActivityAt <= idleBefore))
                .Select(session => session.Id)
                .Take(100)
                .ToListAsync();
        }
        foreach (var sessionId in sessionIds)
        {
            await using var scope = scopes.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<PaymentSessionRepository>();
            var payments = scope.ServiceProvider.GetRequiredService<PaymentRepository>();
            var current = await sessions.GetById(sessionId);
            if (current is null)
            {
                continue;
            }
            var originalStatus = current.Status;
            if (!Stale.Contains(originalStatus))
            {
                continue;
            }
            var session = await sessions.SetStatus(
                sessionId,
                new HashSet<PaymentSessionStatus> { originalStatus },
                originalStatus == PaymentSessionStatus.HELD ? PaymentSessionStatus.CANCELLING : PaymentSessionStatus.NEEDS_MANUAL);
            if (session is null)
            {
                continue;
            }
            var payment = await payments.GetBySessionId(sessionId);
            if (payment is null)
            {
                session.Status = PaymentSessionStatus.NEEDS_MANUAL;
                await sessions.Save(session);
                continue;
            }
            if (originalStatus != PaymentSessionStatus.HELD)
            {
                payment.Status = PaymentStatus.NEEDS_MANUAL;
                await payments.Save(payment);
                logger.LogWarning("HUMO payment needs manual reconciliation: {PaymentId}", payment.Id);
                continue;
            }
            payment.Status = PaymentStatus.CANCELLING;
            await payments.Save(payment);
            var ps = scope.ServiceProvider.GetRequiredService<HumoPaymentClient>();
            var logs = scope.ServiceProvider.GetRequiredService<HumoLogRepository>();
            var paymentRef = payment.Id.ToString();
            var request = new System.Text.Json.Nodes.JsonObject { ["paymentRef"] = paymentRef };
            var cancelled = await HumoAudit.RecordHumoCall(logs, sessionId, "PS", "CancelRequest", request, () => ps.Cancel(paymentRef));
            if (!cancelled.Ok)
            {
                var checkedPayment = await HumoAudit.RecordHumoCall(
                    logs, sessionId, "PS", "GetPayment", (System.Text.Json.Nodes.JsonObject)request.DeepClone(), () => ps.GetPayment(paymentRef));
                if (checkedPayment.Ok && Py.StrEquals(checkedPayment.Result.Get("status"), "cancelled"))
                {
                    cancelled = checkedPayment;
                }
            }
            var code = cancelled.Result.Get("auth_action_code_final");
            if (cancelled.Ok && (code is null || Py.StrEquals(code, "000") || Py.StrEquals(code, "0")))
            {
                session.Status = PaymentSessionStatus.CANCELLED;
                payment.Status = PaymentStatus.CANCELLED;
            }
            else
            {
                session.Status = PaymentSessionStatus.NEEDS_MANUAL;
                payment.Status = PaymentStatus.NEEDS_MANUAL;
            }
            await sessions.Save(session);
            await payments.Save(payment);
        }
    }

    public async Task ReconcileHumoPendingPayments()
    {
        var now = clock.UtcNow();
        var pending = Pending.ToArray();
        List<Guid> sessionIds;
        await using (var scope = scopes.CreateAsyncScope())
        {
            sessionIds = await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().PaymentSessions
                .Where(session => session.Processing == "HUMO" && pending.Contains(session.Status))
                .Select(session => session.Id)
                .Take(100)
                .ToListAsync();
        }
        foreach (var sessionId in sessionIds)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sessions = scope.ServiceProvider.GetRequiredService<PaymentSessionRepository>();
                var payments = scope.ServiceProvider.GetRequiredService<PaymentRepository>();
                var session = await sessions.GetById(sessionId);
                if (session is null || !Pending.Contains(session.Status))
                {
                    continue;
                }
                var payment = await payments.GetBySessionId(sessionId);
                if (payment is null)
                {
                    logger.LogWarning("HUMO reconciliation found session without payment: {SessionId}", sessionId);
                    continue;
                }
                if (payment.NextCheckAt is not null && now < payment.NextCheckAt)
                {
                    continue;
                }
                var ps = scope.ServiceProvider.GetRequiredService<HumoPaymentClient>();
                var paymentRef = payment.Id.ToString();
                var checkedPayment = await HumoAudit.RecordHumoCall(
                    scope.ServiceProvider.GetRequiredService<HumoLogRepository>(), sessionId, "PS", "GetPayment",
                    new System.Text.Json.Nodes.JsonObject { ["paymentRef"] = paymentRef }, () => ps.GetPayment(paymentRef));
                var status = checkedPayment.Ok ? checkedPayment.Result.Get("status") : null;
                if (session.Status == PaymentSessionStatus.PENDING_CHECK && Py.StrEquals(status, "finished") && !string.IsNullOrEmpty(payment.OperatorTxId))
                {
                    await scope.ServiceProvider.GetRequiredService<HumoService>().Finish(session, payment, checkedPayment);
                    continue;
                }
                payment.CheckAttempts += 1;
                if (payment.CheckAttempts >= 3)
                {
                    var transitioned = await sessions.SetStatus(
                        sessionId, new HashSet<PaymentSessionStatus> { session.Status }, PaymentSessionStatus.NEEDS_MANUAL);
                    if (transitioned is not null)
                    {
                        payment.Status = PaymentStatus.NEEDS_MANUAL;
                        await payments.Save(payment);
                        logger.LogWarning("HUMO payment needs manual reconciliation: {PaymentId}", payment.Id);
                    }
                }
                else
                {
                    payment.NextCheckAt = now + TimeSpan.FromSeconds(new[] { 15, 30 }[payment.CheckAttempts - 1]);
                    await payments.Save(payment);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "HUMO reconciliation failed for session {SessionId}", sessionId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.HUMO_ENABLED)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverStaleHumoPayments();
                await ReconcileHumoPendingPayments();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "HUMO hold cleanup failed");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
