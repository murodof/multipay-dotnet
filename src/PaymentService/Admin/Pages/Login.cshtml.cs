using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PaymentService.Admin.Pages;

[IgnoreAntiforgeryToken]
public sealed class LoginModel(AdminAuth auth) : PageModel
{
    public string CsrfToken { get; private set; } = "";

    public string? Error { get; private set; }

    public void OnGet() => CsrfToken = auth.IssueCsrf(HttpContext);

    public IActionResult OnPost(string? username, string? password, string? csrftoken)
    {
        if (!auth.CsrfValid(Request, csrftoken) || !auth.CredentialsMatch(username ?? "", password ?? ""))
        {
            CsrfToken = auth.IssueCsrf(HttpContext);
            Error = "Login yoki parol noto‘g‘ri";
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }
        auth.SignIn(Response);
        return new SeeOther("/admin/");
    }
}
