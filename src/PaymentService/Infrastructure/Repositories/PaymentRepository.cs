using Microsoft.EntityFrameworkCore;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Enums;
using PaymentService.Domain.Models;

namespace PaymentService.Infrastructure.Repositories;

public sealed class PaymentRepository(PaymentDbContext session)
{
    public async Task<Payment> Create(Payment payment)
    {
        session.Payments.Add(payment);
        await session.SaveChangesAsync();
        await session.RefreshAsync(payment);
        return payment;
    }

    public async Task<PaymentSession?> BeginUzcardPay(Guid sessionId, Payment payment)
    {
        await using var transaction = await session.Database.BeginTransactionAsync();
        await session.SaveChangesAsync();
        var paymentSession = await session.LockedSessionAsync(sessionId);
        if (paymentSession is null
            || paymentSession.Status != PaymentSessionStatus.CARD_VERIFIED
            || paymentSession.SvCardToken is null)
        {
            await session.RollbackAsync(transaction);
            return null;
        }
        paymentSession.Status = PaymentSessionStatus.PAYING;
        session.Payments.Add(payment);
        await session.SaveChangesAsync();
        await transaction.CommitAsync();
        await session.RefreshAsync(paymentSession);
        await session.RefreshAsync(payment);
        return paymentSession;
    }

    public async Task<Payment?> GetBySessionId(Guid sessionId, bool @lock = false)
    {
        if (!@lock)
        {
            return await session.Payments.SingleOrDefaultAsync(payment => payment.SessionId == sessionId);
        }
        await session.SaveChangesAsync();
        return await session.LockedPaymentAsync(sessionId);
    }

    public async Task<Payment> Save(Payment payment)
    {
        if (session.Entry(payment).State == EntityState.Detached)
        {
            session.Payments.Update(payment);
        }
        await session.SaveChangesAsync();
        await session.RefreshAsync(payment);
        return payment;
    }

    public async Task<(PaymentSession Session, Payment Payment)?> TransitionPaying(
        Guid sessionId,
        PaymentSessionStatus sessionStatus,
        PaymentStatus paymentStatus,
        string? failCode = null,
        Action<Payment>? paymentValues = null)
    {
        await using var transaction = await session.Database.BeginTransactionAsync();
        await session.SaveChangesAsync();
        var paymentSession = await session.LockedSessionAsync(sessionId);
        if (paymentSession is null || paymentSession.Status != PaymentSessionStatus.PAYING)
        {
            await session.RollbackAsync(transaction);
            return null;
        }
        var payment = await session.LockedPaymentAsync(sessionId);
        if (payment is null || payment.Status != PaymentStatus.PAYING)
        {
            await session.RollbackAsync(transaction);
            return null;
        }
        paymentSession.Status = sessionStatus;
        paymentSession.FailCode = failCode;
        payment.Status = paymentStatus;
        paymentValues?.Invoke(payment);
        await session.SaveChangesAsync();
        await transaction.CommitAsync();
        await session.RefreshAsync(paymentSession);
        await session.RefreshAsync(payment);
        return (paymentSession, payment);
    }

    public async Task<bool?> RecordRecoveryFailure(Guid sessionId, DateTimeOffset now)
    {
        await using var transaction = await session.Database.BeginTransactionAsync();
        await session.SaveChangesAsync();
        var paymentSession = await session.LockedSessionAsync(sessionId);
        if (paymentSession is null || paymentSession.Status != PaymentSessionStatus.PENDING_CHECK)
        {
            await session.RollbackAsync(transaction);
            return null;
        }
        var payment = await session.LockedPaymentAsync(sessionId);
        if (payment is null || payment.Status != PaymentStatus.PENDING_CHECK)
        {
            await session.RollbackAsync(transaction);
            return null;
        }
        payment.CheckAttempts += 1;
        if (payment.CheckAttempts >= 3)
        {
            payment.Status = PaymentStatus.NEEDS_MANUAL;
            paymentSession.Status = PaymentSessionStatus.NEEDS_MANUAL;
            await session.SaveChangesAsync();
            await transaction.CommitAsync();
            return true;
        }
        payment.NextCheckAt = now + TimeSpan.FromSeconds(new[] { 15, 30 }[payment.CheckAttempts - 1]);
        await session.SaveChangesAsync();
        await transaction.CommitAsync();
        return false;
    }
}
