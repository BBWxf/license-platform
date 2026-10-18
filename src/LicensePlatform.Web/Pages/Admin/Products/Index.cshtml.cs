using System.Security.Claims;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages.Admin.Products;

[Authorize]
public sealed class IndexModel(IProductAdminService productService) : PageModel
{
    public IReadOnlyList<ProductListItem> Products { get; private set; } = Array.Empty<ProductListItem>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Products = await productService.ListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        await productService.SetEnabledAsync(id, enabled, GetActorId(), GetSourceIp(), cancellationToken);
        TempData["Success"] = enabled ? "产品已启用。" : "产品已停用。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetClientConfigAsync(Guid id, CancellationToken cancellationToken)
    {
        var download = await productService.GetClientConfigAsync(id, cancellationToken);
        return download is null
            ? NotFound()
            : File(download.Content, download.ContentType, download.FileName);
    }

    private Guid GetActorId()
    {
        return Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("管理员标识不存在。"));
    }

    private string GetSourceIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
