using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Notifications;

public sealed class NotificationTemplateSeedService(DoodhDirectDbContext dbContext)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);
            var existing = await dbContext.NotificationTemplates
                .Select(x => new { x.EventType, x.Channel, x.Language })
                .ToListAsync(cancellationToken);
            var keys = existing
                .Select(x => (x.EventType, x.Channel, x.Language))
                .ToHashSet();

            foreach (var eventType in NotificationEventTypes.All)
            {
                foreach (var channel in Enum.GetValues<NotificationChannel>())
                {
                    if (keys.Contains((eventType, channel, "en")))
                    {
                        continue;
                    }

                    var title = Humanize(eventType);
                    dbContext.NotificationTemplates.Add(new NotificationTemplate(
                        eventType,
                        channel,
                        "en",
                        title,
                        $"{title}: {{{{message}}}}"));
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static string Humanize(string eventType)
    {
        var value = string.Join(' ', eventType.Split('_', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return value.Length == 0 ? "Notification" : char.ToUpperInvariant(value[0]) + value[1..];
    }
}

internal sealed class DevelopmentNotificationService(
    DoodhDirectDbContext dbContext,
    IClock clock) : IDevelopmentNotificationService
{
    private static readonly string[] SampleEventTypes =
    [
        NotificationEventTypes.OrderCreated,
        NotificationEventTypes.PaymentSucceeded,
        NotificationEventTypes.SubscriptionActivated,
        NotificationEventTypes.DeliveryNearCustomer
    ];

    public async Task<IReadOnlyCollection<Guid>> CreateSamplesAsync(
        NotificationActor actor,
        CancellationToken cancellationToken)
    {
        if (actor.UserId <= 0)
        {
            throw new Application.Common.UnauthorizedAppException();
        }

        var now = clock.UtcNow;
        var events = SampleEventTypes.Select((eventType, index) => new NotificationEvent(
            actor.UserId,
            eventType,
            $"development-sample:{actor.UserId}:{eventType}:{Guid.NewGuid():N}",
            System.Text.Json.JsonSerializer.Serialize(new StoredNotificationPayload(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["message"] = SampleMessage(eventType)
                },
                SampleDeepLink(eventType))),
            NotificationEventTypes.IsCritical(eventType),
            now.AddTicks(index))).ToArray();
        dbContext.NotificationEvents.AddRange(events);
        await dbContext.SaveChangesAsync(cancellationToken);
        return events.Select(x => x.PublicId).ToArray();
    }

    private static string SampleMessage(string eventType) => eventType switch
    {
        NotificationEventTypes.OrderCreated => "Your development order has been created.",
        NotificationEventTypes.PaymentSucceeded => "Your development payment was successful.",
        NotificationEventTypes.SubscriptionActivated => "Your development subscription is active.",
        NotificationEventTypes.DeliveryNearCustomer => "Your development delivery is nearby.",
        _ => "A development notification was created."
    };

    private static string SampleDeepLink(string eventType) => eventType switch
    {
        NotificationEventTypes.OrderCreated => "/orders",
        NotificationEventTypes.PaymentSucceeded => "/wallet",
        NotificationEventTypes.SubscriptionActivated => "/subscriptions",
        NotificationEventTypes.DeliveryNearCustomer => "/deliveries",
        _ => "/notifications"
    };
}
