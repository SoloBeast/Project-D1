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
        DateTime occurredAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        UserId = userId;
        EventType = NormalizeCode(eventType, nameof(eventType));
        EventKey = NormalizeRequired(eventKey, nameof(eventKey));
        PayloadJson = NormalizeRequired(payloadJson, nameof(payloadJson));
        IsCritical = isCritical;
        OccurredAtUtc = EnsureUtc(occurredAtUtc, nameof(occurredAtUtc));
        Status = NotificationEventStatus.Pending;
    }

    public long UserId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string EventKey { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public bool IsCritical { get; private set; }
    public NotificationEventStatus Status { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }
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

    public void Complete(DateTime processedAtUtc)
    {
        Status = NotificationEventStatus.Processed;
        ProcessedAtUtc = EnsureUtc(processedAtUtc, nameof(processedAtUtc));
        FailureCode = null;
        FailureMessage = null;
    }

    public void Fail(string failureCode, string failureMessage, DateTime processedAtUtc)
    {
        Status = NotificationEventStatus.Failed;
        FailureCode = NormalizeCode(failureCode, nameof(failureCode));
        FailureMessage = NormalizeRequired(failureMessage, nameof(failureMessage));
        ProcessedAtUtc = EnsureUtc(processedAtUtc, nameof(processedAtUtc));
    }

    internal static string NormalizeCode(string value, string parameterName) =>
        NormalizeRequired(value, parameterName).ToUpperInvariant();

    internal static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    internal static DateTime EnsureUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
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
        DateTime createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(notificationEventId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        NotificationEventId = notificationEventId;
        UserId = userId;
        EventType = NotificationEvent.NormalizeCode(eventType, nameof(eventType));
        Title = NotificationEvent.NormalizeRequired(title, nameof(title));
        Body = NotificationEvent.NormalizeRequired(body, nameof(body));
        DeepLink = NormalizeOptional(deepLink);
        NotificationEvent.EnsureUtc(createdAtUtc, nameof(createdAtUtc));
    }

    public long NotificationEventId { get; private set; }
    public long UserId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public string? DeepLink { get; private set; }
    public DateTime? ReadAtUtc { get; private set; }

    public NotificationEvent Event { get; private set; } = null!;
    public User User { get; private set; } = null!;
    public ICollection<NotificationDelivery> Deliveries { get; private set; } = [];

    public void MarkRead(DateTime readAtUtc)
    {
        ReadAtUtc ??= NotificationEvent.EnsureUtc(readAtUtc, nameof(readAtUtc));
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
        DateTime registeredAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        UserId = userId;
        DeviceIdentifierHash = NotificationEvent.NormalizeRequired(deviceIdentifierHash, nameof(deviceIdentifierHash));
        RotateToken(tokenHash, protectedToken, registeredAtUtc);
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
    public DateTime RegisteredAtUtc { get; private set; }
    public DateTime? LastSeenAtUtc { get; private set; }
    public DateTime? InvalidatedAtUtc { get; private set; }

    public User User { get; private set; } = null!;
    public ICollection<NotificationDelivery> Deliveries { get; private set; } = [];

    public void RotateToken(string tokenHash, string protectedToken, DateTime seenAtUtc)
    {
        TokenHash = NotificationEvent.NormalizeRequired(tokenHash, nameof(tokenHash));
        ProtectedToken = NotificationEvent.NormalizeRequired(protectedToken, nameof(protectedToken));
        LastSeenAtUtc = NotificationEvent.EnsureUtc(seenAtUtc, nameof(seenAtUtc));
        RegisteredAtUtc = RegisteredAtUtc == default ? seenAtUtc : RegisteredAtUtc;
        InvalidatedAtUtc = null;
        IsActive = true;
    }

    public void Touch(DateTime seenAtUtc) =>
        LastSeenAtUtc = NotificationEvent.EnsureUtc(seenAtUtc, nameof(seenAtUtc));

    public void Invalidate(DateTime invalidatedAtUtc)
    {
        IsActive = false;
        InvalidatedAtUtc = NotificationEvent.EnsureUtc(invalidatedAtUtc, nameof(invalidatedAtUtc));
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
        DateTime createdAtUtc)
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
        NextAttemptAtUtc = NotificationEvent.EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        Status = NotificationDeliveryStatus.Pending;
    }

    public long NotificationId { get; private set; }
    public long? UserDeviceId { get; private set; }
    public NotificationChannel Channel { get; private set; }
    public string ProviderCode { get; private set; } = string.Empty;
    public string DestinationReference { get; private set; } = string.Empty;
    public NotificationDeliveryStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime? NextAttemptAtUtc { get; private set; }
    public DateTime? DeliveredAtUtc { get; private set; }
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
        NextAttemptAtUtc = null;
    }

    public void MarkDelivered(string? providerMessageId, DateTime deliveredAtUtc)
    {
        Status = NotificationDeliveryStatus.Delivered;
        ProviderMessageId = string.IsNullOrWhiteSpace(providerMessageId) ? null : providerMessageId.Trim();
        DeliveredAtUtc = NotificationEvent.EnsureUtc(deliveredAtUtc, nameof(deliveredAtUtc));
        FailureCode = null;
        FailureMessage = null;
        NextAttemptAtUtc = null;
    }

    public void ScheduleRetry(string failureCode, string failureMessage, DateTime nextAttemptAtUtc)
    {
        Status = NotificationDeliveryStatus.RetryScheduled;
        SetFailure(failureCode, failureMessage);
        NextAttemptAtUtc = NotificationEvent.EnsureUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
    }

    public void MarkFailed(string failureCode, string failureMessage)
    {
        Status = NotificationDeliveryStatus.Failed;
        SetFailure(failureCode, failureMessage);
        NextAttemptAtUtc = null;
    }

    public void MarkSuppressed(string reason)
    {
        Status = NotificationDeliveryStatus.Suppressed;
        SetFailure("PREFERENCE_SUPPRESSED", reason);
        NextAttemptAtUtc = null;
    }

    public void MarkUnconfigured(string failureMessage)
    {
        Status = NotificationDeliveryStatus.Unconfigured;
        SetFailure("PROVIDER_UNCONFIGURED", failureMessage);
        NextAttemptAtUtc = null;
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
        DateTime attemptedAtUtc)
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
        AttemptedAtUtc = NotificationEvent.EnsureUtc(attemptedAtUtc, nameof(attemptedAtUtc));
    }

    public long NotificationDeliveryId { get; private set; }
    public int AttemptNumber { get; private set; }
    public NotificationAttemptOutcome Outcome { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }
    public DateTime AttemptedAtUtc { get; private set; }

    public NotificationDelivery Delivery { get; private set; } = null!;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
