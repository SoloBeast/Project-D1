namespace DoodhDirect.Domain.Common;

public interface IAuditableEntity
{
    DateTime CreatedAt { get; }

    DateTime UpdatedAt { get; }
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
    public DateTime CreatedAt { get; protected set; }

    public DateTime UpdatedAt { get; protected set; }

    public void SetCreated(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        CreatedAt = indiaLocalNow;
        UpdatedAt = indiaLocalNow;
    }

    public void SetUpdated(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        UpdatedAt = indiaLocalNow;
    }

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Timestamp must be an India-local wall-clock value.",
                parameterName);
        }
    }
}
