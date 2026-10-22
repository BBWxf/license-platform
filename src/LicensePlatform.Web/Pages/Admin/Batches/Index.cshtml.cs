using System.Security.Claims;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages.Admin.Batches;

[Authorize]
public sealed class IndexModel(ILicenseBatchAdminService batchService) : PageModel
{
    public IReadOnlyList<BatchListItem> Batches { get; private set; } = Array.Empty<BatchListItem>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Batches = await batchService.ListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnGetExportAsync(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, no-cache";
        Response.Headers.Pragma = "no-cache";
        var export = await batchService.DownloadExportAsync(id, GetActorId(), GetSourceIp(), cancellationToken);
        if (export is null)
        {
            TempData["Error"] = "导出文件不可用。旧版已清除的数据无法还原；如存档仍在，请检查解密配置。";
            return RedirectToPage();
        }

        return File(export.Content, export.ContentType, export.FileName);
    }

    public async Task<IActionResult> OnPostDisableAllAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "批量禁用必须填写原因。";
            return RedirectToPage();
        }

        await batchService.DisableAllAsync(id, reason.Trim(), GetActorId(), GetSourceIp(), cancellationToken);
        TempData["Success"] = "该批次中的卡密已全部禁用。";
        return RedirectToPage();
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("管理员标识不存在。"));

    private string GetSourceIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
