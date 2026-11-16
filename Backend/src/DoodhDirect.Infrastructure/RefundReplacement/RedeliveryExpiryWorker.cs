using DoodhDirect.Application.RefundReplacement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Restart-safe expiry-notice worker. Polls persisted
/// <c>CustomerChoiceAt + 1 hour</c> deadlines (no in-memory timers: a ticket
/// whose window passes while the API is down is picked up on the next cycle
/// after restart) and ensures customer notices through
/// <see cref="IRedeliveryExpiryProcessor"/>. Follows the
/// <see cref="DeliveryFailTimeoutWorker"/> pattern: scoped work per cycle,
/// per-ticket failures logged without stopping the batch, and the cycle
/// continues until shutdown is requested. Notice-only: never converts,
/// approves, or moves money.
/// </summary>
internal sealed class RedeliveryExpiryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RedeliveryExpiryOptions> options,
    ILogger<RedeliveryExpiryWorker> logger) : BackgroundService
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
                    .GetRequiredService<IRedeliveryExpiryProcessor>();
                var count = await processor.ProcessExpiredRedeliveriesAsync(stoppingToken);
                if (count > 0)
                {
                    logger.LogInformation(
                        "Redelivery expiry sweep notified {TicketCount} tickets.",
                        count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Redelivery expiry sweep cycle failed.");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
