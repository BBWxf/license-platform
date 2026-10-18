using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages;

[ResponseCache(Duration = 300, Location = ResponseCacheLocation.Client)]
public sealed class BrandLogoModel(ISystemSettingsService settingsService) : PageModel
{
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var logo = await settingsService.GetLogoAsync(cancellationToken);
        return logo is null ? NotFound() : File(logo.Content, logo.ContentType);
    }
}
