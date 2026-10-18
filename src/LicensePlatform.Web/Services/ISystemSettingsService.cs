using Microsoft.AspNetCore.Http;

namespace LicensePlatform.Web.Services;

public interface ISystemSettingsService
{
    Task<SystemSettingsEditor> GetAsync(CancellationToken cancellationToken);
    Task<BrandLogo?> GetLogoAsync(CancellationToken cancellationToken);
    Task UpdateAsync(string systemName, IFormFile? logo, Guid actorId, string sourceIp, CancellationToken cancellationToken);
}

public sealed record SystemSettingsEditor(string SystemName, bool HasLogo, string InstanceId, string Version);

public sealed record BrandLogo(byte[] Content, string ContentType);
