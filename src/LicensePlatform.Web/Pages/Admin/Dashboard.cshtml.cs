using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicensePlatform.Web.Pages.Admin;

[Authorize]
public sealed class DashboardModel(IAdminDashboardQuery dashboardQuery) : PageModel
{
    public AdminDashboardSnapshot Snapshot { get; private set; } = new(
        0, 0, 0, 0, 0, Array.Empty<RecentAuditItem>());

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Snapshot = await dashboardQuery.GetAsync(cancellationToken);
    }
}
