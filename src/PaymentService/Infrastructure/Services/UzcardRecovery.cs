using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Enums;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Utils;

namespace PaymentService.Infrastructure.Services;

public sealed class UzcardRecovery(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IOptions<Settings> options,
    ILogger<UzcardRecovery> logger) : BackgroundService
{
    private static readonly PaymentSessionStatus[] TokenHolders =
    [
        PaymentSessionStatus.SUCCESS, PaymentSessionStatus.FAILED, PaymentSessionStatus.REVERSED,
        PaymentSessionStatus.NEEDS_MANUAL, PaymentSessionStatus.EXPIRED, PaymentSessionStatus.CREATED,
    ];

    private TimeSpan StaleAfter => TimeSpan.FromSeconds(options.Value.SVGATE_TIMEOUT_SECONDS * 2 + 15);

    private TimeSpan StaleSettlementAfter =>
        TimeSpan.FromSeconds(options.Value.OPERATOR_TIMEOUT_SECONDS + options.Value.SVGATE_TIMEOUT_SECONDS * 2 + 15);

    public async Task RecoverStaleUzcardPayments()
    {
        List<Guid> sessionIds;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            var staleBefore = clock.UtcNow() - StaleAfter;
            var staleSettlementBefore = clock.UtcNow() - StaleSettlementAfter;
            sessionIds = await (
                from session in db.PaymentSessions
                join payment in db.Payments on session.Id equals payment.SessionId into payments
                from payment in payments.DefaultIfEmpty()
                where session.Processing == "UZCARD"
                    && (session.Status == PaymentSessionStatus.PENDING_CHECK
                        || session.Status == PaymentSessionStatus.PAID
                        || (session.Status == PaymentSessionStatus.PAYING
                            && ((payment != null && payment.CreatedAt < staleBefore)
                                || (payment == null && session.UpdatedAt < staleBefore)))
                        || ((session.Status == PaymentSessionStatus.CREDITING || session.Status == PaymentSessionStatus.REVERSING)
                            && session.UpdatedAt < staleSettlementBefore)
                        || (session.SvCardToken != null && TokenHolders.Contains(session.Status)))
                select session.Id).Take(100).ToListAsync();
        }

        foreach (var sessionId in sessionIds)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sessions = scope.ServiceProvider.GetRequiredService<PaymentSessionRepository>();
                var payments = scope.ServiceProvider.GetRequiredService<PaymentRepository>();
                var service = scope.ServiceProvider.GetRequiredService<UzcardService>();
                var session = await sessions.GetById(sessionId);
                if (session is null)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(session.SvCardToken) && session.Status != PaymentSessionStatus.PAYING)
                {
                    await service.RemoveCardToken(session);
                }
                if (session.Status is PaymentSessionStatus.CREDITING or PaymentSessionStatus.REVERSING)
                {
                    if (session.UpdatedAt >= clock.UtcNow() - StaleSettlementAfter)
                    {
                        continue;
                    }
                    var stuck = await sessions.SetStatus(
                        session.Id, new HashSet<PaymentSessionStatus> { session.Status }, PaymentSessionStatus.NEEDS_MANUAL);
                    if (stuck is null)
                    {
                        continue;
                    }
                    logger.LogWarning("UZCARD payment needs manual reconciliation: {SessionId}", sessionId);
                    var stuckPayment = await payments.GetBySessionId(sessionId);
                    if (stuckPayment is not null)
                    {
                        stuckPayment.Status = PaymentStatus.NEEDS_MANUAL;
                        await payments.Save(stuckPayment);
                    }
                    continue;
                }
                if (session.Status is not (PaymentSessionStatus.PAYING or PaymentSessionStatus.PENDING_CHECK or PaymentSessionStatus.PAID))
                {
                    continue;
                }
                var payment = await payments.GetBySessionId(sessionId);
                if (payment is null)
                {
                    logger.LogWarning("UZCARD recovery found session without payment: {SessionId}", sessionId);
                    if (session.Status == PaymentSessionStatus.PAYING && session.UpdatedAt < clock.UtcNow() - StaleAfter)
                    {
                        await sessions.SetStatus(
                            session.Id, new HashSet<PaymentSessionStatus> { PaymentSessionStatus.PAYING }, PaymentSessionStatus.NEEDS_MANUAL);
                    }
                    continue;
                }
                if (session.Status == PaymentSessionStatus.PAYING)
                {
                    if (payment.CreatedAt >= clock.UtcNow() - StaleAfter)
                    {
                        continue;
                    }
                    var nextCheckAt = clock.UtcNow();
                    var finalized = await payments.TransitionPaying(
                        session.Id,
                        PaymentSessionStatus.PENDING_CHECK,
                        PaymentStatus.PENDING_CHECK,
                        paymentValues: item => item.NextCheckAt = nextCheckAt);
                    if (finalized is null)
                    {
                        continue;
                    }
                    (session, payment) = finalized.Value;
                }
                if (session.Status == PaymentSessionStatus.PENDING_CHECK)
                {
                    await service.Recover(session, payment);
                }
                else
                {
                    await service.Credit(session, payment);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "UZCARD recovery failed for session {SessionId}", sessionId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverStaleUzcardPayments();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "UZCARD recovery scan failed");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
