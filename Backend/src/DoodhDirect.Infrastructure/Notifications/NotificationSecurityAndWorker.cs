using DoodhDirect.Application.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Notifications;

public sealed class DeliveryOtpHandoffProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "DoodhDirect.Deliveries.OtpHandoff.v1");

    public string Protect(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return _protector.Protect(code.Trim());
    }

    public string Unprotect(string protectedCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedCode);
        return _protector.Unprotect(protectedCode);
    }
}

internal sealed class NotificationTokenProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "DoodhDirect.Notifications.PushToken.v1");

    public string Protect(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return _protector.Protect(token.Trim());
    }

    public string Unprotect(string protectedToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedToken);
        return _protector.Unprotect(protectedToken);
    }
}

internal sealed class NotificationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationOptions> options,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<INotificationProcessor>();
                var eventCount = await processor.ProcessPendingEventsAsync(stoppingToken);
                var deliveryCount = await processor.ProcessDueDeliveriesAsync(stoppingToken);

                if (eventCount > 0 || deliveryCount > 0)
                {
                    logger.LogInformation(
                        "Notification cycle processed {EventCount} events and {DeliveryCount} deliveries.",
                        eventCount,
                        deliveryCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Notification processing cycle failed.");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
