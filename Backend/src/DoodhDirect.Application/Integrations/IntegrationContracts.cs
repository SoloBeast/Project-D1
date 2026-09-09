namespace DoodhDirect.Application.Integrations;

/// <summary>
/// Well-known runtime integration setting keys stored in <see cref="DoodhDirect.Domain.Configuration.SystemConfiguration"/>.
/// Values written here override the appsettings/environment fallback values at call time.
/// </summary>
public static class IntegrationConfigurationKeys
{
    // Email (SMTP) delivery ---------------------------------------------------
    public const string EmailFromAddress = "Integration.Email.FromAddress";
    public const string EmailFromName = "Integration.Email.FromName";
    public const string EmailHost = "Integration.Email.Host";
    public const string EmailPort = "Integration.Email.Port";
    public const string EmailUserName = "Integration.Email.UserName";
    public const string EmailPassword = "Integration.Email.Password";
    public const string EmailUseSsl = "Integration.Email.UseSsl";

    /// <summary>Public base URL used to build absolute invitation links (for example https://app.example.com).</summary>
    public const string InviteUrlBase = "Integration.InviteUrlBase";

    // Razorpay ----------------------------------------------------------------
    public const string RazorpayKeyId = "Integration.Razorpay.KeyId";
    public const string RazorpayKeySecret = "Integration.Razorpay.KeySecret";
    public const string RazorpayWebhookSecret = "Integration.Razorpay.WebhookSecret";

    // Google Maps -------------------------------------------------------------
    public const string GoogleMapsApiKey = "Integration.GoogleMaps.ApiKey";
    public const string GoogleMapsBaseUrl = "Integration.GoogleMaps.BaseUrl";

    /// <summary>
    /// Client-side (browser) Google Maps API key used by the Flutter Web client. Stored plainly
    /// (NOT protected) because it is intentionally client-visible and restricted by HTTP referrer
    /// rather than secrecy. Never used by the backend; only the Web Client Key is exposed through
    /// the client-safe configuration surface.
    /// </summary>
    public const string GoogleMapsWebClientKey = "Integration.GoogleMaps.WebClientKey";
}

/// <summary>Email (SMTP) delivery settings resolved at call time.</summary>
public sealed record EmailDeliverySettings(
    string? FromAddress,
    string? FromName,
    string? Host,
    int Port,
    string? UserName,
    string? Password,
    bool UseSsl,
    bool IsConfigured);

/// <summary>Razorpay credentials resolved at call time.</summary>
public sealed record RazorpayRuntimeSettings(
    string? KeyId,
    string? KeySecret,
    string? WebhookSecret,
    bool IsConfigured);

/// <summary>Google Maps server-side credentials resolved at call time.</summary>
public sealed record GoogleMapsRuntimeSettings(
    string? ApiKey,
    string? BaseUrl,
    bool IsConfigured);

/// <summary>
/// Reads the effective integration settings at call time. A runtime (database) value overrides the
/// appsettings/environment fallback whenever it is present and non-empty.
/// </summary>
public interface IIntegrationSettingsProvider
{
    Task<EmailDeliverySettings> GetEmailAsync(CancellationToken cancellationToken);
    Task<RazorpayRuntimeSettings> GetRazorpayAsync(CancellationToken cancellationToken);
    Task<GoogleMapsRuntimeSettings> GetGoogleMapsAsync(CancellationToken cancellationToken);
    Task<string?> GetInviteUrlBaseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Client-visible configuration that is safe to send to authenticated Flutter clients.
/// Only intentionally public values are ever exposed here (for example the Google Maps
/// Web Client Key, which is restricted by HTTP referrer and is not a secret). Server-only
/// secrets (the Google Maps server-side key, SMTP credentials, Razorpay secrets, MSG91
/// AuthKey, database credentials, JWT signing key) are never returned through this surface.
/// </summary>
public sealed record ClientConfigurationResult(
    string? GoogleMapsWebClientKey);

/// <summary>
/// Resolves configuration values that are safe to return to the authenticated client.
/// </summary>
public interface IClientConfigurationService
{
    /// <summary>
    /// Returns the Google Maps Web Client Key (browser key) used by the Flutter Web client,
    /// or null when it is not configured. The server-side Google Maps key is never returned.
    /// </summary>
    Task<string?> GetGoogleMapsWebClientKeyAsync(CancellationToken cancellationToken);
}

public static class IntegrationConfiguration
{
    public const string Email = "Email";
    public const string Razorpay = "Razorpay";
    public const string GoogleMaps = "GoogleMaps";
}

/// <summary>
/// Effective integration configuration returned to the System Setup screen. Sensitive values are
/// never returned; only their <c>Configured</c> flags are exposed (write-only secrets).
/// </summary>
public sealed record IntegrationConfigurationResult(
    string? EmailFromAddress,
    string? EmailFromName,
    string? EmailHost,
    int EmailPort,
    string? EmailUserName,
    bool EmailUseSsl,
    bool EmailPasswordConfigured,
    bool EmailIsConfigured,
    string? InviteUrlBase,
    string? RazorpayKeyId,
    bool RazorpayKeySecretConfigured,
    bool RazorpayWebhookSecretConfigured,
    bool RazorpayIsConfigured,
    string? GoogleMapsBaseUrl,
    bool GoogleMapsApiKeyConfigured,
    bool GoogleMapsIsConfigured,
    string? GoogleMapsWebClientKey);

/// <summary>
/// Update request for the integration settings. Every field is optional so a partial update leaves
/// unrelated settings untouched. A provided non-empty value writes (or replaces) the database
/// override; a provided empty value clears the override so the environment/appsettings fallback
/// applies again. Sensitive values are write-only: they are stored protected and never returned.
/// </summary>
public sealed record UpdateIntegrationConfigurationRequest(
    string? EmailFromAddress = null,
    string? EmailFromName = null,
    string? EmailHost = null,
    int? EmailPort = null,
    string? EmailUserName = null,
    string? EmailPassword = null,
    bool? EmailUseSsl = null,
    string? InviteUrlBase = null,
    string? RazorpayKeyId = null,
    string? RazorpayKeySecret = null,
    string? RazorpayWebhookSecret = null,
    string? GoogleMapsApiKey = null,
    string? GoogleMapsBaseUrl = null,
    string? GoogleMapsWebClientKey = null);

public sealed record IntegrationTestResult(bool Success, string Message);

public interface IIntegrationConfigurationService
{
    Task<IntegrationConfigurationResult> GetAsync(CancellationToken cancellationToken);

    Task<IntegrationConfigurationResult> UpdateAsync(
        UpdateIntegrationConfigurationRequest request,
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);

    Task<IntegrationTestResult> TestAsync(
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);
}

/// <summary>Abstraction for delivering an email through the configured SMTP relay.</summary>
public interface IEmailSender
{
    /// <summary>
    /// Attempts to send the message. Returns <see cref="EmailSendResult.Sent"/> = false (without
    /// throwing) when SMTP is not configured; transport failures throw and are handled by the caller.
    /// </summary>
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string PlainTextBody,
    string? HtmlBody = null);

public sealed record EmailSendResult(bool Sent, string? Reason);
