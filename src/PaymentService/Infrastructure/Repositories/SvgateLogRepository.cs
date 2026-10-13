using PaymentService.Core.Configurations;
using PaymentService.Domain.Models;

namespace PaymentService.Infrastructure.Repositories;

public sealed class SvgateLogRepository(PaymentDbContext session)
{
    public async Task<SvgateLog> Create(SvgateLog log)
    {
        session.SvgateLogs.Add(log);
        await session.SaveChangesAsync();
        await session.RefreshAsync(log);
        return log;
    }
}
