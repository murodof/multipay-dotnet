using Microsoft.EntityFrameworkCore;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Models;

namespace PaymentService.Infrastructure.Repositories;

public sealed class ReceiptRepository(PaymentDbContext session)
{
    public async Task<Receipt> Create(Receipt receipt)
    {
        session.Receipts.Add(receipt);
        await session.SaveChangesAsync();
        await session.RefreshAsync(receipt);
        return receipt;
    }

    public Task<Receipt?> GetById(Guid receiptId) =>
        session.Receipts.SingleOrDefaultAsync(receipt => receipt.Id == receiptId);

    public Task<Receipt?> GetByPaymentId(Guid paymentId) =>
        session.Receipts.SingleOrDefaultAsync(receipt => receipt.PaymentId == paymentId);
}
