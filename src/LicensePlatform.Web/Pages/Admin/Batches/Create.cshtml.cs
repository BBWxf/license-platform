using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages.Admin.Batches;

[Authorize]
public sealed class CreateModel(ILicenseBatchAdminService batchService) : PageModel
{
    public IReadOnlyList<ProductOption> Products { get; private set; } = Array.Empty<ProductOption>();

    [BindProperty]
    public BatchInput Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Products = await batchService.GetProductOptionsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Products = await batchService.GetProductOptionsAsync(cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var durationUnits = Input.ValidityKind == AdminValidityKind.Permanent ? 0 : Input.DurationUnits;
        var id = await batchService.CreateAsync(
            new CreateBatchCommand(
                Input.ProductId,
                Input.Name.Trim(),
                Input.Count,
                Input.ValidityKind,
                durationUnits,
                string.IsNullOrWhiteSpace(Input.Note) ? null : Input.Note.Trim()),
            GetActorId(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            cancellationToken);

        TempData["Success"] = "卡密已生成，请在 15 分钟内完成一次性 CSV 下载。";
        return RedirectToPage("Index", new { id });
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("管理员标识不存在。"));

    public sealed class BatchInput
    {
        [Required(ErrorMessage = "请选择产品。")]
        public Guid ProductId { get; set; }

        [Required(ErrorMessage = "请输入批次名称。")]
        [StringLength(120, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;

        [Range(1, 10000, ErrorMessage = "每批生成数量应为 1～10000。")]
        public int Count { get; set; } = 100;

        public AdminValidityKind ValidityKind { get; set; } = AdminValidityKind.Month;

        [Range(1, 3650, ErrorMessage = "时长数量应为 1～3650。")]
        public int DurationUnits { get; set; } = 1;

        [StringLength(500)]
        public string? Note { get; set; }
    }
}
