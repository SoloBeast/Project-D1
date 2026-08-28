using DoodhDirect.Application.Identity;
using Microsoft.Extensions.Logging;

namespace DoodhDirect.Infrastructure.Identity;

/// <summary>
/// Development-only OTP delivery that logs the code so the invitation
/// onboarding flow can be manually verified in a local environment.
/// Registered ONLY when the environment is Development. In any other
/// environment <see cref="UnconfiguredOtpDeliveryService"/> is used, so
/// production behavior is unchanged (no OTP provider means the send fails closed).
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
