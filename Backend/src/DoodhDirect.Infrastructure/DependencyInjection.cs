using DoodhDirect.Application.Abstractions;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DoodhDirect.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DoodhDirect")
            ?? throw new InvalidOperationException(
                "Connection string 'DoodhDirect' is required. Configure it through environment-specific settings or secrets.");

        services.AddDbContext<DoodhDirectDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(DoodhDirectDbContext).Assembly.FullName);
                sql.EnableRetryOnFailure(maxRetryCount: 5);
                sql.CommandTimeout(30);
            }));

        services.AddHealthChecks()
            .AddDbContextCheck<DoodhDirectDbContext>("sql-server", tags: ["ready"]);
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }

    private sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
