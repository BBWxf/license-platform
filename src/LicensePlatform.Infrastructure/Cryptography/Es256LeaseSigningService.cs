using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Contracts.Leases;
using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Products;

namespace LicensePlatform.Infrastructure.Cryptography;

public sealed class Es256LeaseSigningService : ILeaseSigningService
{
    private readonly IProtectedDataService _protectedData;

    public Es256LeaseSigningService(IProtectedDataService protectedData)
    {
        _protectedData = protectedData;
    }

    public SignedLeaseDto Sign(LeasePayloadDto payload, ProductSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signingKey);

        if (signingKey.Status != SigningKeyStatus.Active)
        {
            throw new DomainException("Only an active product signing key can issue leases.");
        }

        if (!string.Equals(signingKey.Algorithm, "ES256", StringComparison.Ordinal))
        {
            throw new DomainException("The product signing key algorithm is not ES256.");
        }

        if (payload.ProductId != signingKey.ProductId)
        {
            throw new DomainException("The lease product does not match the signing key product.");
        }

        ValidateLeaseTimes(payload);

        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = "ES256",
            ["typ"] = "JWT",
            ["kid"] = signingKey.KeyId
        });

        var claims = new Dictionary<string, object?>
        {
            ["iss"] = payload.Issuer,
            ["aud"] = payload.Audience,
            ["sub"] = payload.LicenseKeyId.ToString("D"),
            ["jti"] = payload.LeaseId,
            ["iat"] = payload.IssuedAtUtc.ToUnixTimeSeconds(),
            ["nbf"] = payload.NotBeforeUtc.ToUnixTimeSeconds(),
            ["exp"] = payload.ExpiresAtUtc.ToUnixTimeSeconds(),
            ["product_id"] = payload.ProductId.ToString("D"),
            ["product_code"] = payload.ProductCode,
            ["device_id"] = payload.DeviceId,
            ["installation_key_thumbprint"] = payload.InstallationKeyThumbprint,
            ["cnf"] = new Dictionary<string, object>
            {
                ["jkt"] = payload.InstallationKeyThumbprint
            },
            ["binding_version"] = payload.BindingVersion,
            ["entitlement_exp"] = payload.EntitlementExpiresAtUtc?.ToUnixTimeSeconds(),
            ["permanent"] = !payload.EntitlementExpiresAtUtc.HasValue
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(claims);
        var signingInput = Encoding.ASCII.GetBytes($"{Base64Url.Encode(header)}.{Base64Url.Encode(payloadBytes)}");
        var privateKey = _protectedData.Unprotect(
            signingKey.ProtectedPrivateKey,
            SigningKeyProtectionPurpose.For(signingKey.ProductId, signingKey.KeyId));

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            if (bytesRead != privateKey.Length
                || ecdsa.KeySize != 256
                || !string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal))
            {
                throw new CryptographicException("The product signing key is not a complete P-256 PKCS#8 key.");
            }

            var signature = ecdsa.SignData(
                signingInput,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (signature.Length != 64)
            {
                throw new CryptographicException("The ES256 signature must contain 64 bytes.");
            }

            return new SignedLeaseDto(
                $"{Encoding.ASCII.GetString(signingInput)}.{Base64Url.Encode(signature)}",
                signingKey.KeyId,
                "ES256",
                payload.ExpiresAtUtc);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(signingInput);
            CryptographicOperations.ZeroMemory(payloadBytes);
            CryptographicOperations.ZeroMemory(header);
        }
    }

    private static void ValidateLeaseTimes(LeasePayloadDto payload)
    {
        if (payload.IssuedAtUtc.Offset != TimeSpan.Zero
            || payload.NotBeforeUtc.Offset != TimeSpan.Zero
            || payload.ExpiresAtUtc.Offset != TimeSpan.Zero
            || (payload.EntitlementExpiresAtUtc.HasValue
                && payload.EntitlementExpiresAtUtc.Value.Offset != TimeSpan.Zero))
        {
            throw new DomainException("Lease timestamps must use the UTC offset.");
        }

        if (payload.NotBeforeUtc > payload.IssuedAtUtc
            || payload.ExpiresAtUtc <= payload.IssuedAtUtc
            || payload.ExpiresAtUtc - payload.IssuedAtUtc > TimeSpan.FromHours(6))
        {
            throw new DomainException("Lease time bounds are invalid or exceed the six-hour limit.");
        }

        if (payload.EntitlementExpiresAtUtc.HasValue
            && payload.ExpiresAtUtc > payload.EntitlementExpiresAtUtc.Value)
        {
            throw new DomainException("A lease cannot outlive its entitlement.");
        }

        if (payload.BindingVersion < 1)
        {
            throw new DomainException("Lease binding version must be positive.");
        }
    }
}
