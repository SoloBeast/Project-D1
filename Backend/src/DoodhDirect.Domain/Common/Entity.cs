namespace DoodhDirect.Domain.Common;

public interface IAuditableEntity
{
    DateTime CreatedAtUtc { get; }

    DateTime UpdatedAtUtc { get; }
}

public abstract class Entity
{
    public long Id { get; protected set; }
}

public abstract class PublicEntity : Entity
{
    public Guid PublicId { get; protected set; } = Guid.NewGuid();
}

public abstract class AuditableEntity : PublicEntity, IAuditableEntity
{
    public DateTime CreatedAtUtc { get; protected set; }

    public DateTime UpdatedAtUtc { get; protected set; }

    public void SetCreated(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(utcNow));
        }

        CreatedAtUtc = utcNow;
        UpdatedAtUtc = utcNow;
    }

    public void SetUpdated(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(utcNow));
        }

        UpdatedAtUtc = utcNow;
    }
}
