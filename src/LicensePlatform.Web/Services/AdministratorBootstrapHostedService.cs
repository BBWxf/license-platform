using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Services;

public sealed class AdministratorBootstrapHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<AdministratorBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configuredPath = configuration["Security:BootstrapAdminPasswordFile"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LicensePlatformDbContext>();
        if (await dbContext.Administrators.AnyAsync(cancellationToken))
        {
            return;
        }

        var passwordPath = ConfiguredSecretReader.ResolvePath(environment.ContentRootPath, configuredPath);
        var password = await ConfiguredSecretReader.ReadPasswordAsync(passwordPath, cancellationToken);
        var userName = configuration["Security:BootstrapAdminUsername"] ?? "admin";
        var bootstrapService = scope.ServiceProvider.GetRequiredService<IAdministratorBootstrapService>();
        if (await bootstrapService.InitializeAsync(userName, password, cancellationToken))
        {
            logger.LogInformation("The initial administrator account was created from the configured bootstrap secret.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
