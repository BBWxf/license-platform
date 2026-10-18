using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages.Admin.Licenses;

[Authorize]
public sealed class IndexModel(ILicenseAdminService licenseService) : PageModel
{
    public LicenseSearchResult Result { get; private set; } = new(
        Array.Empty<LicenseListItem>(), 1, 50, 0, Array.Empty<ProductOption>());

    public string? Query { get; set; }
    public Guid? ProductId { get; set; }
    public string? Status { get; set; }
    public int PageNumber { get; set; } = 1;

    public async Task OnGetAsync(
        string? query,
        Guid? productId,
        string? status,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        Query = query?.Trim();
        ProductId = productId;
        Status = status;
        PageNumber = Math.Max(1, pageNumber);
        Result = await licenseService.SearchAsync(
            new LicenseSearchQuery(Query, ProductId, Status, PageNumber, 50),
            cancellationToken);
    }
}
