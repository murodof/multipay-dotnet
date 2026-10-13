using PaymentService.Core.Configurations;
using PaymentService.Domain.Models;

namespace PaymentService.Infrastructure.Repositories;

public sealed class HumoLogRepository(PaymentDbContext session)
{
    public async Task<HumoLog> Create(HumoLog log)
    {
        session.HumoLogs.Add(log);
        await session.SaveChangesAsync();
        await session.RefreshAsync(log);
        return log;
    }
}
