using System;
using System.Threading;
using System.Threading.Tasks;

namespace Licensing.Client
{
    /// <summary>Persists the installation's ECDSA P-256 private key.</summary>
    public interface IInstallationKeyStore
    {
        Task<InstallationKeyRecord?> LoadAsync(CancellationToken cancellationToken);
        Task SaveAsync(InstallationKeyRecord record, CancellationToken cancellationToken);
    }

    /// <summary>Persists the signed lease and trusted-time metadata.</summary>
    public interface ILicenseStateStore
    {
        Task<LicenseStateRecord?> LoadAsync(CancellationToken cancellationToken);
        Task SaveAsync(LicenseStateRecord record, CancellationToken cancellationToken);
        Task ClearAsync(CancellationToken cancellationToken);
    }

    /// <summary>Supplies a privacy-preserving, versioned device fingerprint.</summary>
    public interface IMachineFingerprintProvider
    {
        Task<MachineFingerprint> GetAsync(CancellationToken cancellationToken);
    }

    public interface ILicenseClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class SystemLicenseClock : ILicenseClock
    {
        public static readonly SystemLicenseClock Instance = new SystemLicenseClock();
        private SystemLicenseClock() { }
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
