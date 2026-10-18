using LicensePlatform.Contracts.Common;

namespace LicensePlatform.Contracts.Products;

public sealed record ProductDto(
    Guid Id,
    string Code,
    string Name,
    bool IsEnabled,
    string LicensePrefix,
    int LeaseLifetimeMinutes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version);

public sealed record CreateProductRequest(
    string Code,
    string Name,
    string LicensePrefix,
    int LeaseLifetimeMinutes);

public sealed record UpdateProductRequest(
    string Name,
    string LicensePrefix,
    int LeaseLifetimeMinutes,
    long ExpectedVersion);

public sealed record ProductSigningKeyDto(
    Guid Id,
    Guid ProductId,
    string KeyId,
    string Algorithm,
    string PublicKeyPem,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RetiredAtUtc,
    DateTimeOffset? RevokedAtUtc,
    long Version);

public interface IProductApplicationService
{
    Task<ProductDto> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken = default);

    Task<ProductDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<ProductDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    Task<ProductDto> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default);

    Task<ProductDto> SetEnabledAsync(Guid id, bool enabled, long expectedVersion, CancellationToken cancellationToken = default);

    Task<ProductSigningKeyDto> RotateSigningKeyAsync(Guid productId, long expectedProductVersion, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductSigningKeyDto>> ListSigningKeysAsync(Guid productId, CancellationToken cancellationToken = default);

    Task RevokeSigningKeyAsync(Guid productId, Guid signingKeyId, long expectedVersion, CancellationToken cancellationToken = default);
}
