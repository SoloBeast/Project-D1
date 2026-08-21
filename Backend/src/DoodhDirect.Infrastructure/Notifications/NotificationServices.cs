using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Notifications;

internal sealed class NotificationEventWriter(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider) : INotificationEventWriter
{
    public void Add(NotificationEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var eventType = NormalizeEventType(request.EventType);
        var payload = JsonSerializer.Serialize(new StoredNotificationPayload(
            request.Variables,
            string.IsNullOrWhiteSpace(request.DeepLink) ? null : request.DeepLink.Trim()));
        dbContext.NotificationEvents.Add(new NotificationEvent(
            request.UserId,
            eventType,
            request.EventKey,
            payload,
            NotificationEventTypes.IsCritical(eventType),
            request.OccurredAt ?? timeProvider.Now));
    }

    internal static string NormalizeEventType(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        var normalized = eventType.Trim().ToUpperInvariant();
        if (!NotificationEventTypes.All.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ValidationAppException("The notification event type is not supported.", "eventType");
        }

        return normalized;
    }
}

internal sealed record StoredNotificationPayload(
    IReadOnlyDictionary<string, string> Variables,
    string? DeepLink);

internal sealed class NotificationService(
    DoodhDirectDbContext dbContext,
    SecureTokenGenerator tokenGenerator,
    NotificationTokenProtector tokenProtector,
    IIndiaTimeProvider timeProvider) : INotificationService
{
    public async Task<NotificationPageResult> GetAsync(
        NotificationActor actor,
        NotificationListRequest request,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        if (request.Page < 1)
        {
            throw new ValidationAppException("Page must be at least 1.", "page");
        }
        if (request.PageSize is < 1 or > 100)
        {
            throw new ValidationAppException("Page size must be between 1 and 100.", "pageSize");
        }

        var query = dbContext.Notifications.AsNoTracking().Where(x => x.UserId == actor.UserId);
        if (request.IsRead.HasValue)
        {
            query = request.IsRead.Value
                ? query.Where(x => x.ReadAt != null)
                : query.Where(x => x.ReadAt == null);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new NotificationListItem(
                x.PublicId,
                x.EventType,
                x.Title,
                x.Body,
                x.DeepLink,
                x.ReadAt != null,
                x.CreatedAt,
                x.ReadAt))
            .ToListAsync(cancellationToken);

        return new NotificationPageResult(items, request.Page, request.PageSize, totalCount);
    }

    public async Task<NotificationUnreadCountResult> GetUnreadCountAsync(
        NotificationActor actor,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        var count = await dbContext.Notifications.CountAsync(
            x => x.UserId == actor.UserId && x.ReadAt == null,
            cancellationToken);
        return new NotificationUnreadCountResult(count);
    }

    public async Task MarkReadAsync(
        NotificationActor actor,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        var notification = await dbContext.Notifications.SingleOrDefaultAsync(
            x => x.PublicId == notificationId && x.UserId == actor.UserId,
            cancellationToken)
            ?? throw new NotFoundException("Notification was not found.");
        notification.MarkRead(timeProvider.Now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<UserDeviceResult> RegisterDeviceAsync(
        NotificationActor actor,
        RegisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        ValidateLength(request.DeviceIdentifier, 1, 500, "deviceIdentifier");
        ValidateLength(request.PushToken, 1, 2000, "pushToken");
        ValidateLength(request.Platform, 1, 30, "platform");
        if (request.DeviceName?.Length > 160)
        {
            throw new ValidationAppException("Device name cannot exceed 160 characters.", "deviceName");
        }

        var now = timeProvider.Now;
        var deviceHash = tokenGenerator.Hash(request.DeviceIdentifier.Trim());
        var tokenHash = tokenGenerator.Hash(request.PushToken.Trim());
        var protectedToken = tokenProtector.Protect(request.PushToken);
        var device = await dbContext.UserDevices.SingleOrDefaultAsync(
            x => x.UserId == actor.UserId && x.DeviceIdentifierHash == deviceHash,
            cancellationToken);

        var tokenOwner = await dbContext.UserDevices.SingleOrDefaultAsync(
            x => x.TokenHash == tokenHash && (device == null || x.Id != device.Id),
            cancellationToken);
        if (tokenOwner is not null)
        {
            tokenOwner.Invalidate(now);
        }

        if (device is null)
        {
            device = new UserDevice(
                actor.UserId,
                deviceHash,
                tokenHash,
                protectedToken,
                request.Platform,
                request.DeviceName,
                now);
            dbContext.UserDevices.Add(device);
        }
        else
        {
            device.RotateToken(tokenHash, protectedToken, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return MapDevice(device);
    }

    public async Task<IReadOnlyCollection<NotificationPreferenceResult>> GetPreferencesAsync(
        NotificationActor actor,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        var stored = await dbContext.NotificationPreferences
            .AsNoTracking()
            .Where(x => x.UserId == actor.UserId)
            .ToDictionaryAsync(x => (x.EventType, x.Channel), x => x.IsEnabled, cancellationToken);

        return NotificationEventTypes.All
            .SelectMany(eventType => Enum.GetValues<NotificationChannel>().Select(channel =>
                new NotificationPreferenceResult(
                    eventType,
                    channel,
                    NotificationEventTypes.IsCritical(eventType)
                        || !stored.TryGetValue((eventType, channel), out var enabled)
                        || enabled,
                    NotificationEventTypes.IsCritical(eventType))))
            .ToArray();
    }

    public async Task<NotificationPreferenceResult> UpdatePreferenceAsync(
        NotificationActor actor,
        UpdateNotificationPreferenceRequest request,
        CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        var eventType = NotificationEventWriter.NormalizeEventType(request.EventType);
        if (!Enum.IsDefined(request.Channel))
        {
            throw new ValidationAppException("The notification channel is not supported.", "channel");
        }
        if (NotificationEventTypes.IsCritical(eventType) && !request.IsEnabled)
        {
            throw new BusinessRuleException("Critical notifications cannot be disabled.");
        }

        var preference = await dbContext.NotificationPreferences.SingleOrDefaultAsync(
            x => x.UserId == actor.UserId && x.EventType == eventType && x.Channel == request.Channel,
            cancellationToken);
        if (preference is null)
        {
            preference = new NotificationPreference(actor.UserId, eventType, request.Channel, request.IsEnabled);
            dbContext.NotificationPreferences.Add(preference);
        }
        else
        {
            preference.SetEnabled(request.IsEnabled);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new NotificationPreferenceResult(
            eventType,
            request.Channel,
            preference.IsEnabled,
            NotificationEventTypes.IsCritical(eventType));
    }

    private static UserDeviceResult MapDevice(UserDevice device) => new(
        device.PublicId,
        device.Platform,
        device.DeviceName,
        device.IsActive,
        device.RegisteredAt,
        device.LastSeenAt);

    private static void ValidateActor(NotificationActor actor)
    {
        if (actor.UserId <= 0)
        {
            throw new UnauthorizedAppException();
        }
    }

    private static void ValidateLength(string value, int minimum, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < minimum || value.Trim().Length > maximum)
        {
            throw new ValidationAppException($"{field} must contain between {minimum} and {maximum} characters.", field);
        }
    }
}

internal sealed class NotificationTemplateService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider) : INotificationTemplateService
{
    public async Task<IReadOnlyCollection<NotificationTemplateResult>> GetAsync(
        CancellationToken cancellationToken) =>
        await dbContext.NotificationTemplates
            .AsNoTracking()
            .OrderBy(x => x.EventType)
            .ThenBy(x => x.Channel)
            .ThenBy(x => x.Language)
            .Select(x => new NotificationTemplateResult(
                x.PublicId,
                x.EventType,
                x.Channel,
                x.Language,
                x.TitleTemplate,
                x.BodyTemplate,
                x.IsActive,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);

    public async Task<NotificationTemplateResult> UpdateAsync(
        long actorUserId,
        Guid templateId,
        UpdateNotificationTemplateRequest request,
        CancellationToken cancellationToken)
    {
        if (actorUserId <= 0)
        {
            throw new UnauthorizedAppException();
        }
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.BodyTemplate) || request.BodyTemplate.Trim().Length > 2000)
        {
            throw new ValidationAppException("Body template is required and cannot exceed 2000 characters.", "bodyTemplate");
        }
        if (request.TitleTemplate?.Trim().Length > 240)
        {
            throw new ValidationAppException("Title template cannot exceed 240 characters.", "titleTemplate");
        }
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 1000)
        {
            throw new ValidationAppException("A reason of at most 1000 characters is required.", "reason");
        }

        var template = await dbContext.NotificationTemplates.SingleOrDefaultAsync(
            x => x.PublicId == templateId,
            cancellationToken)
            ?? throw new NotFoundException("Notification template was not found.");
        var oldValue = JsonSerializer.Serialize(new
        {
            template.TitleTemplate,
            template.BodyTemplate,
            template.IsActive
        });
        template.Update(request.TitleTemplate, request.BodyTemplate, request.IsActive);
        var newValue = JsonSerializer.Serialize(new
        {
            template.TitleTemplate,
            template.BodyTemplate,
            template.IsActive
        });
        dbContext.AuditLogs.Add(new AuditLog(
            actorUserId,
            "NOTIFICATION_TEMPLATE_UPDATED",
            nameof(NotificationTemplate),
            template.PublicId.ToString(),
            oldValue,
            newValue,
            null,
            null,
            request.Reason.Trim(),
            timeProvider.Now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new NotificationTemplateResult(
            template.PublicId,
            template.EventType,
            template.Channel,
            template.Language,
            template.TitleTemplate,
            template.BodyTemplate,
            template.IsActive,
            template.CreatedAt,
            template.UpdatedAt);
    }
}
