using DoodhDirect.Infrastructure.Catalogue;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Setup;

/// <summary>
/// Runs the reference/identity seed services once at application startup when
/// <c>SeedOptions:EnableDevelopmentSeeds</c> is true. The seed services are all
/// additive and idempotent, so this safely repairs databases that are missing
/// newly-introduced roles, permissions or reference rows (for example the
/// <c>SETUP.REFUND_REPLACEMENT.*</c> grants) without mutating live data.
///
/// Services are executed in dependency order:
/// number series -> catalogue -> identity (roles/permissions) -> development
/// users -> notification templates -> UAT bootstrap. The development user seeds
/// and the UAT bootstrap self-gate on the hosting environment, so enabling the
/// flag is safe in every environment.
/// </summary>
internal sealed class StartupSeedWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SeedOptions> options,
    ILogger<StartupSeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableDevelopmentSeeds)
        {
            logger.LogDebug(
                "Startup seeding is disabled (SeedOptions:EnableDevelopmentSeeds=false).");
            return;
        }

        logger.LogInformation("Startup seeding is enabled; running seed services.");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var provider = scope.ServiceProvider;

            await provider.GetRequiredService<NumberSeriesSeedService>()
                .SeedAsync(stoppingToken);
            await provider.GetRequiredService<CatalogueSeedService>()
                .SeedAsync(stoppingToken);
            await provider.GetRequiredService<IdentitySeedService>()
                .SeedAsync(stoppingToken);
            await provider.GetRequiredService<DevelopmentUatUserSeedService>()
                .SeedAsync(stoppingToken);
            await provider.GetRequiredService<DevelopmentCustomerSeedService>()
                .SeedAsync(stoppingToken);
            await provider.GetRequiredService<DevelopmentDeliveryStaffSeedService>()
                .SeedAsync(stoppingToken);
            await provider.GetRequiredService<DevelopmentDairyManagerSeedService>()
                .SeedAsync(stoppingToken);
            await provider.GetRequiredService<NotificationTemplateSeedService>()
                .SeedAsync(stoppingToken);
            await provider.GetRequiredService<UatBootstrapSeedService>()
                .SeedAsync(stoppingToken);

            logger.LogInformation("Startup seeding completed successfully.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Seeding must never prevent the API from serving traffic. The failure is
            // logged so operators can repair the database and restart.
            logger.LogError(exception, "Startup seeding failed.");
        }
    }
}
