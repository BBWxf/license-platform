using LicensePlatform.Domain.Common;

namespace LicensePlatform.Domain.Products;

public sealed class Product : Entity
{
    private readonly List<ProductSigningKey> _signingKeys = [];

    private Product()
    {
    }

    public Product(
        Guid id,
        string code,
        string name,
        string licensePrefix,
        int leaseLifetimeMinutes,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        Code = NormalizeCode(code);
        Name = Guard.Required(name, nameof(name), 200);
        LicensePrefix = NormalizeLicensePrefix(licensePrefix);
        SetLeaseLifetime(leaseLifetimeMinutes);
        CreatedAtUtc = Guard.Utc(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = CreatedAtUtc;
        IsEnabled = true;
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public string LicensePrefix { get; private set; } = string.Empty;

    public int LeaseLifetimeMinutes { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyCollection<ProductSigningKey> SigningKeys => _signingKeys.AsReadOnly();

    public void Rename(string name, DateTimeOffset nowUtc)
    {
        Name = Guard.Required(name, nameof(name), 200);
        MarkUpdated(nowUtc);
    }

    public void ChangeLicensePrefix(string licensePrefix, DateTimeOffset nowUtc)
    {
        var normalized = NormalizeLicensePrefix(licensePrefix);
        if (string.Equals(LicensePrefix, normalized, StringComparison.Ordinal))
        {
            return;
        }

        LicensePrefix = normalized;
        MarkUpdated(nowUtc);
    }

    public void ChangeLeaseLifetime(int leaseLifetimeMinutes, DateTimeOffset nowUtc)
    {
        SetLeaseLifetime(leaseLifetimeMinutes);
        MarkUpdated(nowUtc);
    }

    public void Disable(DateTimeOffset nowUtc)
    {
        if (!IsEnabled)
        {
            return;
        }

        IsEnabled = false;
        MarkUpdated(nowUtc);
    }

    public void Enable(DateTimeOffset nowUtc)
    {
        if (IsEnabled)
        {
            return;
        }

        IsEnabled = true;
        MarkUpdated(nowUtc);
    }

    private static string NormalizeCode(string code)
    {
        var normalized = Guard.Required(code, nameof(code), 64).ToUpperInvariant();
        if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new DomainException("Product code may contain only ASCII letters, digits, '-' and '_'.");
        }

        return normalized;
    }

    private static string NormalizeLicensePrefix(string licensePrefix)
    {
        var normalized = Guard.Required(licensePrefix, nameof(licensePrefix), 12).ToUpperInvariant();
        if (normalized.Length < 2 || normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new DomainException("License prefix must contain 2 to 12 ASCII letters or digits.");
        }

        return normalized;
    }

    private void SetLeaseLifetime(int leaseLifetimeMinutes)
    {
        if (leaseLifetimeMinutes is < 5 or > 43_200)
        {
            throw new DomainException("Lease lifetime must be between 5 minutes and 30 days.");
        }

        LeaseLifetimeMinutes = leaseLifetimeMinutes;
    }

    private void MarkUpdated(DateTimeOffset nowUtc)
    {
        UpdatedAtUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        Touch();
    }
}
