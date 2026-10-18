namespace LicensePlatform.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; }

    public long Version { get; protected set; }

    protected Entity()
    {
    }

    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("Entity id cannot be empty.");
        }

        Id = id;
        Version = 1;
    }

    protected void Touch()
    {
        Version = checked(Version + 1);
    }
}
