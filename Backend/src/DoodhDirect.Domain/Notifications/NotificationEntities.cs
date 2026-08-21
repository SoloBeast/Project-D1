using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Domain.Notifications;

public enum NotificationChannel
{
    Push = 1,
    Sms = 2,
    WhatsApp = 3,
    Email = 4
}

public enum NotificationEventStatus
{
    Pending = 1,
    Processing = 2,
    Processed = 3,
    Failed = 4
}

public enum NotificationDeliveryStatus
{
    Pending = 1,
    Processing = 2,
    Delivered = 3,
    RetryScheduled = 4,
    Failed = 5,
    Suppressed = 6,
    Unconfigured = 7
}

public enum NotificationAttemptOutcome
{
    Delivered = 1,
    RetryableFailure = 2,
    PermanentFailure = 3,
    Unconfigured = 4
}

public sealed class NotificationEvent : AuditableEntity
{
    private NotificationEvent() { }

    public NotificationEvent(
        long userId,
        string eventType,
        string eventKey,
        string payloadJson,
        bool isCritical,
        DateTime occurredAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        UserId = userId;
        EventType = NormalizeCode(eventType, nameof(eventType));
        EventKey = NormalizeRequired(eventKey, nameof(eventKey));
        PayloadJson = NormalizeRequired(payloadJson, nameof(payloadJson));
        IsCritical = isCritical;
        OccurredAt = EnsureIndiaLocal(occurredAt, nameof(occurredAt));
        Status = NotificationEventStatus.Pending;
    }

    public long UserId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string EventKey { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public bool IsCritical { get; private set; }
    public NotificationEventStatus Status { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }

    public User User { get; private set; } = null!;
    public ICollection<Notification> Notifications { get; private set; } = [];

    public void StartProcessing()
    {
        if (Status is NotificationEventStatus.Processed or NotificationEventStatus.Failed)
        {
            return;
        }

        Status = NotificationEventStatus.Processing;
        FailureCode = null;
        FailureMessage = null;
    }

    public void Complete(DateTime processedAt)
    {
        Status = NotificationEventStatus.Processed;
        ProcessedAt = EnsureIndiaLocal(processedAt, nameof(processedAt));
        FailureCode = null;
        FailureMessage = null;
    }

    public void Fail(string failureCode, string failureMessage, DateTime processedAt)
    {
        Status = NotificationEventStatus.Failed;
        FailureCode = NormalizeCode(failureCode, nameof(failureCode));
        FailureMessage = NormalizeRequired(failureMessage, nameof(failureMessage));
        ProcessedAt = EnsureIndiaLocal(processedAt, nameof(processedAt));
    }

    internal static string NormalizeCode(string value, string parameterName) =>
        NormalizeRequired(value, parameterName).ToUpperInvariant();

    internal static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    internal static DateTime EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("The timestamp must be India-local with an unspecified DateTime kind.", parameterName);
        }

        return value;
    }
}

public sealed class Notification : AuditableEntity
{
    private Notification() { }

    public Notification(
        long notificationEventId,
        long userId,
        string eventType,
        string title,
        string body,
        string? deepLink,
        DateTime createdAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(notificationEventId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        NotificationEventId = notificationEventId;
        UserId = userId;
        EventType = NotificationEvent.NormalizeCode(eventType, nameof(eventType));
        Title = NotificationEvent.NormalizeRequired(title, nameof(title));
        Body = NotificationEvent.NormalizeRequired(body, nameof(body));
        DeepLink = NormalizeOptional(deepLink);
        CreatedAt = NotificationEvent.EnsureIndiaLocal(createdAt, nameof(createdAt));
    }

    public long NotificationEventId { get; private set; }
    public long UserId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public string? DeepLink { get; private set; }
    public DateTime? ReadAt { get; private set; }

    public NotificationEvent Event { get; private set; } = null!;
    public User User { get; private set; } = null!;
    public ICollection<NotificationDelivery> Deliveries { get; private set; } = [];

    public void MarkRead(DateTime readAt)
    {
        ReadAt ??= NotificationEvent.EnsureIndiaLocal(readAt, nameof(readAt));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class NotificationTemplate : AuditableEntity
{
    private NotificationTemplate() { }

    public NotificationTemplate(
        string eventType,
        NotificationChannel channel,
        string language,
        string? titleTemplate,
        string bodyTemplate,
        bool isActive = true)
    {
        EventType = NotificationEvent.NormalizeCode(eventType, nameof(eventType));
        SetChannel(channel);
        Language = NormalizeLanguage(language);
        Update(titleTemplate, bodyTemplate, isActive);
    }

    public string EventType { get; private set; } = string.Empty;
    public NotificationChannel Channel { get; private set; }
    public string Language { get; private set; } = string.Empty;
    public string? TitleTemplate { get; private set; }
    public string BodyTemplate { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }

    public void Update(string? titleTemplate, string bodyTemplate, bool isActive)
    {
        TitleTemplate = string.IsNullOrWhiteSpace(titleTemplate) ? null : titleTemplate.Trim();
        BodyTemplate = NotificationEvent.NormalizeRequired(bodyTemplate, nameof(bodyTemplate));
        IsActive = isActive;
    }

    private void SetChannel(NotificationChannel channel)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        Channel = channel;
    }

    private static string NormalizeLanguage(string language) =>
        NotificationEvent.NormalizeRequired(language, nameof(language)).ToLowerInvariant();
}

public sealed class NotificationPreference : AuditableEntity
{
    private NotificationPreference() { }

    public NotificationPreference(
        long userId,
        string eventType,
        NotificationChannel channel,
        bool isEnabled)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        UserId = userId;
        EventType = NotificationEvent.NormalizeCode(eventType, nameof(eventType));
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        Channel = channel;
        IsEnabled = isEnabled;
    }

    public long UserId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public NotificationChannel Channel { get; private set; }
    public bool IsEnabled { get; private set; }

    public User User { get; private set; } = null!;

    public void SetEnabled(bool isEnabled) => IsEnabled = isEnabled;
}

public sealed class UserDevice : AuditableEntity
{
    private UserDevice() { }

    public UserDevice(
        long userId,
        string deviceIdentifierHash,
        string tokenHash,
        string protectedToken,
        string platform,
        string? deviceName,
        DateTime registeredAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        UserId = userId;
        DeviceIdentifierHash = NotificationEvent.NormalizeRequired(deviceIdentifierHash, nameof(deviceIdentifierHash));
        RotateToken(tokenHash, protectedToken, registeredAt);
        Platform = NotificationEvent.NormalizeCode(platform, nameof(platform));
        DeviceName = NormalizeOptional(deviceName);
    }

    public long UserId { get; private set; }
    public string DeviceIdentifierHash { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public string ProtectedToken { get; private set; } = string.Empty;
    public string Platform { get; private set; } = string.Empty;
    public string? DeviceName { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime RegisteredAt { get; private set; }
    public DateTime? LastSeenAt { get; private set; }
    public DateTime? InvalidatedAt { get; private set; }

    public User User { get; private set; } = null!;
    public ICollection<NotificationDelivery> Deliveries { get; private set; } = [];

    public void RotateToken(string tokenHash, string protectedToken, DateTime seenAt)
    {
        TokenHash = NotificationEvent.NormalizeRequired(tokenHash, nameof(tokenHash));
        ProtectedToken = NotificationEvent.NormalizeRequired(protectedToken, nameof(protectedToken));
        LastSeenAt = NotificationEvent.EnsureIndiaLocal(seenAt, nameof(seenAt));
        RegisteredAt = RegisteredAt == default ? seenAt : RegisteredAt;
        InvalidatedAt = null;
        IsActive = true;
    }

    public void Touch(DateTime seenAt) =>
        LastSeenAt = NotificationEvent.EnsureIndiaLocal(seenAt, nameof(seenAt));

    public void Invalidate(DateTime invalidatedAt)
    {
        IsActive = false;
        InvalidatedAt = NotificationEvent.EnsureIndiaLocal(invalidatedAt, nameof(invalidatedAt));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class NotificationDelivery : AuditableEntity
{
    private NotificationDelivery() { }

    public NotificationDelivery(
        long notificationId,
        NotificationChannel channel,
        string providerCode,
        string destinationReference,
        long? userDeviceId,
        DateTime createdAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(notificationId);
        NotificationId = notificationId;
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        Channel = channel;
        ProviderCode = NotificationEvent.NormalizeCode(providerCode, nameof(providerCode));
        DestinationReference = NotificationEvent.NormalizeRequired(destinationReference, nameof(destinationReference));
        UserDeviceId = userDeviceId;
        NextAttemptAt = NotificationEvent.EnsureIndiaLocal(createdAt, nameof(createdAt));
        Status = NotificationDeliveryStatus.Pending;
    }

    public long NotificationId { get; private set; }
    public long? UserDeviceId { get; private set; }
    public NotificationChannel Channel { get; private set; }
    public string ProviderCode { get; private set; } = string.Empty;
    public string DestinationReference { get; private set; } = string.Empty;
    public NotificationDeliveryStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime? NextAttemptAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }

    public Notification Notification { get; private set; } = null!;
    public UserDevice? UserDevice { get; private set; }
    public ICollection<NotificationAttempt> Attempts { get; private set; } = [];

    public bool IsTerminal => Status is NotificationDeliveryStatus.Delivered
        or NotificationDeliveryStatus.Failed
        or NotificationDeliveryStatus.Suppressed
        or NotificationDeliveryStatus.Unconfigured;

    public void StartAttempt()
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException("A terminal notification delivery cannot be retried.");
        }

        Status = NotificationDeliveryStatus.Processing;
        AttemptCount++;
        NextAttemptAt = null;
    }

    public void MarkDelivered(string? providerMessageId, DateTime deliveredAt)
    {
        Status = NotificationDeliveryStatus.Delivered;
        ProviderMessageId = string.IsNullOrWhiteSpace(providerMessageId) ? null : providerMessageId.Trim();
        DeliveredAt = NotificationEvent.EnsureIndiaLocal(deliveredAt, nameof(deliveredAt));
        FailureCode = null;
        FailureMessage = null;
        NextAttemptAt = null;
    }

    public void ScheduleRetry(string failureCode, string failureMessage, DateTime nextAttemptAt)
    {
        Status = NotificationDeliveryStatus.RetryScheduled;
        SetFailure(failureCode, failureMessage);
        NextAttemptAt = NotificationEvent.EnsureIndiaLocal(nextAttemptAt, nameof(nextAttemptAt));
    }

    public void MarkFailed(string failureCode, string failureMessage)
    {
        Status = NotificationDeliveryStatus.Failed;
        SetFailure(failureCode, failureMessage);
        NextAttemptAt = null;
    }

    public void MarkSuppressed(string reason)
    {
        Status = NotificationDeliveryStatus.Suppressed;
        SetFailure("PREFERENCE_SUPPRESSED", reason);
        NextAttemptAt = null;
    }

    public void MarkUnconfigured(string failureMessage)
    {
        Status = NotificationDeliveryStatus.Unconfigured;
        SetFailure("PROVIDER_UNCONFIGURED", failureMessage);
        NextAttemptAt = null;
    }

    private void SetFailure(string failureCode, string failureMessage)
    {
        FailureCode = NotificationEvent.NormalizeCode(failureCode, nameof(failureCode));
        FailureMessage = NotificationEvent.NormalizeRequired(failureMessage, nameof(failureMessage));
    }
}

public sealed class NotificationAttempt : PublicEntity
{
    private NotificationAttempt() { }

    public NotificationAttempt(
        long notificationDeliveryId,
        int attemptNumber,
        NotificationAttemptOutcome outcome,
        string? providerMessageId,
        string? failureCode,
        string? failureMessage,
        DateTime attemptedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(notificationDeliveryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        NotificationDeliveryId = notificationDeliveryId;
        AttemptNumber = attemptNumber;
        Outcome = outcome;
        ProviderMessageId = NormalizeOptional(providerMessageId);
        FailureCode = NormalizeOptional(failureCode)?.ToUpperInvariant();
        FailureMessage = NormalizeOptional(failureMessage);
        AttemptedAt = NotificationEvent.EnsureIndiaLocal(attemptedAt, nameof(attemptedAt));
    }

    public long NotificationDeliveryId { get; private set; }
    public int AttemptNumber { get; private set; }
    public NotificationAttemptOutcome Outcome { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }
    public DateTime AttemptedAt { get; private set; }

    public NotificationDelivery Delivery { get; private set; } = null!;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
