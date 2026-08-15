using DoodhDirect.Application.Identity;
using Microsoft.Extensions.Logging;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class UnconfiguredOtpDeliveryService(ILogger<UnconfiguredOtpDeliveryService> logger) : IOtpDeliveryService
{
    public Task SendAsync(string destination, string code, CancellationToken cancellationToken)
    {
        logger.LogWarning("OTP delivery is not configured for destination ending {DestinationSuffix}.", destination.Length > 4 ? destination[^4..] : "unknown");
        throw new InvalidOperationException("An OTP delivery provider is not configured.");
    }
}
