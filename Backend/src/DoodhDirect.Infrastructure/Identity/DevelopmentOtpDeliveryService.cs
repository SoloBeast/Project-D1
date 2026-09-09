using DoodhDirect.Application.Deliveries;
using Microsoft.Extensions.Logging;

namespace DoodhDirect.Infrastructure.Identity;

/// <summary>
/// Test-only OTP transport that logs the code so the delivery OTP handoff can
/// be manually verified in a local environment. This is explicit test
/// infrastructure and is never registered as a normal runtime fallback. The
/// DI container always registers <see cref="UnconfiguredOtpDeliveryService"/>,
/// which fails closed when the delivery OTP transport is not configured.
/// </summary>
public sealed class DevelopmentOtpDeliveryService(ILogger<DevelopmentOtpDeliveryService> logger) : IOtpDeliveryService
{
    public Task SendAsync(string destination, string code, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[DEVELOPMENT ONLY] OTP for destination ending {DestinationSuffix} is {OtpCode}.",
            destination.Length > 4 ? destination[^4..] : "unknown",
            code);
        return Task.CompletedTask;
    }
}
