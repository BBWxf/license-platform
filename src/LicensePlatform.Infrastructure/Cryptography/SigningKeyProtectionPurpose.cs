namespace LicensePlatform.Infrastructure.Cryptography;

internal static class SigningKeyProtectionPurpose
{
    public static string For(Guid productId, string keyId) => $"product-signing-key:v1:{productId:D}:{keyId}";
}
