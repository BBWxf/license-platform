using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages.Admin.Audit;

[Authorize]
public sealed class IndexModel(IAuditAdminService auditService) : PageModel
{
    public AuditSearchResult Result { get; private set; } = new(Array.Empty<AuditListItem>(), 1, 50, 0);
    public string? ActionFilter { get; private set; }

    public async Task OnGetAsync(string? action, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        ActionFilter = action?.Trim();
        Result = await auditService.SearchAsync(Math.Max(1, pageNumber), 50, ActionFilter, cancellationToken);
    }
}
