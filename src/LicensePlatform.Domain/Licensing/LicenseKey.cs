using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Products;

namespace LicensePlatform.Domain.Licensing;

public enum LicenseKeyStatus
{
    Enabled = 1,
    Disabled = 2
}

public sealed class LicenseKey : Entity
{
    private readonly List<DeviceBinding> _deviceBindings = [];

    private LicenseKey()
    {
    }

    public LicenseKey(
        Guid id,
        Guid productId,
        Guid batchId,
        byte[] keyHash,
        string keyHint,
        LicenseTerm term,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        if (productId == Guid.Empty || batchId == Guid.Empty)
        {
            throw new DomainException("Product id and batch id are required.");
        }

        ProductId = productId;
        BatchId = batchId;
        KeyHash = Guard.Bytes(keyHash, nameof(keyHash), 32);
        KeyHint = Guard.Required(keyHint, nameof(keyHint), 32);
        TermUnit = term.Unit;
        TermValue = term.Value;
        Status = LicenseKeyStatus.Enabled;
        BindingVersion = 1;
        CreatedAtUtc = Guard.Utc(createdAtUtc, nameof(createdAtUtc));
    }

    public Guid ProductId { get; private set; }

    public Product? Product { get; private set; }

    public Guid BatchId { get; private set; }

    public LicenseBatch? Batch { get; private set; }

    public byte[] KeyHash { get; private set; } = [];

    public string KeyHint { get; private set; } = string.Empty;

    public LicenseTermUnit TermUnit { get; private set; }

    public int TermValue { get; private set; }

    public int AdditionalDays { get; private set; }

    public LicenseKeyStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? ActivatedAtUtc { get; private set; }

    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public DateTimeOffset? DisabledAtUtc { get; private set; }

    public string? DisabledReason { get; private set; }

    public long BindingVersion { get; private set; }

    public IReadOnlyCollection<DeviceBinding> DeviceBindings => _deviceBindings.AsReadOnly();

    public LicenseTerm GetTerm() => new(TermUnit, TermValue);

    public bool IsPermanent => TermUnit == LicenseTermUnit.Permanent;

    public bool IsExpiredAt(DateTimeOffset nowUtc)
    {
        nowUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        return ExpiresAtUtc.HasValue && nowUtc >= ExpiresAtUtc.Value;
    }

    public void Activate(DateTimeOffset nowUtc)
    {
        nowUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        EnsureEnabled();

        if (ActivatedAtUtc.HasValue)
        {
            return;
        }

        ActivatedAtUtc = nowUtc;
        var expiration = GetTerm().CalculateExpiration(nowUtc);
        ExpiresAtUtc = expiration?.AddDays(AdditionalDays);
        Touch();
    }

    public void EnsureCanIssueLease(DateTimeOffset nowUtc)
    {
        nowUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        EnsureEnabled();

        if (!ActivatedAtUtc.HasValue)
        {
            throw new DomainException("The license has not been activated.");
        }

        if (IsExpiredAt(nowUtc))
        {
            throw new DomainException("The license has expired.");
        }
    }

    public void Disable(string reason, DateTimeOffset nowUtc)
    {
        reason = Guard.Required(reason, nameof(reason), 500);
        nowUtc = Guard.Utc(nowUtc, nameof(nowUtc));

        if (Status == LicenseKeyStatus.Disabled)
        {
            if (!string.Equals(DisabledReason, reason, StringComparison.Ordinal))
            {
                DisabledReason = reason;
                DisabledAtUtc = nowUtc;
                Touch();
            }

            return;
        }

        Status = LicenseKeyStatus.Disabled;
        DisabledReason = reason;
        DisabledAtUtc = nowUtc;
        Touch();
    }

    public void Enable(DateTimeOffset nowUtc)
    {
        Guard.Utc(nowUtc, nameof(nowUtc));
        if (Status == LicenseKeyStatus.Enabled)
        {
            return;
        }

        Status = LicenseKeyStatus.Enabled;
        DisabledReason = null;
        DisabledAtUtc = null;
        Touch();
    }

    public void Extend(LicenseTerm extension, DateTimeOffset nowUtc)
    {
        Guard.Utc(nowUtc, nameof(nowUtc));
        if (IsPermanent)
        {
            throw new DomainException("A permanent license cannot be extended.");
        }

        if (extension.IsPermanent)
        {
            throw new DomainException("A finite license cannot be extended with a permanent term.");
        }

        var days = extension.ToDays();
        AdditionalDays = checked(AdditionalDays + days);
        if (ActivatedAtUtc.HasValue)
        {
            var currentExpiration = ExpiresAtUtc ?? ActivatedAtUtc.Value;
            var extensionStartsAt = currentExpiration > nowUtc ? currentExpiration : nowUtc;
            ExpiresAtUtc = extensionStartsAt.AddDays(days);
        }

        Touch();
    }

    public DeviceBinding BindDevice(
        DeviceFingerprintHashes fingerprint,
        string deviceHint,
        string devicePublicKey,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        nowUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        EnsureCanIssueLease(nowUtc);
        var activeBinding = _deviceBindings.SingleOrDefault(binding => binding.IsActive);

        if (activeBinding is not null)
        {
            if (activeBinding.Matches(fingerprint)
                && activeBinding.MatchesPublicKey(devicePublicKey))
            {
                return activeBinding;
            }

            throw new DomainException("The license is already bound to another device.");
        }

        var binding = new DeviceBinding(
            Guid.NewGuid(),
            Id,
            fingerprint,
            deviceHint,
            devicePublicKey,
            BindingVersion,
            nowUtc);
        _deviceBindings.Add(binding);
        Touch();
        return binding;
    }

    public void UnbindDevice(DeviceFingerprintHashes expectedFingerprint, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        nowUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        var activeBinding = _deviceBindings.SingleOrDefault(binding => binding.IsActive)
            ?? throw new DomainException("The license has no active device binding.");

        if (!activeBinding.Matches(expectedFingerprint))
        {
            throw new DomainException("The supplied device does not match the active binding.");
        }

        activeBinding.Unbind(nowUtc);
        BindingVersion = checked(BindingVersion + 1);
        Touch();
    }

    private void EnsureEnabled()
    {
        if (Status != LicenseKeyStatus.Enabled)
        {
            throw new DomainException("The license is disabled.");
        }
    }
}
