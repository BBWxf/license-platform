using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace LicensePlatform.Web.Security;

public interface IAdminPasswordHasher
{
    Task<string> HashAsync(string password, CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(string encodedHash, string password, CancellationToken cancellationToken = default);
}

public sealed class AdminPasswordHasher : IAdminPasswordHasher
{
    private const int MemorySizeKiB = 65_536;
    private const int Iterations = 3;
    private const int Parallelism = 2;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    public async Task<string> HashAsync(string password, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var hash = await DeriveAsync(passwordBytes, salt, MemorySizeKiB, Iterations, Parallelism, cancellationToken);
            return $"$argon2id$v=19$m={MemorySizeKiB},t={Iterations},p={Parallelism}" +
                   $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public async Task<bool> VerifyAsync(
        string encodedHash,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(encodedHash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        if (!TryParse(encodedHash, out var memory, out var iterations, out var parallelism, out var salt, out var expected))
        {
            return false;
        }

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var actual = await DeriveAsync(passwordBytes, salt, memory, iterations, parallelism, cancellationToken);
            try
            {
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static async Task<byte[]> DeriveAsync(
        byte[] password,
        byte[] salt,
        int memory,
        int iterations,
        int parallelism,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var argon2 = new Argon2id(password)
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        var result = await argon2.GetBytesAsync(HashLength);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static bool TryParse(
        string encodedHash,
        out int memory,
        out int iterations,
        out int parallelism,
        out byte[] salt,
        out byte[] hash)
    {
        memory = 0;
        iterations = 0;
        parallelism = 0;
        salt = [];
        hash = [];

        try
        {
            var parts = encodedHash.Split('$');
            if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id" || parts[2] != "v=19")
            {
                return false;
            }

            var parameters = parts[3].Split(',');
            if (parameters.Length != 3
                || !parameters[0].StartsWith("m=", StringComparison.Ordinal)
                || !parameters[1].StartsWith("t=", StringComparison.Ordinal)
                || !parameters[2].StartsWith("p=", StringComparison.Ordinal)
                || !int.TryParse(parameters[0].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out memory)
                || !int.TryParse(parameters[1].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
                || !int.TryParse(parameters[2].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out parallelism)
                || memory is < 8_192 or > 262_144
                || iterations is < 1 or > 10
                || parallelism is < 1 or > 8)
            {
                return false;
            }

            salt = Convert.FromBase64String(parts[4]);
            hash = Convert.FromBase64String(parts[5]);
            return salt.Length is >= 16 and <= 64 && hash.Length == HashLength;
        }
        catch (FormatException)
        {
            salt = [];
            hash = [];
            return false;
        }
    }

    private static void ValidatePassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length is < 14 or > 512)
        {
            throw new ArgumentException("Administrator password must contain between 14 and 512 characters.", nameof(password));
        }
    }
}
