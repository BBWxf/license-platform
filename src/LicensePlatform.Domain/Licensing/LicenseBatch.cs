using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Products;

namespace LicensePlatform.Domain.Licensing;

public enum LicenseBatchStatus
{
    Draft = 1,
    Issuing = 2,
    Issued = 3,
    Cancelled = 4
}

public sealed class LicenseBatch : Entity
{
    private readonly List<LicenseKey> _licenseKeys = [];

    private LicenseBatch()
    {
    }

    public LicenseBatch(
        Guid id,
        Guid productId,
        string name,
        int quantity,
        LicenseTerm term,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        if (productId == Guid.Empty)
        {
            throw new DomainException("Product id cannot be empty.");
        }

        if (quantity is < 1 or > 1_000_000)
        {
            throw new DomainException("Batch quantity must be between 1 and 1,000,000.");
        }

        ProductId = productId;
        Name = Guard.Required(name, nameof(name), 200);
        Quantity = quantity;
        TermUnit = term.Unit;
        TermValue = term.Value;
        Status = LicenseBatchStatus.Draft;
        CreatedAtUtc = Guard.Utc(createdAtUtc, nameof(createdAtUtc));
    }

    public Guid ProductId { get; private set; }

    public Product? Product { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public int IssuedCount { get; private set; }

    public LicenseTermUnit TermUnit { get; private set; }

    public int TermValue { get; private set; }

    public LicenseBatchStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? IssuedAtUtc { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public IReadOnlyCollection<LicenseKey> LicenseKeys => _licenseKeys.AsReadOnly();

    public LicenseTerm GetTerm() => new(TermUnit, TermValue);

    public void RegisterIssuedLicense(DateTimeOffset nowUtc)
    {
        Guard.Utc(nowUtc, nameof(nowUtc));

        if (Status == LicenseBatchStatus.Cancelled)
        {
            throw new DomainException("A cancelled batch cannot issue licenses.");
        }

        if (IssuedCount >= Quantity)
        {
            throw new DomainException("The batch has already issued its requested quantity.");
        }

        IssuedCount++;
        Status = IssuedCount == Quantity ? LicenseBatchStatus.Issued : LicenseBatchStatus.Issuing;
        if (Status == LicenseBatchStatus.Issued)
        {
            IssuedAtUtc = nowUtc;
        }

        Touch();
    }

    public void Cancel(DateTimeOffset nowUtc)
    {
        if (Status == LicenseBatchStatus.Cancelled)
        {
            return;
        }

        if (IssuedCount != 0)
        {
            throw new DomainException("A batch that has issued licenses cannot be cancelled.");
        }

        Status = LicenseBatchStatus.Cancelled;
        CancelledAtUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        Touch();
    }
}
