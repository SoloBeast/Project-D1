using DoodhDirect.Application.RefundReplacement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// D1-C restart-safe timeout worker. Polls the persisted customer-choice
/// deadlines (no in-memory timers: a ticket whose deadline passes while the
/// API is down is picked up on the next cycle after restart) and resolves
/// expired tickets through <see cref="IDeliveryFailTimeoutProcessor"/>.
/// Follows the <see cref="Notifications.NotificationWorker"/> pattern:
/// scoped work per cycle, transient failures logged without secrets, and the
/// cycle continues until shutdown is requested.
/// </summary>
internal sealed class DeliveryFailTimeoutWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DeliveryFailTimeoutOptions> options,
    ILogger<DeliveryFailTimeoutWorker> logger) : BackgroundService
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider
                    .GetRequiredService<IDeliveryFailTimeoutProcessor>();
                var count = await processor.ProcessExpiredTicketsAsync(stoppingToken);
                if (count > 0)
                {
                    logger.LogInformation(
                        "Delivery-fail timeout cycle resolved {TicketCount} tickets.",
                        count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Delivery-fail timeout cycle failed.");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
