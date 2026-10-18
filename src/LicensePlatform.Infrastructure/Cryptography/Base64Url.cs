namespace LicensePlatform.Infrastructure.Cryptography;

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var base64 = value.Replace('-', '+').Replace('_', '/');
        if (base64.Length % 4 == 1)
        {
            throw new FormatException("The Base64Url value has an invalid length.");
        }

        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Convert.FromBase64String(base64);
    }
}
