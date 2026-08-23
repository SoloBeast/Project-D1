using DoodhDirect.Domain.Notifications;

namespace DoodhDirect.Application.Notifications;

public static class NotificationEventTypes
{
    public const string RegistrationCompleted = "REGISTRATION_COMPLETED";
    public const string AuthenticationSucceeded = "AUTHENTICATION_SUCCEEDED";
    public const string OrderCreated = "ORDER_CREATED";
    public const string PaymentSucceeded = "PAYMENT_SUCCEEDED";
    public const string PaymentFailed = "PAYMENT_FAILED";
    public const string WalletUpdated = "WALLET_UPDATED";
    public const string SubscriptionCreated = "SUBSCRIPTION_CREATED";
    public const string SubscriptionPaymentPending = "SUBSCRIPTION_PAYMENT_PENDING";
    public const string SubscriptionActivated = "SUBSCRIPTION_ACTIVATED";
    public const string SubscriptionSkipped = "SUBSCRIPTION_SKIPPED";
    public const string SubscriptionPaused = "SUBSCRIPTION_PAUSED";
    public const string SubscriptionResumed = "SUBSCRIPTION_RESUMED";
    public const string DeliveryAssigned = "DELIVERY_ASSIGNED";
    public const string DeliveryStarted = "DELIVERY_STARTED";
    public const string DeliveryNearCustomer = "DELIVERY_NEAR_CUSTOMER";
    public const string DeliveryOtpIssued = "DELIVERY_OTP_ISSUED";
    public const string DeliveryCompleted = "DELIVERY_COMPLETED";
    public const string DeliveryFailed = "DELIVERY_FAILED";
    public const string MilkTestRequested = "MILK_TEST_REQUESTED";
    public const string MilkTestCompleted = "MILK_TEST_COMPLETED";
    public const string ComplaintUpdated = "COMPLAINT_UPDATED";
    public const string ReplacementUpdated = "REPLACEMENT_UPDATED";

    public static IReadOnlySet<string> Critical { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RegistrationCompleted,
        AuthenticationSucceeded,
        PaymentSucceeded,
        PaymentFailed,
        WalletUpdated,
        SubscriptionPaymentPending,
        DeliveryAssigned,
        DeliveryStarted,
        DeliveryNearCustomer,
        DeliveryOtpIssued,
        DeliveryCompleted,
        DeliveryFailed,
        MilkTestRequested,
        MilkTestCompleted
    };

    public static IReadOnlyCollection<string> All { get; } =
    [
        RegistrationCompleted,
        AuthenticationSucceeded,
        OrderCreated,
        PaymentSucceeded,
        PaymentFailed,
        WalletUpdated,
        SubscriptionCreated,
        SubscriptionPaymentPending,
        SubscriptionActivated,
        SubscriptionSkipped,
        SubscriptionPaused,
        SubscriptionResumed,
        DeliveryAssigned,
        DeliveryStarted,
        DeliveryNearCustomer,
        DeliveryOtpIssued,
        DeliveryCompleted,
        DeliveryFailed,
        MilkTestRequested,
        MilkTestCompleted,
        ComplaintUpdated,
        ReplacementUpdated
    ];

    public static bool IsCritical(string eventType) =>
        Critical.Contains(eventType.Trim().ToUpperInvariant());
}

public sealed record NotificationActor(long UserId);

public sealed record NotificationEventRequest(
    long UserId,
    string EventType,
    string EventKey,
    IReadOnlyDictionary<string, string> Variables,
    string? DeepLink = null,
    DateTime? OccurredAt = null,
    IReadOnlyDictionary<string, string>? ProtectedVariables = null);

public sealed record NotificationListRequest(
    int Page = 1,
    int PageSize = 20,
    bool? IsRead = null);

public sealed record NotificationListItem(
    Guid NotificationId,
    string EventType,
    string Title,
    string Body,
    string? DeepLink,
    bool IsRead,
    DateTime CreatedAt,
    DateTime? ReadAt);

public sealed record NotificationPageResult(
    IReadOnlyCollection<NotificationListItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record NotificationUnreadCountResult(int UnreadCount);

public sealed record RegisterDeviceRequest(
    string DeviceIdentifier,
    string PushToken,
    string Platform,
    string? DeviceName);

public sealed record UserDeviceResult(
    Guid DeviceId,
    string Platform,
    string? DeviceName,
    bool IsActive,
    DateTime RegisteredAt,
    DateTime? LastSeenAt);

public sealed record NotificationPreferenceResult(
    string EventType,
    NotificationChannel Channel,
    bool IsEnabled,
    bool IsCritical);

public sealed record UpdateNotificationPreferenceRequest(
    string EventType,
    NotificationChannel Channel,
    bool IsEnabled);

public sealed record NotificationTemplateResult(
    Guid TemplateId,
    string EventType,
    NotificationChannel Channel,
    string Language,
    string? TitleTemplate,
    string BodyTemplate,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record UpdateNotificationTemplateRequest(
    string? TitleTemplate,
    string BodyTemplate,
    bool IsActive,
    string Reason);

public sealed record NotificationProviderMessage(
    Guid DeliveryId,
    NotificationChannel Channel,
    string Destination,
    string? Title,
    string Body,
    string? DeepLink,
    IReadOnlyDictionary<string, string> Data);

public sealed record NotificationProviderResult(
    NotificationAttemptOutcome Outcome,
    string? ProviderMessageId = null,
    string? FailureCode = null,
    string? FailureMessage = null,
    bool InvalidateDestination = false);

public interface INotificationEventWriter
{
    void Add(NotificationEventRequest request);
}

public interface INotificationChannelGateway
{
    NotificationChannel Channel { get; }
    string ProviderCode { get; }

    Task<NotificationProviderResult> SendAsync(
        NotificationProviderMessage message,
        CancellationToken cancellationToken);
}

public interface INotificationService
{
    Task<NotificationPageResult> GetAsync(
        NotificationActor actor,
        NotificationListRequest request,
        CancellationToken cancellationToken);

    Task<NotificationUnreadCountResult> GetUnreadCountAsync(
        NotificationActor actor,
        CancellationToken cancellationToken);

    Task MarkReadAsync(
        NotificationActor actor,
        Guid notificationId,
        CancellationToken cancellationToken);

    Task<UserDeviceResult> RegisterDeviceAsync(
        NotificationActor actor,
        RegisterDeviceRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<NotificationPreferenceResult>> GetPreferencesAsync(
        NotificationActor actor,
        CancellationToken cancellationToken);

    Task<NotificationPreferenceResult> UpdatePreferenceAsync(
        NotificationActor actor,
        UpdateNotificationPreferenceRequest request,
        CancellationToken cancellationToken);
}

public interface INotificationTemplateService
{
    Task<IReadOnlyCollection<NotificationTemplateResult>> GetAsync(
        CancellationToken cancellationToken);

    Task<NotificationTemplateResult> UpdateAsync(
        long actorUserId,
        Guid templateId,
        UpdateNotificationTemplateRequest request,
        CancellationToken cancellationToken);
}

public interface INotificationProcessor
{
    Task<int> ProcessPendingEventsAsync(CancellationToken cancellationToken);
    Task<int> ProcessDueDeliveriesAsync(CancellationToken cancellationToken);
}

public interface IDevelopmentNotificationService
{
    Task<IReadOnlyCollection<Guid>> CreateSamplesAsync(
        NotificationActor actor,
        CancellationToken cancellationToken);
}
