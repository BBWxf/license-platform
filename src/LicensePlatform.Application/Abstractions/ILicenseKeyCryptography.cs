namespace LicensePlatform.Application.Abstractions;

public sealed record GeneratedLicenseKey(
    string Prefix,
    string PlainText,
    string Normalized,
    byte[] LookupHash,
    string Hint);

public interface ILicenseKeyCryptography
{
    GeneratedLicenseKey Generate(string licensePrefix);

    string Normalize(string value);

    string GetPrefix(string value);

    byte[] ComputeLookupHash(string value);
}
