using LicensePlatform.Domain.Common;

namespace LicensePlatform.Domain.Auditing;

public enum AuditActorType
{
    System = 1,
    Administrator = 2,
    LicenseClient = 3
}

public sealed class AuditRecord : Entity
{
    private AuditRecord()
    {
    }

    public AuditRecord(
        Guid id,
        AuditActorType actorType,
        string? actorId,
        string action,
        string entityType,
        string entityId,
        string dataJson,
        string? traceId,
        string? ipAddress,
        DateTimeOffset occurredAtUtc)
        : base(id)
    {
        if (!Enum.IsDefined(actorType))
        {
            throw new DomainException("Audit actor type is invalid.");
        }

        ActorType = actorType;
        ActorId = string.IsNullOrWhiteSpace(actorId) ? null : Guard.Required(actorId, nameof(actorId), 200);
        Action = Guard.Required(action, nameof(action), 128);
        EntityType = Guard.Required(entityType, nameof(entityType), 128);
        EntityId = Guard.Required(entityId, nameof(entityId), 200);
        DataJson = Guard.Required(dataJson, nameof(dataJson), 1_000_000);
        TraceId = string.IsNullOrWhiteSpace(traceId) ? null : Guard.Required(traceId, nameof(traceId), 128);
        IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? null : Guard.Required(ipAddress, nameof(ipAddress), 64);
        OccurredAtUtc = Guard.Utc(occurredAtUtc, nameof(occurredAtUtc));
    }

    public AuditActorType ActorType { get; private set; }

    public string? ActorId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public string EntityId { get; private set; } = string.Empty;

    public string DataJson { get; private set; } = "{}";

    public string? TraceId { get; private set; }

    public string? IpAddress { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }
}
