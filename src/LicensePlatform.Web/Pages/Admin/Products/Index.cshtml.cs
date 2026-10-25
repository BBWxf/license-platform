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

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, bool confirmed, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || id == Guid.Empty || !confirmed)
        {
            TempData["Error"] = "请先确认永久删除产品及其全部卡密。";
            return RedirectToPage();
        }

        try
        {
            var deleted = await productService.DeleteAsync(id, GetActorId(), GetSourceIp(), cancellationToken);
            TempData[deleted ? "Success" : "Error"] = deleted
                ? "产品及其卡密、批次、导出存档和签名密钥已删除，审计记录已保留。"
                : "产品不存在或已被删除。";
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            TempData["Error"] = "删除未完成，数据可能正在被其他操作修改。请刷新后重试。";
        }

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
