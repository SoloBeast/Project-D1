using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Application.Identity;

/// <summary>
/// MSG91 OTP Widget provider contract (reqId-based flow), verified against the
/// official MSG91 docs (2025-08-11).
///
/// Ownership model:
/// - MSG91 generates and delivers the OTP. DoodhDirect NEVER sends or knows
///   the code, so Send carries only the destination (the MSG91 "identifier").
/// - Retry reuses the reqId returned by Send; no destination is sent.
/// - Verify sends reqId plus the client-entered OTP; MSG91 alone confirms it.
/// - VerifyAccessToken returns the provider-attested identifier in
///   data.message; DoodhDirect compares it (normalized) against the challenge
///   Destination. No root-level isValid/mobile or client-submitted mobile is
///   ever trusted.
/// </summary>
public sealed record Msg91OtpSendRequest(string Destination, string Purpose);

public sealed record Msg91OtpRetryRequest(string ReqId);

public sealed record Msg91OtpVerifyRequest(string ReqId, string Destination, string Code, string Purpose);

public sealed record Msg91OtpAccessTokenRequest(string AccessToken, string Destination, string Purpose);

public sealed record Msg91OtpSendResult(string ReqId);

/// <summary>
/// Result of verifyOtp. Only the access token is provider-derived; identity is
/// attested separately via VerifyAccessToken, never inferred from this call.
/// </summary>
public sealed record Msg91OtpVerifyResult(string AccessToken);

/// <summary>
/// Result of verifyAccessToken. VerifiedIdentifier is the provider-attested
/// identifier read from data.message (e.g. normalized mobile/email). A null or
/// blank value means MSG91 did not attest an identifier and the verification
/// MUST fail closed (no session).
/// </summary>
public sealed record Msg91OtpValidationResult(string? VerifiedIdentifier);

public interface IMsg91OtpProvider
{
    Task<Msg91OtpSendResult> SendAsync(Msg91OtpSendRequest request, CancellationToken cancellationToken);

    Task<Msg91OtpSendResult> RetryAsync(Msg91OtpRetryRequest request, CancellationToken cancellationToken);

    Task<Msg91OtpVerifyResult> VerifyAsync(Msg91OtpVerifyRequest request, CancellationToken cancellationToken);

    Task<Msg91OtpValidationResult> ValidateAccessTokenAsync(
        Msg91OtpAccessTokenRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// SystemConfiguration keys backing the OTP provider configuration.
/// The AuthKey value is always stored protected (encrypted at rest) and is
/// never exposed by any read API.
/// </summary>
public static class OtpProviderConfigurationKeys
{
    public const string Provider = "OtpProvider.Provider";
    public const string Enabled = "OtpProvider.Enabled";
    public const string WidgetId = "OtpProvider.WidgetId";
    public const string AuthKey = "OtpProvider.AuthKey";
    public const string Environment = "OtpProvider.Environment";
}

/// <summary>
/// Resolved runtime MSG91 settings used by the provider client. AuthKey is
/// decrypted at call time and never logged or persisted in plain text.
/// </summary>
public sealed record Msg91ProviderSettings(
    string? WidgetId,
    string? AuthKey,
    string? Environment,
    bool IsConfigured);

public interface IMsg91ProviderSettingsProvider
{
    Task<Msg91ProviderSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// OTP provider configuration contract backed by SystemConfiguration storage.
/// The AuthKey is stored encrypted at rest and is NEVER returned by any read API.
/// </summary>
public static class OtpProviderConfiguration
{
    public const string ProviderMsg91 = "MSG91";
    public const string EnvironmentTest = "Test";
    public const string EnvironmentProduction = "Production";
}

public sealed record OtpProviderConfigurationResult(
    string Provider,
    bool Enabled,
    string? WidgetId,
    string? Environment,
    bool Configured,
    string Status);

public sealed record UpdateOtpProviderConfigurationRequest(
    bool? Enabled,
    string? WidgetId,
    string? AuthKey,
    string? Environment);

public sealed record OtpProviderTestResult(bool Success, string Message);

public interface IOtpProviderConfigurationService
{
    Task<OtpProviderConfigurationResult> GetAsync(CancellationToken cancellationToken);

    Task<OtpProviderConfigurationResult> UpdateAsync(
        UpdateOtpProviderConfigurationRequest request,
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);

    Task<OtpProviderTestResult> TestAsync(
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of an OTP send/retry in the application flow. Carries the provider
/// request id (reqId) which is persisted on the OtpChallenge for retry/verify.
/// </summary>
public sealed record SendOtpResult(string ReqId);

public sealed record RetryOtpRequest(string Mobile, OtpPurpose Purpose, string? IpAddress);
