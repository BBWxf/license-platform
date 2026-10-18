namespace LicensePlatform.Infrastructure.Cryptography;

public sealed class CryptographyOptions
{
    public const string SectionName = "Cryptography";

    public string LicenseKeyHmacKeyBase64 { get; set; } = string.Empty;

    public string LookupHmacKeyBase64 { get; set; } = string.Empty;

    public string DataProtectionKeyBase64 { get; set; } = string.Empty;
}
