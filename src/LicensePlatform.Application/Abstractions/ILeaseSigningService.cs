using LicensePlatform.Contracts.Leases;
using LicensePlatform.Domain.Products;

namespace LicensePlatform.Application.Abstractions;

public interface ILeaseSigningService
{
    SignedLeaseDto Sign(LeasePayloadDto payload, ProductSigningKey signingKey);
}
