using System.Security.Cryptography;
using System.Text;

namespace LicensePlatform.Application.Security;

public static class AdminCredentialStamp
{
    public const string ClaimType = "license-platform:admin-credential-stamp";

    public static string Create(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        var bytes = Encoding.UTF8.GetBytes(passwordHash);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static bool Matches(string passwordHash, string? suppliedStamp)
    {
        if (string.IsNullOrWhiteSpace(passwordHash)
            || string.IsNullOrWhiteSpace(suppliedStamp)
            || suppliedStamp.Length != 64)
        {
            return false;
        }

        var expectedBytes = Encoding.ASCII.GetBytes(Create(passwordHash));
        var suppliedBytes = Encoding.ASCII.GetBytes(suppliedStamp);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(suppliedBytes);
        }
    }
}
