using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaymentService.Core.Configurations;

namespace PaymentService.Admin.Pages;

public sealed class IndexModel(PaymentDbContext db) : PageModel
{
    public Dictionary<string, int> Totals { get; } = [];

    public IReadOnlyList<(string Status, int Count)> Statuses { get; private set; } = [];

    public async Task OnGetAsync()
    {
        foreach (var view in AdminViews.All)
        {
            Totals[view.Identity] = await view.Query(db, null).CountAsync();
        }
        var statuses = await db.PaymentSessions.AsNoTracking()
            .GroupBy(session => session.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync();
        Statuses = [.. statuses.OrderByDescending(item => item.Count).Select(item => (item.Status.ToString(), item.Count))];
    }
}
