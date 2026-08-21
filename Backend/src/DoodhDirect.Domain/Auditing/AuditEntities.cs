using DoodhDirect.Domain.Common;

namespace DoodhDirect.Domain.Auditing;

public sealed class AuditLog : Entity
{
    private AuditLog() { }

    public AuditLog(
        long? userId,
        string action,
        string entityType,
        string entityId,
        string? oldValueJson,
        string? newValueJson,
        string? ipAddress,
        string? userAgent,
        string? reason,
        DateTime createdAt)
    {
        if (createdAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Audit timestamps must be India-local DateTime values with an unspecified kind.",
                nameof(createdAt));
        }

        UserId = userId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        OldValueJson = oldValueJson;
        NewValueJson = newValueJson;
        IPAddress = ipAddress;
        UserAgent = userAgent;
        Reason = reason;
        CreatedAt = createdAt;
    }

    public long? UserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string? OldValueJson { get; private set; }
    public string? NewValueJson { get; private set; }
    public string? IPAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public string? Reason { get; private set; }
    public DateTime CreatedAt { get; private set; }
}
