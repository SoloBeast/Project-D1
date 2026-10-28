using System.ComponentModel.DataAnnotations;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Notifications;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Notifications;

/// <summary>
/// Configuration for the Firebase Cloud Messaging push gateway.
/// All values are server-side only: they are bound from configuration
/// (appsettings and/or environment variables such as
/// <c>Notifications__Fcm__ServiceAccountFile</c>) and must never be embedded
/// in Flutter assets or client bundles.
/// </summary>
public sealed class FcmPushOptions
{
    public const string SectionName = "Notifications:Fcm";

    /// <summary>Provider name selected via <c>Notifications:PushProvider</c>.</summary>
    public const string ProviderName = "Fcm";

    /// <summary>Firebase project id used in the FCM HTTP v1 send endpoint.</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>
    /// Absolute path to the Firebase service-account JSON file on the server.
    /// The file is read at send time; it must never be committed to source control.
    /// </summary>
    public string ServiceAccountFile { get; init; } = string.Empty;

    [Range(5, 120)]
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// Startup validation for <see cref="FcmPushOptions"/>. Required values are
/// enforced only when the Push channel actually selects the FCM provider, so
/// <c>Unconfigured</c> (the safe default) and <c>DevelopmentMock</c> never
/// require Firebase credentials.
/// </summary>
internal sealed class FcmPushOptionsValidator(IOptions<NotificationOptions> notificationOptions)
    : IValidateOptions<FcmPushOptions>
{
    public ValidateOptionsResult Validate(string? name, FcmPushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(
                notificationOptions.Value.PushProvider,
                FcmPushOptions.ProviderName,
                StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.ProjectId))
        {
            return ValidateOptionsResult.Fail(
                "Notifications:Fcm:ProjectId is required when Notifications:PushProvider is 'Fcm'.");
        }

        if (string.IsNullOrWhiteSpace(options.ServiceAccountFile))
        {
            return ValidateOptionsResult.Fail(
                "Notifications:Fcm:ServiceAccountFile is required when Notifications:PushProvider is 'Fcm'. " +
                "Set it to the server-side Firebase service-account JSON path (for example via the " +
                "Notifications__Fcm__ServiceAccountFile environment variable).");
        }

        if (!File.Exists(options.ServiceAccountFile))
        {
            return ValidateOptionsResult.Fail(
                $"The FCM service-account file '{options.ServiceAccountFile}' does not exist.");
        }

        return ValidateOptionsResult.Success;
    }
}
