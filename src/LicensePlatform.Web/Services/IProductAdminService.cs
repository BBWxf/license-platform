namespace LicensePlatform.Web.Services;

public interface IProductAdminService
{
    Task<IReadOnlyList<ProductListItem>> ListAsync(CancellationToken cancellationToken);
    Task<ProductEditor?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Guid> SaveAsync(Guid? id, ProductEditor input, Guid actorId, string sourceIp, CancellationToken cancellationToken);
    Task SetEnabledAsync(Guid id, bool enabled, Guid actorId, string sourceIp, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, Guid actorId, string sourceIp, CancellationToken cancellationToken);
    Task<ClientConfigDownload?> GetClientConfigAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record ProductListItem(
    Guid Id,
    string Name,
    string PublicCode,
    string LicensePrefix,
    bool Enabled,
    long LicenseCount,
    long ActiveBindingCount,
    DateTimeOffset CreatedAt);

public sealed class ProductEditor
{
    public string Name { get; set; } = string.Empty;
    public string LicensePrefix { get; set; } = string.Empty;
}

public sealed record ClientConfigDownload(string FileName, byte[] Content, string ContentType);
