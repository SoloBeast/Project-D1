using DoodhDirect.Application.RefundReplacement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Restart-safe end-of-subscription vacation-refund worker. Polls persisted
/// subscription state (no in-memory timers: a subscription whose term ends
/// while the API is down is picked up on the next cycle after restart) and
/// evaluates due subscriptions through <see cref="IVacationRefundService"/>.
/// Follows the <see cref="DeliveryFailTimeoutWorker"/> pattern: scoped work
/// per cycle, transient failures logged without secrets, and the cycle
/// continues until shutdown is requested. Safe under repeated execution and
/// multiple instances: one logical request per subscription is enforced by
/// the service's serializable transactions and unique constraints.
/// </summary>
internal sealed class VacationRefundWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<VacationRefundOptions> options,
    ILogger<VacationRefundWorker> logger) : BackgroundService
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider
                    .GetRequiredService<IVacationRefundService>();
                var count = await service.ProcessDueAsync(stoppingToken);
                if (count > 0)
                {
                    logger.LogInformation(
                        "Vacation-refund cycle created {RequestCount} requests.",
                        count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Vacation-refund cycle failed.");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
