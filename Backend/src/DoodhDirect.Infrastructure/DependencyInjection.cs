using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Customer;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Infrastructure.Catalogue;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<IdentityOptions>()
            .Bind(configuration.GetSection(IdentityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<PaymentOptions>()
            .Bind(configuration.GetSection(PaymentOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHealthChecks()
            .AddDbContextCheck<DoodhDirectDbContext>("sql-server", tags: ["ready"]);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<SecureTokenGenerator>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<ICatalogueService, CatalogueService>();
        services.AddScoped<IBranchAllocationService, BranchAllocationService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IWalletService, WalletService>();
        services.AddSingleton<MockPaymentGateway>();
        services.AddHttpClient<RazorpayPaymentGateway>(client =>
        {
            client.BaseAddress = new Uri("https://api.razorpay.com/v1/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IPaymentGateway>(provider =>
        {
            var paymentOptions = provider.GetRequiredService<IOptions<PaymentOptions>>().Value;
            return paymentOptions.IsRazorpay
                ? provider.GetRequiredService<RazorpayPaymentGateway>()
                : provider.GetRequiredService<MockPaymentGateway>();
        });
        services.AddSingleton<IAddressLocationLookup, UnconfiguredAddressLocationLookup>();
        services.AddScoped<IdentitySeedService>();
        services.AddScoped<DevelopmentCustomerSeedService>();
        services.AddScoped<CatalogueSeedService>();
        services.AddSingleton<IOtpDeliveryService, UnconfiguredOtpDeliveryService>();

        return services;
    }

    private sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
