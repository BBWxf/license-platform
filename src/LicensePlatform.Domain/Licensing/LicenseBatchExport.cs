using LicensePlatform.Domain.Common;

namespace LicensePlatform.Domain.Licensing;

public sealed class LicenseBatchExport : Entity
{
    private LicenseBatchExport()
    {
    }

    public LicenseBatchExport(
        Guid id,
        Guid batchId,
        byte[] downloadTokenHash,
        byte[] protectedPayload,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc)
        : base(id)
    {
        if (batchId == Guid.Empty)
        {
            throw new DomainException("Batch id cannot be empty.");
        }

        CreatedAtUtc = Guard.Utc(createdAtUtc, nameof(createdAtUtc));
        ExpiresAtUtc = expiresAtUtc.HasValue
            ? Guard.Utc(expiresAtUtc.Value, nameof(expiresAtUtc))
            : null;
        if (ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= CreatedAtUtc)
        {
            throw new DomainException("Export expiration must be after its creation time.");
        }

        BatchId = batchId;
        DownloadTokenHash = Guard.Bytes(downloadTokenHash, nameof(downloadTokenHash), 32);
        ProtectedPayload = Guard.Bytes(protectedPayload, nameof(protectedPayload));
    }

    public Guid BatchId { get; private set; }

    public LicenseBatch? Batch { get; private set; }

    public byte[] DownloadTokenHash { get; private set; } = [];

    public byte[] ProtectedPayload { get; private set; } = [];

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public bool IsConsumed => ConsumedAtUtc.HasValue;

    public void Consume(DateTimeOffset nowUtc)
    {
        if (!ExpiresAtUtc.HasValue)
        {
            throw new DomainException("A durable export cannot be consumed.");
        }

        nowUtc = Guard.Utc(nowUtc, nameof(nowUtc));

        if (ConsumedAtUtc.HasValue)
        {
            throw new DomainException("This export has already been consumed.");
        }

        if (nowUtc >= ExpiresAtUtc.Value)
        {
            throw new DomainException("This export has expired.");
        }

        ConsumedAtUtc = nowUtc;
        Touch();
    }

    public void ErasePayload()
    {
        if (!ExpiresAtUtc.HasValue)
        {
            throw new DomainException("A durable export payload cannot be erased.");
        }

        if (!ConsumedAtUtc.HasValue)
        {
            throw new DomainException("An unconsumed export payload cannot be erased.");
        }

        if (ProtectedPayload.Length == 0)
        {
            return;
        }

        ProtectedPayload = [];
        Touch();
    }
}
