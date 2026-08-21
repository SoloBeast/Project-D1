using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Infrastructure.Persistence;

namespace DoodhDirect.Api.IntegrationTests;

internal sealed class TestNotificationEventWriter(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider) : INotificationEventWriter
{
    public void Add(NotificationEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var eventType = request.EventType.Trim().ToUpperInvariant();
        if (!NotificationEventTypes.All.Contains(eventType, StringComparer.Ordinal))
        {
            throw new ArgumentException("The notification event type is not supported.", nameof(request));
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            request.Variables,
            DeepLink = string.IsNullOrWhiteSpace(request.DeepLink)
                ? null
                : request.DeepLink.Trim()
        });
        dbContext.NotificationEvents.Add(new NotificationEvent(
            request.UserId,
            eventType,
            request.EventKey,
            payloadJson,
            NotificationEventTypes.IsCritical(eventType),
            request.OccurredAt ?? timeProvider.Now));
    }
}
