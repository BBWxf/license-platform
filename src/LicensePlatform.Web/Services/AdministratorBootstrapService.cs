using System.Text.Json;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Domain.Administration;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Services;

public interface IAdministratorBootstrapService
{
    Task<bool> InitializeAsync(string userName, string password, CancellationToken cancellationToken);
    Task<bool> ResetPasswordAsync(string userName, string password, CancellationToken cancellationToken);
}

public sealed class AdministratorBootstrapService(
    LicensePlatformDbContext dbContext,
    IAdminPasswordHasher passwordHasher,
    IClock clock) : IAdministratorBootstrapService
{
    public async Task<bool> InitializeAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        if (await dbContext.Administrators.AnyAsync(cancellationToken))
        {
            return false;
        }

        var now = clock.UtcNow;
        var passwordHash = await passwordHasher.HashAsync(password, cancellationToken);
        var administrator = new Administrator(Guid.NewGuid(), userName, passwordHash, now);
        dbContext.Administrators.Add(administrator);
        dbContext.AuditRecords.Add(new AuditRecord(
            Guid.NewGuid(),
            AuditActorType.System,
            null,
            "ADMIN.INITIALIZED",
            "Administrator",
            administrator.Id.ToString("D"),
            JsonSerializer.Serialize(new { userName = administrator.UserName }),
            null,
            "local-cli",
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ResetPasswordAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        var normalized = userName.Trim().ToUpperInvariant();
        var administrator = await dbContext.Administrators
            .SingleOrDefaultAsync(item => item.NormalizedUserName == normalized, cancellationToken);
        if (administrator is null)
        {
            return false;
        }

        var now = clock.UtcNow;
        administrator.ChangePassword(await passwordHasher.HashAsync(password, cancellationToken), now);
        dbContext.AuditRecords.Add(new AuditRecord(
            Guid.NewGuid(),
            AuditActorType.System,
            null,
            "ADMIN.PASSWORD_RESET",
            "Administrator",
            administrator.Id.ToString("D"),
            JsonSerializer.Serialize(new { source = "local-cli" }),
            null,
            "local-cli",
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
