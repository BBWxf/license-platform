using System.Text.Json;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Application.Security;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Services;

public sealed class AdminAuthenticationService(
    LicensePlatformDbContext dbContext,
    IAdminPasswordHasher passwordHasher,
    IClock clock,
    IHttpContextAccessor httpContextAccessor) : IAdminAuthenticationService
{
    private readonly Lazy<Task<string>> _dummyHash = new(() =>
        passwordHasher.HashAsync("Not-A-Real-Password-Change-Me-123!"));

    public async Task<AdminAuthenticationResult> AuthenticateAsync(
        string userName,
        string password,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var normalized = userName.Trim().ToUpperInvariant();
        var administrator = await dbContext.Administrators
            .SingleOrDefaultAsync(item => item.NormalizedUserName == normalized, cancellationToken);
        var now = clock.UtcNow;

        if (administrator is null)
        {
            await passwordHasher.VerifyAsync(await _dummyHash.Value, password, cancellationToken);
            await AddAuditAsync(null, "ADMIN.LOGIN_FAILED", "unknown", sourceIp, now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new AdminAuthenticationResult(false);
        }

        if (administrator.IsLockedOut(now))
        {
            await AddAuditAsync(administrator.Id, "ADMIN.LOGIN_LOCKED", administrator.Id.ToString("D"), sourceIp, now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new AdminAuthenticationResult(false, LockedUntil: administrator.LockoutEndUtc);
        }

        if (!await passwordHasher.VerifyAsync(administrator.PasswordHash, password, cancellationToken))
        {
            administrator.RecordFailedAccess(now);
            await AddAuditAsync(administrator.Id, "ADMIN.LOGIN_FAILED", administrator.Id.ToString("D"), sourceIp, now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new AdminAuthenticationResult(false, LockedUntil: administrator.LockoutEndUtc);
        }

        administrator.RecordSuccessfulLogin(now);
        await AddAuditAsync(administrator.Id, "ADMIN.LOGIN_SUCCEEDED", administrator.Id.ToString("D"), sourceIp, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new AdminAuthenticationResult(
            true,
            administrator.Id,
            administrator.UserName,
            AdminCredentialStamp.Create(administrator.PasswordHash));
    }

    private Task AddAuditAsync(
        Guid? actorId,
        string action,
        string entityId,
        string sourceIp,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var traceId = httpContextAccessor.HttpContext?.TraceIdentifier;
        return dbContext.AuditRecords.AddAsync(
            new AuditRecord(
                Guid.NewGuid(),
                AuditActorType.Administrator,
                actorId?.ToString("D"),
                action,
                "Administrator",
                entityId,
                JsonSerializer.Serialize(new { result = "recorded" }),
                traceId,
                sourceIp,
                now),
            cancellationToken).AsTask();
    }
}
