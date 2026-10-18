using System.Security.Cryptography;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Domain.Products;

namespace LicensePlatform.Infrastructure.Cryptography;

public sealed class Es256ProductSigningKeyFactory : IProductSigningKeyFactory
{
    private readonly IProtectedDataService _protectedData;

    public Es256ProductSigningKeyFactory(IProtectedDataService protectedData)
    {
        _protectedData = protectedData;
    }

    public ProductSigningKey Create(Guid productId, DateTimeOffset createdAtUtc)
    {
        if (productId == Guid.Empty)
        {
            throw new ArgumentException("Product id cannot be empty.", nameof(productId));
        }

        Span<byte> keyIdEntropy = stackalloc byte[16];
        RandomNumberGenerator.Fill(keyIdEntropy);
        var keyId = Base64Url.Encode(keyIdEntropy);
        CryptographicOperations.ZeroMemory(keyIdEntropy);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = ecdsa.ExportPkcs8PrivateKey();
        try
        {
            var protectedPrivateKey = _protectedData.Protect(
                privateKey,
                SigningKeyProtectionPurpose.For(productId, keyId));

            return new ProductSigningKey(
                Guid.NewGuid(),
                productId,
                keyId,
                ecdsa.ExportSubjectPublicKeyInfoPem(),
                protectedPrivateKey,
                createdAtUtc);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}
