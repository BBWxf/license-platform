using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages.Admin.Settings;

[Authorize]
[RequestSizeLimit(768 * 1024)]
public sealed class IndexModel(ISystemSettingsService settingsService) : PageModel
{
    public SystemSettingsEditor Settings { get; private set; } = new("软件授权管理系统", false, "-", "-");

    [BindProperty]
    public SettingsInput Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Settings = await settingsService.GetAsync(cancellationToken);
        Input.SystemName = Settings.SystemName;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (Input.Logo is { Length: > 512 * 1024 })
        {
            ModelState.AddModelError("Input.Logo", "Logo 文件不能超过 512KB。");
        }

        if (Input.Logo is not null && Input.Logo.ContentType is not ("image/png" or "image/jpeg"))
        {
            ModelState.AddModelError("Input.Logo", "Logo 仅支持 PNG 或 JPEG。");
        }

        if (!ModelState.IsValid)
        {
            Settings = await settingsService.GetAsync(cancellationToken);
            return Page();
        }

        await settingsService.UpdateAsync(
            Input.SystemName.Trim(),
            Input.Logo,
            GetActorId(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            cancellationToken);
        TempData["Success"] = "系统设置已保存。";
        return RedirectToPage();
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("管理员标识不存在。"));

    public sealed class SettingsInput
    {
        [Required]
        [StringLength(80, MinimumLength = 2)]
        public string SystemName { get; set; } = string.Empty;

        public IFormFile? Logo { get; set; }
    }
}
