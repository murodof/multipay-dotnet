using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Models;

namespace PaymentService.Infrastructure.Repositories;

public class PaymentSessionRepository(PaymentDbContext session)
{
    public int OrderIdLength =>
        session.Model.FindEntityType(typeof(PaymentSession))!.FindProperty(nameof(PaymentSession.OrderId))!.GetMaxLength()!.Value;

    public virtual Task<PaymentSession?> GetByOrderId(string orderId) =>
        session.PaymentSessions.SingleOrDefaultAsync(item => item.OrderId == orderId);

    public Task<PaymentSession?> GetByIdAndTokenHash(Guid sessionId, string tokenHash) =>
        session.PaymentSessions.SingleOrDefaultAsync(item => item.Id == sessionId && item.SessionTokenHash == tokenHash);

    public Task<PaymentSession?> GetById(Guid sessionId) =>
        session.PaymentSessions.SingleOrDefaultAsync(item => item.Id == sessionId);

    public async Task<PaymentSession> Save(PaymentSession paymentSession)
    {
        if (session.Entry(paymentSession).State == EntityState.Detached)
        {
            session.PaymentSessions.Update(paymentSession);
        }
        await session.SaveChangesAsync();
        await session.RefreshAsync(paymentSession);
        return paymentSession;
    }

    public async Task ClearCardToken(Guid sessionId, string token)
    {
        await using var transaction = await session.Database.BeginTransactionAsync();
        await session.SaveChangesAsync();
        var paymentSession = await session.LockedSessionAsync(sessionId);
        if (paymentSession is not null && paymentSession.SvCardToken == token)
        {
            paymentSession.SvCardToken = null;
        }
        await session.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<PaymentSession?> SetStatus(
        Guid sessionId,
        IReadOnlySet<PaymentSessionStatus> allowed,
        PaymentSessionStatus status,
        Action<PaymentSession>? values = null)
    {
        await using var transaction = await session.Database.BeginTransactionAsync();
        await session.SaveChangesAsync();
        var paymentSession = await session.LockedSessionAsync(sessionId);
        if (paymentSession is null || !allowed.Contains(paymentSession.Status))
        {
            await session.RollbackAsync(transaction);
            return null;
        }
        paymentSession.Status = status;
        values?.Invoke(paymentSession);
        await session.SaveChangesAsync();
        await transaction.CommitAsync();
        await session.RefreshAsync(paymentSession);
        return paymentSession;
    }

    public async Task<PaymentSession?> Create(PaymentSession paymentSession)
    {
        session.PaymentSessions.Add(paymentSession);
        try
        {
            await session.SaveChangesAsync();
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            session.Entry(paymentSession).State = EntityState.Detached;
            return null;
        }
        await session.RefreshAsync(paymentSession);
        return paymentSession;
    }
}
