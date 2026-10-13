using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PaymentService.Admin.Pages;

public sealed class LogoutModel(AdminAuth auth) : PageModel
{
    public IActionResult OnGet()
    {
        auth.SignOut(Response);
        return new SeeOther("/admin/login");
    }
}
