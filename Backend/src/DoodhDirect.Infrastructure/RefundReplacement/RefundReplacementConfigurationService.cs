using System.Globalization;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Admin configuration service for the refund/replacement request window.
/// Persists the window (in hours) in SystemConfiguration. The window is
/// server-authoritative: the submission deadline for a normal request is
/// computed by the backend as Delivery.CompletedAt + WindowHours. Every
/// mutation is audited with non-secret metadata only.
/// </summary>
public sealed class RefundReplacementConfigurationService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider) : IRefundReplacementConfigurationService
{
    public const string ActionConfigUpdated = "REFUND_REPLACEMENT.CONFIG_UPDATED";

    private const string EntityType = "RefundReplacementConfiguration";
    private const string EntityId = "RequestWindow";

    /// <summary>
    /// Default window when the setting has not been configured yet (48 hours).
    /// </summary>
    public const int DefaultWindowHours = 48;

    /// <summary>
    /// Valid range for the window (hours). Must be a positive integer so the
    /// deadline is always strictly after the delivery completion timestamp.
    /// </summary>
    public const int MinimumWindowHours = 1;
    public const int MaximumWindowHours = 24 * 365;

    public async Task<RefundReplacementConfigurationResult> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken);
        var windowHours = ReadWindowHours(settings);

        return ToResult(windowHours);
    }

    public async Task<RefundReplacementConfigurationResult> UpdateAsync(
        UpdateRefundReplacementConfigurationRequest request,
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await LoadAsync(cancellationToken);
        var before = ReadWindowHours(settings);

        var now = timeProvider.Now;
        var windowHours = request.WindowHours ?? before;
        windowHours = NormalizeWindowHours(windowHours);

        await SetValueAsync(
            settings,
            RefundReplacementConfigurationKeys.WindowHours,
            windowHours.ToString(CultureInfo.InvariantCulture),
            "int",
            "Refund/replacement request window in whole hours. The deadline for a normal request is delivery completion + this window.",
            now,
            cancellationToken);

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionConfigUpdated,
            EntityType,
            EntityId,
            JsonSerializer.Serialize(new
            {
                windowHours = before
            }),
            JsonSerializer.Serialize(new
            {
                windowHours
            }),
            ipAddress,
            userAgent,
            "Refund/replacement request window updated.",
            now));

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResult(windowHours);
    }

    private async Task<Dictionary<string, SystemConfiguration>> LoadAsync(
        CancellationToken cancellationToken) =>
        await dbContext.SystemConfigurations
            .AsNoTracking()
            .Where(x => x.Key.StartsWith(RefundReplacementConfigurationKeys.Prefix))
            .ToDictionaryAsync(x => x.Key, StringComparer.Ordinal, cancellationToken);

    private async Task SetValueAsync(
        Dictionary<string, SystemConfiguration> settings,
        string key,
        string value,
        string valueType,
        string description,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SystemConfigurations
            .SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (entity is null)
        {
            entity = new SystemConfiguration(
                key,
                value,
                valueType,
                description,
                false);
            dbContext.SystemConfigurations.Add(entity);
        }
        else
        {
            // UpdateValue -> SetUpdated requires an India-local wall-clock value
            // (unspecified kind); timeProvider.Now already is that value.
            entity.UpdateValue(value, now);
        }

        // Keep the in-memory snapshot in sync so the response can reflect the
        // persisted state immediately (including rows created on this request).
        settings[key] = entity;
    }

    private static int NormalizeWindowHours(int windowHours)
    {
        if (windowHours < MinimumWindowHours || windowHours > MaximumWindowHours)
        {
            throw new ValidationAppException(
                $"The refund/replacement request window must be between {MinimumWindowHours} and {MaximumWindowHours} hours.",
                "windowHours");
        }

        return windowHours;
    }

    private static int ReadWindowHours(IReadOnlyDictionary<string, SystemConfiguration> settings)
    {
        if (!settings.TryGetValue(RefundReplacementConfigurationKeys.WindowHours, out var entity))
        {
            return DefaultWindowHours;
        }

        if (int.TryParse(entity.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= MinimumWindowHours
            && value <= MaximumWindowHours)
        {
            return value;
        }

        return DefaultWindowHours;
    }

    private static RefundReplacementConfigurationResult ToResult(int windowHours) =>
        new(
            windowHours,
            windowHours > 0 ? "Configured" : "Not Configured");
}
