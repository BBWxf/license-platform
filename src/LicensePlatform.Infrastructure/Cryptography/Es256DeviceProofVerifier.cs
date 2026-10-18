using System.Security.Cryptography;
using LicensePlatform.Application.Abstractions;

namespace LicensePlatform.Infrastructure.Cryptography;

public sealed class Es256DeviceProofVerifier : IDeviceProofVerifier
{
    public bool VerifyEs256(
        string devicePublicKeyPem,
        ReadOnlySpan<byte> challengeNonce,
        string proofSignature)
    {
        if (string.IsNullOrWhiteSpace(devicePublicKeyPem)
            || !devicePublicKeyPem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)
            || !devicePublicKeyPem.Contains("-----END PUBLIC KEY-----", StringComparison.Ordinal)
            || devicePublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal)
            || challengeNonce.Length != 32
            || string.IsNullOrWhiteSpace(proofSignature))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Base64Url.Decode(proofSignature);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            if (signature.Length != 64)
            {
                return false;
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(devicePublicKeyPem);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            return ecdsa.KeySize == 256
                && string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal)
                && parameters.Q.X is { Length: 32 }
                && parameters.Q.Y is { Length: 32 }
                && ecdsa.VerifyData(
                    challengeNonce,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }
}
