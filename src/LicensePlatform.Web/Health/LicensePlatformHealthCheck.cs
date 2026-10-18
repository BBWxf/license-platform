using LicensePlatform.Application.Abstractions;
using LicensePlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LicensePlatform.Web.Health;

public sealed class LicensePlatformHealthCheck(
    IServiceScopeFactory scopeFactory,
    IDataProtectionProvider dataProtectionProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var dbContext = services.GetRequiredService<LicensePlatformDbContext>();
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy();
            }

            if ((await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                return HealthCheckResult.Unhealthy();
            }

            _ = services.GetRequiredService<ILicenseKeyCryptography>();
            _ = services.GetRequiredService<ILookupHashService>();
            _ = services.GetRequiredService<IProtectedDataService>();

            var protector = dataProtectionProvider.CreateProtector("LicensePlatform.Web.HealthCheck.v1");
            var protectedProbe = protector.Protect("health-check");
            if (!string.Equals(protector.Unprotect(protectedProbe), "health-check", StringComparison.Ordinal))
            {
                return HealthCheckResult.Unhealthy();
            }

            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}
