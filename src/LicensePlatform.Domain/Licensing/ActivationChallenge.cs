using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Products;

namespace LicensePlatform.Domain.Licensing;

public enum ActivationChallengePurpose
{
    Activate = 1,
    RefreshLease = 2,
    Unbind = 3
}

public sealed class ActivationChallenge : Entity
{
    private ActivationChallenge()
    {
    }

    public ActivationChallenge(
        Guid id,
        Guid productId,
        Guid licenseKeyId,
        byte[] challengeHash,
        DeviceFingerprintHashes fingerprint,
        byte[] nonce,
        string deviceHint,
        string devicePublicKey,
        ActivationChallengePurpose purpose,
        long bindingVersion,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
        : base(id)
    {
        if (productId == Guid.Empty || licenseKeyId == Guid.Empty)
        {
            throw new DomainException("Product id and license key id are required.");
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new DomainException("Challenge purpose is invalid.");
        }

        if (bindingVersion < 1)
        {
            throw new DomainException("Binding version must be positive.");
        }

        CreatedAtUtc = Guard.Utc(createdAtUtc, nameof(createdAtUtc));
        ExpiresAtUtc = Guard.Utc(expiresAtUtc, nameof(expiresAtUtc));
        if (ExpiresAtUtc <= CreatedAtUtc)
        {
            throw new DomainException("Challenge expiration must be after its creation time.");
        }

        ProductId = productId;
        LicenseKeyId = licenseKeyId;
        ArgumentNullException.ThrowIfNull(fingerprint);
        ChallengeHash = Guard.Bytes(challengeHash, nameof(challengeHash), 32);
        MachineGuidHash = fingerprint.MachineGuidHash.ToArray();
        SmbiosHash = fingerprint.SmbiosHash.ToArray();
        SystemVolumeHash = fingerprint.SystemVolumeHash.ToArray();
        Nonce = Guard.Bytes(nonce, nameof(nonce), 32);
        DeviceHint = Guard.Required(deviceHint, nameof(deviceHint), 128);
        DevicePublicKey = Guard.Required(devicePublicKey, nameof(devicePublicKey), 2_048);
        Purpose = purpose;
        BindingVersion = bindingVersion;
    }

    public Guid ProductId { get; private set; }

    public Product? Product { get; private set; }

    public Guid LicenseKeyId { get; private set; }

    public LicenseKey? LicenseKey { get; private set; }

    public byte[] ChallengeHash { get; private set; } = [];

    public byte[] MachineGuidHash { get; private set; } = [];

    public byte[] SmbiosHash { get; private set; } = [];

    public byte[] SystemVolumeHash { get; private set; } = [];

    public byte[] Nonce { get; private set; } = [];

    public string DeviceHint { get; private set; } = string.Empty;

    public string DevicePublicKey { get; private set; } = string.Empty;

    public ActivationChallengePurpose Purpose { get; private set; }

    public long BindingVersion { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public DeviceFingerprintHashes GetFingerprint()
    {
        return new DeviceFingerprintHashes(MachineGuidHash, SmbiosHash, SystemVolumeHash);
    }

    public bool MatchesFingerprint(DeviceFingerprintHashes fingerprint, int requiredMatches = 3)
    {
        return GetFingerprint().MatchesAtLeast(fingerprint, requiredMatches);
    }

    public void Consume(long currentBindingVersion, DateTimeOffset nowUtc)
    {
        nowUtc = Guard.Utc(nowUtc, nameof(nowUtc));

        if (ConsumedAtUtc.HasValue)
        {
            throw new DomainException("The challenge has already been consumed.");
        }

        if (nowUtc >= ExpiresAtUtc)
        {
            throw new DomainException("The challenge has expired.");
        }

        if (currentBindingVersion != BindingVersion)
        {
            throw new DomainException("The challenge was invalidated by a binding version change.");
        }

        ConsumedAtUtc = nowUtc;
        Touch();
    }
}
