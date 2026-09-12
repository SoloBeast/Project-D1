using DoodhDirect.Application.Payments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Payments;

internal sealed class PaymentReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentReconciliationOptions> options,
    ILogger<PaymentReconciliationWorker> logger) : BackgroundService
{
    private readonly PaymentReconciliationOptions _options = options.Value;
    private readonly TimeSpan _pollInterval =
        TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var coordinator = scope.ServiceProvider
                    .GetRequiredService<IPaymentReconciliationCoordinator>();
                var count = await coordinator.ProcessBatchAsync(
                    _options.BatchSize,
                    _options.CandidateAgeMinutes,
                    stoppingToken);
                if (count > 0)
                {
                    logger.LogInformation(
                        "Payment reconciliation cycle processed {PaymentCount} payments.",
                        count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Payment reconciliation cycle failed.");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
