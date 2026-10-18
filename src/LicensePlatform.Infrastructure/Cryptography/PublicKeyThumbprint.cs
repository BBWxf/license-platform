using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LicensePlatform.Infrastructure.Cryptography;

internal static class PublicKeyThumbprint
{
    public static string FromPem(string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        if (!publicKeyPem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)
            || !publicKeyPem.Contains("-----END PUBLIC KEY-----", StringComparison.Ordinal)
            || publicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new CryptographicException("The installation key must contain only a public SubjectPublicKeyInfo PEM.");
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        var parameters = ecdsa.ExportParameters(false);
        if (ecdsa.KeySize != 256
            || !string.Equals(
                parameters.Curve.Oid.Value,
                ECCurve.NamedCurves.nistP256.Oid.Value,
                StringComparison.Ordinal)
            || parameters.Q.X is not { Length: 32 }
            || parameters.Q.Y is not { Length: 32 })
        {
            throw new CryptographicException("The installation public key must use P-256.");
        }

        var canonical = JsonSerializer.Serialize(new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["crv"] = "P-256",
            ["kty"] = "EC",
            ["x"] = Base64Url.Encode(parameters.Q.X),
            ["y"] = Base64Url.Encode(parameters.Q.Y)
        });
        return Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
