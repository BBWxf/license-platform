using System.Security.Cryptography;
using System.Text;

namespace LicensePlatform.Web.Security;

public static class ConfiguredSecretReader
{
    private const int MaximumSecretFileBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ReadBase64Secret(
        IConfiguration configuration,
        string contentRootPath,
        string fileSetting,
        string fallbackSetting,
        int minimumDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileSetting);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackSetting);

        var configuredPath = configuration[fileSetting];
        var encoded = string.IsNullOrWhiteSpace(configuredPath)
            ? configuration[fallbackSetting]
            : ReadTextFile(ResolvePath(contentRootPath, configuredPath));
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException($"{fileSetting} is required.");
        }

        encoded = encoded.Trim();
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"The secret configured by {fileSetting} is not valid Base64.", exception);
        }

        try
        {
            if (decoded.Length < minimumDecodedBytes)
            {
                throw new InvalidOperationException(
                    $"The secret configured by {fileSetting} must decode to at least {minimumDecodedBytes} bytes.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }

        return encoded;
    }

    public static async Task<string> ReadPasswordAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        ValidateRegularFile(fullPath);
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        try
        {
            if (bytes.Length is 0 or > MaximumSecretFileBytes)
            {
                throw new InvalidOperationException("The password file is empty or exceeds the allowed size.");
            }

            var value = StrictUtf8.GetString(bytes).TrimEnd('\r', '\n');
            if (value.IndexOf('\0') >= 0)
            {
                throw new InvalidOperationException("The password file contains an invalid null character.");
            }

            return value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string ResolvePath(string contentRootPath, string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(configuredPath, contentRootPath);
    }

    private static string ReadTextFile(string fullPath)
    {
        ValidateRegularFile(fullPath);
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length is <= 0 or > MaximumSecretFileBytes)
        {
            throw new InvalidOperationException("A configured secret file is empty or exceeds the allowed size.");
        }

        return File.ReadAllText(fullPath, StrictUtf8);
    }

    private static void ValidateRegularFile(string fullPath)
    {
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists || (fileInfo.Attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidOperationException("A configured secret path does not reference a regular file.");
        }

        if (!string.IsNullOrEmpty(fileInfo.LinkTarget)
            || (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Symbolic links are not accepted for configured secret files.");
        }
    }
}
