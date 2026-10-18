using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Licensing.Client.Windows
{
    /// <summary>Stores the exportable P-256 installation key under current-user DPAPI protection.</summary>
    public sealed class DpapiInstallationKeyStore : IInstallationKeyStore
    {
        private readonly DpapiAtomicFile _file;

        public DpapiInstallationKeyStore(WindowsStorageOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _file = new DpapiAtomicFile(
                Path.Combine(options.ResolveDirectory(), "installation-key.v1.bin"),
                options.ApplicationName + ":installation-key:v1");
        }

        public Task<InstallationKeyRecord?> LoadAsync(CancellationToken cancellationToken) =>
            _file.LoadAsync<InstallationKeyRecord>(cancellationToken);

        public async Task SaveAsync(InstallationKeyRecord record, CancellationToken cancellationToken)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            // First writer wins so concurrent processes cannot replace an established device identity.
            await _file.SaveAsync(record, createOnly: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stores lease and trusted-time state under current-user DPAPI protection using atomic replacement.</summary>
    public sealed class DpapiLicenseStateStore : ILicenseStateStore
    {
        private readonly DpapiAtomicFile _file;

        public DpapiLicenseStateStore(WindowsStorageOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _file = new DpapiAtomicFile(
                Path.Combine(options.ResolveDirectory(), "lease-cache.v1.bin"),
                options.ApplicationName + ":lease-cache:v1");
        }

        public Task<LicenseStateRecord?> LoadAsync(CancellationToken cancellationToken) =>
            _file.LoadAsync<LicenseStateRecord>(cancellationToken);

        public Task SaveAsync(LicenseStateRecord record, CancellationToken cancellationToken)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return _file.UpdateAsync<LicenseStateRecord>(existing => Merge(existing, record), cancellationToken);
        }

        public Task ClearAsync(CancellationToken cancellationToken) => _file.DeleteAsync(cancellationToken);

        private static LicenseStateRecord Merge(LicenseStateRecord? existing, LicenseStateRecord incoming)
        {
            if (existing == null || !string.Equals(existing.LicenseKeyHash, incoming.LicenseKeyHash, StringComparison.Ordinal))
                return incoming;

            if (existing.LastOnlineUtc > incoming.LastOnlineUtc)
            {
                // Do not let a stale process replace a newer online lease or lower its time anchors.
                existing.TrustedUtc = Max(existing.TrustedUtc, incoming.TrustedUtc);
                existing.LastObservedUtc = Max(existing.LastObservedUtc, incoming.LastObservedUtc);
                return existing;
            }

            if (existing.LastOnlineUtc == incoming.LastOnlineUtc &&
                string.Equals(existing.LeaseToken, incoming.LeaseToken, StringComparison.Ordinal))
            {
                incoming.TrustedUtc = Max(existing.TrustedUtc, incoming.TrustedUtc);
                incoming.LastObservedUtc = Max(existing.LastObservedUtc, incoming.LastObservedUtc);
            }
            else
            {
                // A newly authenticated lease may intentionally repair a poisoned local observation,
                // but authenticated trusted time itself is monotonic.
                incoming.TrustedUtc = Max(existing.TrustedUtc, incoming.TrustedUtc);
            }
            return incoming;
        }

        private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
    }

    internal sealed class DpapiAtomicFile
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LCSDK1\0");
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private readonly string _path;
        private readonly byte[] _entropy;
        private readonly SemaphoreSlim _gate;
        private readonly Mutex _processMutex;
        private readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            DateParseHandling = DateParseHandling.DateTimeOffset
        };

        internal DpapiAtomicFile(string path, string purpose)
        {
            _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
            byte[] pathHash;
            using (var sha = SHA256.Create())
            {
                _entropy = sha.ComputeHash(Encoding.UTF8.GetBytes("Licensing.Client.Windows\n" + purpose));
                pathHash = sha.ComputeHash(Encoding.UTF8.GetBytes(_path.ToUpperInvariant()));
            }
            var mutexName = "Local\\LicensingSdk-" + Convert.ToBase64String(pathHash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _processMutex = new Mutex(initiallyOwned: false, name: mutexName);
            _gate = Gates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
        }

        internal async Task<T?> LoadAsync<T>(CancellationToken cancellationToken) where T : class
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (AcquireProcessLock(cancellationToken))
                    return LoadCore<T>();
            }
            finally
            {
                _gate.Release();
            }
        }

        internal async Task SaveAsync<T>(T value, bool createOnly, CancellationToken cancellationToken) where T : class
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (AcquireProcessLock(cancellationToken))
                    SaveCore(value, createOnly);
            }
            finally
            {
                _gate.Release();
            }
        }

        internal async Task UpdateAsync<T>(Func<T?, T> update, CancellationToken cancellationToken) where T : class
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (AcquireProcessLock(cancellationToken))
                {
                    var value = update(LoadCore<T>());
                    if (value == null)
                        throw new InvalidOperationException("The protected licensing update returned null.");
                    SaveCore(value, createOnly: false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        internal async Task DeleteAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (AcquireProcessLock(cancellationToken))
                {
                    if (File.Exists(_path))
                        File.Delete(_path);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private T? LoadCore<T>() where T : class
        {
            if (!File.Exists(_path))
                return null;

            var envelope = File.ReadAllBytes(_path);
            if (envelope.Length <= Magic.Length || !HasMagic(envelope))
                throw new CryptographicException("The protected licensing file has an invalid header.");
            var protectedBytes = new byte[envelope.Length - Magic.Length];
            Buffer.BlockCopy(envelope, Magic.Length, protectedBytes, 0, protectedBytes.Length);
            byte[]? plaintext = null;
            try
            {
                plaintext = ProtectedData.Unprotect(protectedBytes, _entropy, DataProtectionScope.CurrentUser);
                var value = JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(plaintext), _jsonSettings);
                if (value == null)
                    throw new InvalidDataException("The protected licensing file contains no object.");
                return value;
            }
            finally
            {
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
                if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
                Array.Clear(envelope, 0, envelope.Length);
            }
        }

        private void SaveCore<T>(T value, bool createOnly) where T : class
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("The licensing storage path has no directory.");
            Directory.CreateDirectory(directory);

            var plaintext = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.None, _jsonSettings));
            byte[]? protectedBytes = null;
            byte[]? envelope = null;
            try
            {
                protectedBytes = ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);
                envelope = new byte[Magic.Length + protectedBytes.Length];
                Buffer.BlockCopy(Magic, 0, envelope, 0, Magic.Length);
                Buffer.BlockCopy(protectedBytes, 0, envelope, Magic.Length, protectedBytes.Length);
                AtomicWrite(envelope, createOnly);
            }
            finally
            {
                Array.Clear(plaintext, 0, plaintext.Length);
                if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
                if (envelope != null) Array.Clear(envelope, 0, envelope.Length);
            }
        }

        private IDisposable AcquireProcessLock(CancellationToken cancellationToken)
        {
            var acquired = false;
            try
            {
                int index;
                try
                {
                    index = WaitHandle.WaitAny(new WaitHandle[] { _processMutex, cancellationToken.WaitHandle });
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                    return new MutexReleaser(_processMutex);
                }
                if (index != 0)
                    throw new OperationCanceledException(cancellationToken);
                acquired = true;
                return new MutexReleaser(_processMutex);
            }
            catch
            {
                if (acquired) _processMutex.ReleaseMutex();
                throw;
            }
        }

        private void AtomicWrite(byte[] bytes, bool createOnly)
        {
            var directory = Path.GetDirectoryName(_path)!;
            var temporary = Path.Combine(directory, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                var flags = MoveFileFlags.WriteThrough | (createOnly ? MoveFileFlags.None : MoveFileFlags.ReplaceExisting);
                if (!MoveFileEx(temporary, _path, flags))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (createOnly && (error == 80 || error == 183))
                        return;
                    throw new Win32Exception(error, "Could not atomically commit the protected licensing file.");
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }

        private static bool HasMagic(byte[] envelope)
        {
            var difference = 0;
            for (var i = 0; i < Magic.Length; i++)
                difference |= envelope[i] ^ Magic[i];
            return difference == 0;
        }

        private sealed class MutexReleaser : IDisposable
        {
            private Mutex? _mutex;
            internal MutexReleaser(Mutex mutex) => _mutex = mutex;
            public void Dispose()
            {
                var mutex = Interlocked.Exchange(ref _mutex, null);
                if (mutex != null) mutex.ReleaseMutex();
            }
        }

        [Flags]
        private enum MoveFileFlags : uint
        {
            None = 0,
            ReplaceExisting = 0x1,
            WriteThrough = 0x8
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, MoveFileFlags flags);
    }
}
