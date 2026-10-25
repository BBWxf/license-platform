using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages.Admin.Licenses;

[Authorize]
public sealed class DetailsModel(ILicenseAdminService licenseService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public LicenseDetails? License { get; private set; }

    [BindProperty]
    public ReasonInput Action { get; set; } = new();

    [BindProperty]
    public ExtendInput Extension { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        License = await licenseService.GetAsync(Id, cancellationToken);
        return License is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Action.Reason))
        {
            TempData["Error"] = "启用或禁用授权必须填写原因。";
            return RedirectToPage(new { id = Id });
        }

        await licenseService.SetEnabledAsync(Id, enabled, Action.Reason.Trim(), GetActorId(), GetSourceIp(), cancellationToken);
        TempData["Success"] = enabled ? "授权已启用。" : "授权已禁用。";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostExtendAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(Extension.Reason))
        {
            TempData["Error"] = "延期参数或原因不完整。";
            return RedirectToPage(new { id = Id });
        }

        await licenseService.ExtendAsync(
            Id,
            Extension.ValidityKind,
            Extension.Units,
            Extension.Reason.Trim(),
            GetActorId(),
            GetSourceIp(),
            cancellationToken);
        TempData["Success"] = "授权有效期已延长。";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(bool confirmed, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || Id == Guid.Empty || !confirmed || string.IsNullOrWhiteSpace(Action.Reason))
        {
            TempData["Error"] = "请确认永久删除，并填写不超过 300 字的删除原因。";
            return RedirectToPage(new { id = Id });
        }

        try
        {
            var deleted = await licenseService.DeleteAsync(Id, Action.Reason.Trim(), GetActorId(), GetSourceIp(), cancellationToken);
            TempData[deleted ? "Success" : "Error"] = deleted
                ? "卡密及其设备绑定、激活挑战已删除，审计记录已保留。"
                : "卡密不存在或已被删除。";
            return RedirectToPage("Index");
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            TempData["Error"] = "删除未完成，数据可能正在被其他操作修改。请刷新后重试。";
            return RedirectToPage(new { id = Id });
        }
    }

    public async Task<IActionResult> OnPostUnbindAsync(Guid bindingId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Action.Reason))
        {
            TempData["Error"] = "解绑设备必须填写原因。";
            return RedirectToPage(new { id = Id });
        }

        await licenseService.UnbindAsync(bindingId, Action.Reason.Trim(), GetActorId(), GetSourceIp(), cancellationToken);
        TempData["Success"] = "设备已解绑；旧设备最迟可继续使用到现有 6 小时租约到期。";
        return RedirectToPage(new { id = Id });
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("管理员标识不存在。"));

    private string GetSourceIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public sealed class ReasonInput
    {
        [StringLength(300)]
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class ExtendInput
    {
        public AdminValidityKind ValidityKind { get; set; } = AdminValidityKind.Month;

        [Range(1, 3650)]
        public int Units { get; set; } = 1;

        [StringLength(300)]
        public string Reason { get; set; } = string.Empty;
    }
}
