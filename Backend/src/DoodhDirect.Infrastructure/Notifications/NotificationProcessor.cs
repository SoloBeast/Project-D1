using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Notifications;

internal sealed partial class NotificationProcessor(
    DoodhDirectDbContext dbContext,
    IEnumerable<INotificationChannelGateway> gateways,
    NotificationTokenProtector tokenProtector,
    IOptions<NotificationOptions> options,
    IClock clock) : INotificationProcessor
{
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationChannelGateway> _gateways =
        gateways.ToDictionary(x => x.Channel);
    private readonly NotificationOptions _options = options.Value;

    public async Task<int> ProcessPendingEventsAsync(CancellationToken cancellationToken)
    {
        var eventIds = await dbContext.NotificationEvents
            .AsNoTracking()
            .Where(x => x.Status == NotificationEventStatus.Pending)
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var eventId in eventIds)
        {
            dbContext.ChangeTracker.Clear();
            var strategy = dbContext.Database.CreateExecutionStrategy();
            var handled = false;
            await strategy.ExecuteAsync(async () =>
            {
                await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                var notificationEvent = await dbContext.NotificationEvents.SingleOrDefaultAsync(
                    x => x.Id == eventId && x.Status == NotificationEventStatus.Pending,
                    cancellationToken);
                if (notificationEvent is null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }

                notificationEvent.StartProcessing();
                try
                {
                    await MaterializeEventAsync(notificationEvent, cancellationToken);
                    notificationEvent.Complete(clock.UtcNow);
                }
                catch (JsonException exception)
                {
                    notificationEvent.Fail("INVALID_PAYLOAD", Limit(exception.Message, 1000), clock.UtcNow);
                }
                catch (NotificationMaterializationException exception)
                {
                    notificationEvent.Fail(exception.Code, exception.Message, clock.UtcNow);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                handled = true;
            });

            if (handled)
            {
                processed++;
            }
        }

        return processed;
    }

    public async Task<int> ProcessDueDeliveriesAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var deliveryIds = await dbContext.NotificationDeliveries
            .AsNoTracking()
            .Where(x => (x.Status == NotificationDeliveryStatus.Pending
                    || x.Status == NotificationDeliveryStatus.RetryScheduled)
                && x.NextAttemptAtUtc != null
                && x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.NextAttemptAtUtc)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var deliveryId in deliveryIds)
        {
            dbContext.ChangeTracker.Clear();
            if (await ProcessDeliveryAsync(deliveryId, cancellationToken))
            {
                processed++;
            }
        }

        return processed;
    }

    private async Task MaterializeEventAsync(
        NotificationEvent notificationEvent,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<StoredNotificationPayload>(notificationEvent.PayloadJson)
            ?? throw new NotificationMaterializationException("INVALID_PAYLOAD", "Notification payload is empty.");
        var variables = payload.Variables
            ?? throw new NotificationMaterializationException("INVALID_PAYLOAD", "Notification variables are required.");
        var templates = await dbContext.NotificationTemplates
            .Where(x => x.EventType == notificationEvent.EventType && x.IsActive && x.Language == "en")
            .OrderBy(x => x.Channel)
            .ToListAsync(cancellationToken);
        if (templates.Count == 0)
        {
            throw new NotificationMaterializationException(
                "TEMPLATE_NOT_FOUND",
                $"No active English template is configured for {notificationEvent.EventType}.");
        }

        var existingNotification = await dbContext.Notifications.SingleOrDefaultAsync(
            x => x.NotificationEventId == notificationEvent.Id,
            cancellationToken);
        if (existingNotification is not null)
        {
            return;
        }

        var inboxTemplate = templates.FirstOrDefault(x => x.Channel == NotificationChannel.Push) ?? templates[0];
        var notification = new Notification(
            notificationEvent.Id,
            notificationEvent.UserId,
            notificationEvent.EventType,
            Render(inboxTemplate.TitleTemplate ?? Humanize(notificationEvent.EventType), variables),
            Render(inboxTemplate.BodyTemplate, variables),
            payload.DeepLink,
            clock.UtcNow);
        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync(cancellationToken);

        var user = await dbContext.Users.AsNoTracking().SingleAsync(
            x => x.Id == notificationEvent.UserId,
            cancellationToken);
        var preferences = await dbContext.NotificationPreferences
            .AsNoTracking()
            .Where(x => x.UserId == notificationEvent.UserId && x.EventType == notificationEvent.EventType)
            .ToDictionaryAsync(x => x.Channel, x => x.IsEnabled, cancellationToken);
        var devices = await dbContext.UserDevices
            .AsNoTracking()
            .Where(x => x.UserId == notificationEvent.UserId && x.IsActive)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var template in templates)
        {
            var enabled = notificationEvent.IsCritical
                || !preferences.TryGetValue(template.Channel, out var preferenceEnabled)
                || preferenceEnabled;
            if (!enabled)
            {
                AddSuppressedDelivery(notification.Id, template.Channel);
                continue;
            }

            if (template.Channel == NotificationChannel.Push)
            {
                foreach (var device in devices)
                {
                    dbContext.NotificationDeliveries.Add(new NotificationDelivery(
                        notification.Id,
                        template.Channel,
                        GatewayFor(template.Channel).ProviderCode,
                        device.TokenHash,
                        device.Id,
                        clock.UtcNow));
                }

                continue;
            }

            var destination = template.Channel switch
            {
                NotificationChannel.Sms or NotificationChannel.WhatsApp => user.Mobile,
                NotificationChannel.Email => user.Email,
                _ => null
            };
            var delivery = new NotificationDelivery(
                notification.Id,
                template.Channel,
                GatewayFor(template.Channel).ProviderCode,
                destination ?? "DESTINATION_UNAVAILABLE",
                null,
                clock.UtcNow);
            if (string.IsNullOrWhiteSpace(destination))
            {
                delivery.MarkFailed("DESTINATION_UNAVAILABLE", $"No {template.Channel} destination is registered.");
            }
            dbContext.NotificationDeliveries.Add(delivery);
        }
    }

    private async Task<bool> ProcessDeliveryAsync(long deliveryId, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        var processed = false;
        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var delivery = await dbContext.NotificationDeliveries
                .Include(x => x.Notification)
                .Include(x => x.UserDevice)
                .SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
            var now = clock.UtcNow;
            if (delivery is null
                || delivery.IsTerminal
                || delivery.NextAttemptAtUtc is null
                || delivery.NextAttemptAtUtc > now)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            delivery.StartAttempt();
            await dbContext.SaveChangesAsync(cancellationToken);

            NotificationProviderResult result;
            try
            {
                var destination = delivery.Channel == NotificationChannel.Push
                    ? UnprotectPushDestination(delivery)
                    : delivery.DestinationReference;
                result = await GatewayFor(delivery.Channel).SendAsync(
                    new NotificationProviderMessage(
                        delivery.PublicId,
                        delivery.Channel,
                        destination,
                        delivery.Notification.Title,
                        delivery.Notification.Body,
                        delivery.Notification.DeepLink,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["notificationId"] = delivery.Notification.PublicId.ToString(),
                            ["eventType"] = delivery.Notification.EventType
                        }),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = new NotificationProviderResult(
                    NotificationAttemptOutcome.RetryableFailure,
                    FailureCode: "PROVIDER_EXCEPTION",
                    FailureMessage: Limit(exception.Message, 1000));
            }

            ApplyResult(delivery, result, now);
            dbContext.NotificationAttempts.Add(new NotificationAttempt(
                delivery.Id,
                delivery.AttemptCount,
                result.Outcome,
                result.ProviderMessageId,
                result.FailureCode,
                result.FailureMessage,
                now));
            if (result.InvalidateDestination && delivery.UserDevice is not null)
            {
                delivery.UserDevice.Invalidate(now);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            processed = true;
        });
        return processed;
    }

    private void ApplyResult(NotificationDelivery delivery, NotificationProviderResult result, DateTime now)
    {
        var failureCode = string.IsNullOrWhiteSpace(result.FailureCode)
            ? "PROVIDER_FAILURE"
            : Limit(result.FailureCode, 100);
        var failureMessage = string.IsNullOrWhiteSpace(result.FailureMessage)
            ? "The notification provider did not deliver the message."
            : Limit(result.FailureMessage, 1000);

        switch (result.Outcome)
        {
            case NotificationAttemptOutcome.Delivered:
                delivery.MarkDelivered(result.ProviderMessageId, now);
                break;
            case NotificationAttemptOutcome.RetryableFailure when delivery.AttemptCount < _options.MaxAttempts:
                var multiplier = Math.Pow(2, delivery.AttemptCount - 1);
                delivery.ScheduleRetry(
                    failureCode,
                    failureMessage,
                    now.AddMinutes(_options.InitialRetryDelayMinutes * multiplier));
                break;
            case NotificationAttemptOutcome.Unconfigured:
                delivery.MarkUnconfigured(failureMessage);
                break;
            case NotificationAttemptOutcome.RetryableFailure:
            case NotificationAttemptOutcome.PermanentFailure:
                delivery.MarkFailed(failureCode, failureMessage);
                break;
            default:
                throw new InvalidOperationException($"Unsupported notification outcome {result.Outcome}.");
        }
    }

    private void AddSuppressedDelivery(long notificationId, NotificationChannel channel)
    {
        var delivery = new NotificationDelivery(
            notificationId,
            channel,
            GatewayFor(channel).ProviderCode,
            "PREFERENCE_SUPPRESSED",
            null,
            clock.UtcNow);
        delivery.MarkSuppressed("The customer disabled this optional notification channel.");
        dbContext.NotificationDeliveries.Add(delivery);
    }

    private string UnprotectPushDestination(NotificationDelivery delivery)
    {
        if (delivery.UserDevice is null || !delivery.UserDevice.IsActive)
        {
            throw new InvalidOperationException("The push device is no longer active.");
        }

        return tokenProtector.Unprotect(delivery.UserDevice.ProtectedToken);
    }

    private INotificationChannelGateway GatewayFor(NotificationChannel channel) =>
        _gateways.TryGetValue(channel, out var gateway)
            ? gateway
            : throw new NotificationMaterializationException(
                "PROVIDER_NOT_REGISTERED",
                $"No {channel} notification gateway is registered.");

    private static string Render(string template, IReadOnlyDictionary<string, string> variables) =>
        PlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return variables.TryGetValue(key, out var value) ? value : match.Value;
        });

    private static string Humanize(string eventType) =>
        string.Join(' ', eventType.Split('_', StringSplitOptions.RemoveEmptyEntries)) switch
        {
            var value when value.Length == 0 => "Notification",
            var value => char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant()
        };

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.-]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    private sealed class NotificationMaterializationException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
