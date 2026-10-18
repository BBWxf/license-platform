using System.Security.Cryptography;
using System.Text;
using Licensing.Client;
using Licensing.Client.Windows;
using Xunit;

namespace Client.Tests;

public sealed class WindowsStorageTests
{
    [Fact]
    public async Task DpapiKeyStoreIsProtectedAndFirstWriterWins()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new DpapiInstallationKeyStore(new WindowsStorageOptions("Client.Tests") { DirectoryPath = directory });
            var first = Key("first", 1);
            var second = Key("second", 2);

            await store.SaveAsync(first, CancellationToken.None);
            await store.SaveAsync(second, CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal("first", loaded!.InstallationId);
            var fileBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "installation-key.v1.bin"));
            Assert.DoesNotContain(Convert.ToBase64String(first.D), Convert.ToBase64String(fileBytes));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task DpapiLeaseStoreAtomicallyReplacesAndClearsState()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new DpapiLicenseStateStore(new WindowsStorageOptions("Client.Tests") { DirectoryPath = directory });
            await store.SaveAsync(State("lease-one"), CancellationToken.None);
            await store.SaveAsync(State("lease-two"), CancellationToken.None);

            Assert.Equal("lease-two", (await store.LoadAsync(CancellationToken.None))!.LeaseToken);
            await store.ClearAsync(CancellationToken.None);
            Assert.Null(await store.LoadAsync(CancellationToken.None));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Theory]
    [InlineData(false, "header")]
    [InlineData(false, "truncated")]
    [InlineData(false, "ciphertext")]
    [InlineData(true, "header")]
    [InlineData(true, "truncated")]
    [InlineData(true, "ciphertext")]
    public async Task CorruptProtectedFilesFailClosedWithoutOverwriting(bool installationKey, string corruption)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var options = new WindowsStorageOptions("Client.Tests") { DirectoryPath = directory };
            var keys = new DpapiInstallationKeyStore(options);
            var leases = new DpapiLicenseStateStore(options);
            if (installationKey)
                await keys.SaveAsync(Key("first", 1), CancellationToken.None);
            else
                await leases.SaveAsync(State("signed-lease"), CancellationToken.None);
            var path = Path.Combine(directory, installationKey ? "installation-key.v1.bin" : "lease-cache.v1.bin");
            var bytes = await File.ReadAllBytesAsync(path);
            switch (corruption)
            {
                case "header": bytes[0] ^= 0xff; break;
                case "truncated": bytes = bytes[..7]; break;
                case "ciphertext": bytes[^1] ^= 0xff; break;
                default: throw new ArgumentException("Unknown corruption", nameof(corruption));
            }
            await File.WriteAllBytesAsync(path, bytes);

            if (installationKey)
            {
                await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.LoadAsync(CancellationToken.None));
                await keys.SaveAsync(Key("replacement", 2), CancellationToken.None);
                await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.LoadAsync(CancellationToken.None));
            }
            else
            {
                await Assert.ThrowsAnyAsync<CryptographicException>(() => leases.LoadAsync(CancellationToken.None));
                await Assert.ThrowsAnyAsync<CryptographicException>(() => leases.SaveAsync(State("replacement"), CancellationToken.None));
            }
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            if (!installationKey)
            {
                await leases.ClearAsync(CancellationToken.None);
                Assert.Null(await leases.LoadAsync(CancellationToken.None));
                await leases.SaveAsync(State("recovered-online"), CancellationToken.None);
                Assert.Equal("recovered-online", (await leases.LoadAsync(CancellationToken.None))!.LeaseToken);
            }
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task ProtectedStateCannotBeReadUnderDifferentApplicationPurpose()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var original = new DpapiLicenseStateStore(new WindowsStorageOptions("Client.Tests") { DirectoryPath = directory });
            var other = new DpapiLicenseStateStore(new WindowsStorageOptions("Other.Product") { DirectoryPath = directory });
            await original.SaveAsync(State("signed-lease"), CancellationToken.None);

            await Assert.ThrowsAnyAsync<CryptographicException>(() => other.LoadAsync(CancellationToken.None));

            Assert.Equal("signed-lease", (await original.LoadAsync(CancellationToken.None))!.LeaseToken);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{invalid-json")]
    public async Task AuthenticatedButInvalidStateFailsClosed(string plaintext)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new DpapiLicenseStateStore(new WindowsStorageOptions("Client.Tests") { DirectoryPath = directory });
            var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("Licensing.Client.Windows\nClient.Tests:lease-cache:v1"));
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), entropy, DataProtectionScope.CurrentUser);
            var bytes = Encoding.ASCII.GetBytes("LCSDK1\0").Concat(encrypted).ToArray();
            var path = Path.Combine(directory, "lease-cache.v1.bin");
            await File.WriteAllBytesAsync(path, bytes);

            if (plaintext == "null")
                await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
            else
                await Assert.ThrowsAsync<Newtonsoft.Json.JsonReaderException>(() => store.LoadAsync(CancellationToken.None));

            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task StaleProcessCannotReplaceNewerLeaseOrLowerTimeAnchors()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var options = new WindowsStorageOptions("Client.Tests") { DirectoryPath = directory };
            var firstProcess = new DpapiLicenseStateStore(options);
            var staleProcess = new DpapiLicenseStateStore(options);
            var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
            var newer = State("newer");
            newer.LastOnlineUtc = now;
            newer.TrustedUtc = now.AddMinutes(30);
            newer.LastObservedUtc = now.AddHours(1);
            await firstProcess.SaveAsync(newer, CancellationToken.None);
            var stale = State("older");
            stale.LastOnlineUtc = now.AddHours(-1);
            stale.TrustedUtc = now.AddHours(-1);
            stale.LastObservedUtc = now.AddHours(-1);

            await staleProcess.SaveAsync(stale, CancellationToken.None);
            var loaded = (await firstProcess.LoadAsync(CancellationToken.None))!;

            Assert.Equal("newer", loaded.LeaseToken);
            Assert.Equal(newer.LastOnlineUtc, loaded.LastOnlineUtc);
            Assert.Equal(newer.TrustedUtc, loaded.TrustedUtc);
            Assert.Equal(newer.LastObservedUtc, loaded.LastObservedUtc);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task SameLeaseObservationCannotRollBackPersistedClockButNewLeaseCanRepairIt()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var options = new WindowsStorageOptions("Client.Tests") { DirectoryPath = directory };
            var firstProcess = new DpapiLicenseStateStore(options);
            var secondProcess = new DpapiLicenseStateStore(options);
            var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
            var original = State("same-lease");
            original.LastOnlineUtc = now;
            original.TrustedUtc = now.AddHours(1);
            original.LastObservedUtc = now.AddYears(1);
            await firstProcess.SaveAsync(original, CancellationToken.None);
            var observation = State("same-lease");
            observation.LastOnlineUtc = now;
            observation.TrustedUtc = now;
            observation.LastObservedUtc = now;

            await secondProcess.SaveAsync(observation, CancellationToken.None);
            var preserved = (await firstProcess.LoadAsync(CancellationToken.None))!;
            Assert.Equal(original.TrustedUtc, preserved.TrustedUtc);
            Assert.Equal(original.LastObservedUtc, preserved.LastObservedUtc);

            var refreshed = State("new-authenticated-lease");
            refreshed.LastOnlineUtc = now.AddHours(2);
            refreshed.TrustedUtc = now.AddHours(2);
            refreshed.LastObservedUtc = now.AddHours(2);
            await secondProcess.SaveAsync(refreshed, CancellationToken.None);
            var repaired = (await firstProcess.LoadAsync(CancellationToken.None))!;
            Assert.Equal(refreshed.TrustedUtc, repaired.TrustedUtc);
            Assert.Equal(refreshed.LastObservedUtc, repaired.LastObservedUtc);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static InstallationKeyRecord Key(string installationId, byte fill) => new()
    {
        InstallationId = installationId,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        D = Enumerable.Repeat(fill, 32).ToArray(),
        X = Enumerable.Repeat((byte)(fill + 1), 32).ToArray(),
        Y = Enumerable.Repeat((byte)(fill + 2), 32).ToArray(),
        KeyId = "key-" + installationId
    };

    private static LicenseStateRecord State(string token) => new()
    {
        LeaseToken = token,
        LicenseKeyHash = "hash",
        LastOnlineUtc = DateTimeOffset.UtcNow,
        TrustedUtc = DateTimeOffset.UtcNow,
        LastObservedUtc = DateTimeOffset.UtcNow
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Licensing.Client.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
