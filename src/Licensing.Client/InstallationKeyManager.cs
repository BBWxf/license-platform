using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Licensing.Client
{
    internal sealed class InstallationKeyManager
    {
        private readonly IInstallationKeyStore _store;
        private readonly ILicenseClock _clock;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private InstallationKeyRecord? _cached;

        internal InstallationKeyManager(IInstallationKeyStore store, ILicenseClock clock)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        internal async Task<InstallationKeyRecord> GetOrCreateAsync(CancellationToken cancellationToken)
        {
            if (_cached != null)
                return _cached;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cached != null)
                    return _cached;

                InstallationKeyRecord? existing;
                try
                {
                    existing = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new LicensingException(LicenseErrorCode.StorageFailure, "Could not load the installation private key.", ex);
                }

                if (existing != null)
                {
                    Validate(existing);
                    _cached = existing;
                    return existing;
                }

                var created = Generate();
                try
                {
                    await _store.SaveAsync(created, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new LicensingException(LicenseErrorCode.StorageFailure, "Could not persist the installation private key.", ex);
                }

                // Reload after saving. A cross-process first-writer-wins store may have kept a key
                // created concurrently by another process instead of the candidate above.
                try
                {
                    var persisted = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
                    if (persisted == null)
                        throw new InvalidOperationException("The installation key store did not return the saved key.");
                    Validate(persisted);
                    _cached = persisted;
                    return persisted;
                }
                catch (OperationCanceledException) { throw; }
                catch (LicensingException) { throw; }
                catch (Exception ex)
                {
                    throw new LicensingException(LicenseErrorCode.StorageFailure, "Could not reload the persisted installation private key.", ex);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private InstallationKeyRecord Generate()
        {
            try
            {
                using (var key = ECDsa.Create())
                {
                    key.GenerateKey(ECCurve.NamedCurves.nistP256);
                    var parameters = key.ExportParameters(true);
                    if (parameters.D == null || parameters.Q.X == null || parameters.Q.Y == null)
                        throw new CryptographicException("The ECDSA provider did not export a complete private key.");
                    var record = new InstallationKeyRecord
                    {
                        Version = 1,
                        InstallationId = Guid.NewGuid().ToString("N"),
                        CreatedAtUtc = _clock.UtcNow,
                        D = parameters.D,
                        X = parameters.Q.X,
                        Y = parameters.Q.Y,
                        KeyId = CryptoUtilities.ToPublicJwkThumbprint(parameters.Q.X, parameters.Q.Y)
                    };
                    Validate(record);
                    return record;
                }
            }
            catch (LicensingException) { throw; }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.KeyGenerationFailed, "Could not generate an ECDSA P-256 installation key.", ex);
            }
        }

        private static void Validate(InstallationKeyRecord record)
        {
            if (record.Version != 1 || string.IsNullOrWhiteSpace(record.InstallationId) ||
                record.D == null || record.X == null || record.Y == null ||
                record.D.Length != 32 || record.X.Length != 32 || record.Y.Length != 32)
                throw new LicensingException(LicenseErrorCode.StorageFailure, "The stored installation key is malformed or unsupported.");

            var expected = CryptoUtilities.ToPublicJwkThumbprint(record.X, record.Y);
            if (!MachineFingerprint.FixedTimeEquals(expected, record.KeyId))
                throw new LicensingException(LicenseErrorCode.StorageFailure, "The stored installation key identifier does not match its public key.");

            try
            {
                using (CryptoUtilities.CreateEcdsa(record)) { }
            }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.StorageFailure, "The stored installation key cannot be imported.", ex);
            }
        }
    }
}
