using System.Collections.Concurrent;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using Microsoft.Extensions.Logging;

namespace DoodhDirect.Infrastructure.OtpProvider;

/// <summary>
/// Explicit test infrastructure provider that mirrors the MSG91 widget v5 flow without
/// real credentials. It generates its own test code, owns the code→reqId mapping,
/// verifies submitted codes, and returns a synthetic access token plus a synthetic
/// provider-attested identifier (digits-only, matching MSG91's <c>data.message</c> shape).
/// This type is NEVER auto-registered as the normal Development provider — it exists
/// only for explicit test harnesses (see DependencyInjection).
/// </summary>
public sealed class DevelopmentMsg91OtpProvider(
    ILogger<DevelopmentMsg91OtpProvider> logger) : IMsg91OtpProvider
{
    private const string TestCode = "123456";
    private readonly ConcurrentDictionary<string, (string Code, string Destination)> _requests = new(StringComparer.Ordinal);

    public Task<Msg91OtpSendResult> SendAsync(
        Msg91OtpSendRequest request,
        CancellationToken cancellationToken)
    {
        var reqId = $"dev-{Guid.NewGuid():N}";
        _requests[reqId] = (TestCode, request.Destination);
        LogCode(request.Destination, TestCode);
        return Task.FromResult(new Msg91OtpSendResult(reqId));
    }

    public Task<Msg91OtpSendResult> RetryAsync(
        Msg91OtpRetryRequest request,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "[DEV-OTP] Re-sending OTP for provider request {ReqId}.",
            request.ReqId);
        return Task.FromResult(new Msg91OtpSendResult(request.ReqId));
    }

    public Task<Msg91OtpVerifyResult> VerifyAsync(
        Msg91OtpVerifyRequest request,
        CancellationToken cancellationToken)
    {
        if (!_requests.TryGetValue(request.ReqId, out var entry) ||
            !string.Equals(entry.Code, request.Code, StringComparison.Ordinal))
        {
            logger.LogWarning("[DEV-OTP] Rejecting OTP verification for {ReqId}.", request.ReqId);
            throw new OtpProviderRejectedException("The OTP verification could not be confirmed.");
        }

        logger.LogWarning("[DEV-OTP] Verifying OTP for provider request {ReqId}.", request.ReqId);
        return Task.FromResult(new Msg91OtpVerifyResult($"dev-access-{Guid.NewGuid():N}"));
    }

    public Task<Msg91OtpValidationResult> ValidateAccessTokenAsync(
        Msg91OtpAccessTokenRequest request,
        CancellationToken cancellationToken)
    {
        logger.LogWarning("[DEV-OTP] Validating access token for {Destination}.", request.Destination);

        // Mirror MSG91's data.message shape: a digits-only identifier. The challenge
        // destination is stored as submitted (e.g. "+919999999999"); strip non-digits
        // so the identity comparison in the services matches.
        var digits = new string(request.Destination.Where(c => c is >= '0' and <= '9').ToArray());
        return Task.FromResult(new Msg91OtpValidationResult(digits));
    }

    private void LogCode(string destination, string code)
    {
        var suffix = destination.Length > 4 ? destination[^4..] : destination;
        logger.LogWarning(
            "[DEV-OTP] Development OTP for {DestinationSuffix}: {Code}",
            suffix,
            code);
    }
}
