using LicensePlatform.Domain.Common;

namespace LicensePlatform.Domain.Licensing;

public sealed class DeviceBinding : Entity
{
    private DeviceBinding()
    {
    }

    internal DeviceBinding(
        Guid id,
        Guid licenseKeyId,
        DeviceFingerprintHashes fingerprint,
        string deviceHint,
        string devicePublicKey,
        long bindingVersion,
        DateTimeOffset boundAtUtc)
        : base(id)
    {
        if (licenseKeyId == Guid.Empty)
        {
            throw new DomainException("License key id cannot be empty.");
        }

        if (bindingVersion < 1)
        {
            throw new DomainException("Binding version must be positive.");
        }

        LicenseKeyId = licenseKeyId;
        ArgumentNullException.ThrowIfNull(fingerprint);
        MachineGuidHash = fingerprint.MachineGuidHash.ToArray();
        SmbiosHash = fingerprint.SmbiosHash.ToArray();
        SystemVolumeHash = fingerprint.SystemVolumeHash.ToArray();
        DeviceHint = Guard.Required(deviceHint, nameof(deviceHint), 128);
        DevicePublicKey = Guard.Required(devicePublicKey, nameof(devicePublicKey), 2_048);
        BindingVersion = bindingVersion;
        BoundAtUtc = Guard.Utc(boundAtUtc, nameof(boundAtUtc));
    }

    public Guid LicenseKeyId { get; private set; }

    public LicenseKey? LicenseKey { get; private set; }

    public byte[] MachineGuidHash { get; private set; } = [];

    public byte[] SmbiosHash { get; private set; } = [];

    public byte[] SystemVolumeHash { get; private set; } = [];

    public string DeviceHint { get; private set; } = string.Empty;

    public string DevicePublicKey { get; private set; } = string.Empty;

    public long BindingVersion { get; private set; }

    public DateTimeOffset BoundAtUtc { get; private set; }

    public DateTimeOffset? UnboundAtUtc { get; private set; }

    public bool IsActive => !UnboundAtUtc.HasValue;

    public bool Matches(DeviceFingerprintHashes fingerprint, int requiredMatches = 2)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return GetFingerprint().MatchesAtLeast(fingerprint, requiredMatches);
    }

    public DeviceFingerprintHashes GetFingerprint()
    {
        return new DeviceFingerprintHashes(MachineGuidHash, SmbiosHash, SystemVolumeHash);
    }

    public bool MatchesPublicKey(string devicePublicKey)
    {
        return string.Equals(DevicePublicKey, devicePublicKey?.Trim(), StringComparison.Ordinal);
    }

    internal void Unbind(DateTimeOffset nowUtc)
    {
        if (UnboundAtUtc.HasValue)
        {
            throw new DomainException("The device binding has already been released.");
        }

        UnboundAtUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        Touch();
    }
}
