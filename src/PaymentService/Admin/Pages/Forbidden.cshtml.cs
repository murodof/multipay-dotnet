using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PaymentService.Admin.Pages;

[IgnoreAntiforgeryToken]
public sealed class ForbiddenModel : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status403Forbidden);

    public IActionResult OnPost() => StatusCode(StatusCodes.Status403Forbidden);
}
