using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaymentService.Core.Configurations;

namespace PaymentService.Admin.Pages;

public sealed class ListModel(PaymentDbContext db) : PageModel
{
    public const int PageSize = 25;

    public AdminView View { get; private set; } = AdminViews.All[0];

    public IReadOnlyList<Base> Rows { get; private set; } = [];

    public int Total { get; private set; }

    public int PageNumber { get; private set; } = 1;

    public string? Search { get; private set; }

    public async Task<IActionResult> OnGetAsync(string identity, int page = 1, string? q = null)
    {
        var view = AdminViews.All.FirstOrDefault(item => item.Identity == identity);
        if (view is null)
        {
            return NotFound();
        }
        View = view;
        PageNumber = Math.Max(1, page);
        Search = string.IsNullOrWhiteSpace(q) || view.SearchField is null ? null : q.Trim();
        var query = view.Query(db, Search);
        Total = await query.CountAsync();
        Rows = await query.OrderByDescending(row => row.CreatedAt).Skip((PageNumber - 1) * PageSize).Take(PageSize).ToListAsync();
        return Page();
    }
}
