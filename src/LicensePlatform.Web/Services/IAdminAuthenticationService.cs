namespace LicensePlatform.Web.Services;

public interface IAdminAuthenticationService
{
    Task<AdminAuthenticationResult> AuthenticateAsync(
        string userName,
        string password,
        string sourceIp,
        CancellationToken cancellationToken);
}

public sealed record AdminAuthenticationResult(
    bool Succeeded,
    Guid? AdminId = null,
    string? UserName = null,
    string? CredentialStamp = null,
    DateTimeOffset? LockedUntil = null);
