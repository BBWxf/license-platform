using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages;

public sealed class IndexModel : PageModel
{
    public IActionResult OnGet()
    {
        return User.Identity?.IsAuthenticated == true
            ? RedirectToPage("/Admin/Dashboard")
            : RedirectToPage("/Account/Login");
    }
}
