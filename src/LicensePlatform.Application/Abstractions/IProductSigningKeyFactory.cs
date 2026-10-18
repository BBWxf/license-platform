using LicensePlatform.Domain.Products;

namespace LicensePlatform.Application.Abstractions;

public interface IProductSigningKeyFactory
{
    ProductSigningKey Create(Guid productId, DateTimeOffset createdAtUtc);
}
