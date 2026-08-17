using System.ComponentModel.DataAnnotations;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Notifications;

namespace DoodhDirect.Infrastructure.Notifications;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    [Range(1, 100)]
    public int BatchSize { get; init; } = 25;

    [Range(1, 10)]
    public int MaxAttempts { get; init; } = 4;

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; init; } = 10;

    [Range(1, 1440)]
    public int InitialRetryDelayMinutes { get; init; } = 2;

    public string PushProvider { get; init; } = "Unconfigured";
    public string SmsProvider { get; init; } = "Unconfigured";
    public string WhatsAppProvider { get; init; } = "Unconfigured";
    public string EmailProvider { get; init; } = "Unconfigured";

    public bool UsesDevelopmentMock =>
        IsDevelopmentMock(PushProvider)
        || IsDevelopmentMock(SmsProvider)
        || IsDevelopmentMock(WhatsAppProvider)
        || IsDevelopmentMock(EmailProvider);

    public string ProviderFor(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Push => PushProvider,
        NotificationChannel.Sms => SmsProvider,
        NotificationChannel.WhatsApp => WhatsAppProvider,
        NotificationChannel.Email => EmailProvider,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    public static bool IsDevelopmentMock(string provider) =>
        string.Equals(provider, "DevelopmentMock", StringComparison.OrdinalIgnoreCase);
}

internal sealed class DevelopmentNotificationGateway(NotificationChannel channel) : INotificationChannelGateway
{
    public NotificationChannel Channel { get; } = channel;
    public string ProviderCode => "DEVELOPMENT_MOCK";

    public Task<NotificationProviderResult> SendAsync(
        NotificationProviderMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new NotificationProviderResult(
            NotificationAttemptOutcome.Delivered,
            $"dev-{message.DeliveryId:N}"));
    }
}

internal sealed class UnconfiguredNotificationGateway(NotificationChannel channel) : INotificationChannelGateway
{
    public NotificationChannel Channel { get; } = channel;
    public string ProviderCode => "UNCONFIGURED";

    public Task<NotificationProviderResult> SendAsync(
        NotificationProviderMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new NotificationProviderResult(
            NotificationAttemptOutcome.Unconfigured,
            FailureCode: "PROVIDER_UNCONFIGURED",
            FailureMessage: $"No {Channel} notification provider is configured."));
    }
}
