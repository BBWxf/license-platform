using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages.Admin.Products;

[Authorize]
public sealed class EditModel(IProductAdminService productService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? Id { get; set; }

    [BindProperty]
    public ProductInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (Id is null)
        {
            return Page();
        }

        var product = await productService.GetAsync(Id.Value, cancellationToken);
        if (product is null)
        {
            return NotFound();
        }

        Input.Name = product.Name;
        Input.LicensePrefix = product.LicensePrefix;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Input.LicensePrefix = Input.LicensePrefix.Trim().ToUpperInvariant();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var id = await productService.SaveAsync(
            Id,
            new ProductEditor
            {
                Name = Input.Name.Trim(),
                LicensePrefix = Input.LicensePrefix
            },
            GetActorId(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            cancellationToken);

        TempData["Success"] = Id is null ? "产品已创建。" : "产品已更新。";
        return RedirectToPage("Index", new { id });
    }

    private Guid GetActorId()
    {
        return Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("管理员标识不存在。"));
    }

    public sealed class ProductInput
    {
        [Required(ErrorMessage = "请输入产品名称。")]
        [StringLength(120, MinimumLength = 2, ErrorMessage = "产品名称长度应为 2～120 个字符。")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "请输入卡密前缀。")]
        [RegularExpression("^[A-Za-z0-9]{2,8}$", ErrorMessage = "卡密前缀只能包含 2～8 位字母或数字。")]
        public string LicensePrefix { get; set; } = string.Empty;
    }
}
