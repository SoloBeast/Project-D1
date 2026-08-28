using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Branches;
using DoodhDirect.Application.Cameras;
using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Customer;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Dairy;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Reports;
using DoodhDirect.Application.Setup;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Infrastructure.Branches;
using DoodhDirect.Infrastructure.Cameras;
using DoodhDirect.Infrastructure.Catalogue;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Deliveries;
using DoodhDirect.Infrastructure.Dairy;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Reports;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Subscriptions;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
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
            .Validate(
                options => options.IsValidForEnvironment(environment.IsDevelopment()),
                environment.IsDevelopment()
                    ? "Development payment configuration must use Mock or Razorpay with both credentials configured."
                    : "Production payment configuration must use Razorpay with both credentials configured.")
            .ValidateOnStart();
        services.AddOptions<DeliveryOptions>()
            .Bind(configuration.GetSection(DeliveryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MilkTestMediaOptions>()
            .Bind(configuration.GetSection(MilkTestMediaOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase),
                "MilkTestMedia:Provider must be 'Local'.")
            .ValidateOnStart();
        services.AddOptions<CameraStreamOptions>()
            .Bind(configuration.GetSection(CameraStreamOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => environment.IsDevelopment() || !options.IsDevelopmentMock,
                "CameraStreams:Provider DevelopmentMock is prohibited outside Development.")
            .Validate(
                options => !options.IsDevelopmentMock
                    || Uri.TryCreate(options.DevelopmentHlsPlaybackUrl, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps,
                "CameraStreams:DevelopmentHlsPlaybackUrl must be an absolute HTTPS URL when DevelopmentMock is selected.")
            .ValidateOnStart();
        services.AddOptions<AddressGeocodingOptions>()
            .Bind(configuration.GetSection(AddressGeocodingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Enum.GetValues<DoodhDirect.Domain.Notifications.NotificationChannel>()
                    .All(channel => IsSupportedNotificationProvider(options.ProviderFor(channel))),
                "Notifications providers must be 'Unconfigured' or 'DevelopmentMock'.")
            .Validate(
                options => environment.IsDevelopment() || !options.UsesDevelopmentMock,
                "Notifications DevelopmentMock providers are prohibited outside Development.")
            .ValidateOnStart();

        var timeZoneId = configuration["TimeZone"]
            ?? throw new InvalidOperationException("TimeZone configuration is required.");
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        if (!string.Equals(timeZone.Id, timeZoneId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(timeZoneId, "Asia/Calcutta", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configured TimeZone '{timeZoneId}' could not be resolved consistently.");
        }

        services.AddHealthChecks()
            .AddDbContextCheck<DoodhDirectDbContext>("sql-server", tags: ["ready"]);
        services.AddDataProtection()
            .SetApplicationName("DoodhDirect");
        services.AddSingleton<IIndiaTimeProvider>(_ => new IndiaTimeProvider(timeZone));
        services.AddSingleton<IClock, SystemUtcClock>();
        services.AddSingleton<SecureTokenGenerator>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<ICatalogueService, CatalogueService>();
        services.AddScoped<IBranchAllocationService, BranchAllocationService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddSingleton<DeliveryOtpSendGate>();
        services.AddScoped<DeliveryService>();
        services.AddScoped<IDeliveryService>(provider => provider.GetRequiredService<DeliveryService>());
        services.AddScoped<IOneTimeDeliveryCreator>(provider => provider.GetRequiredService<DeliveryService>());
        services.AddScoped<IDairyService, DairyService>();
        services.AddScoped<IMilkTestService, MilkTestService>();
        services.AddScoped<ICameraService, CameraService>();
        services.AddScoped<INotificationEventWriter, NotificationEventWriter>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationTemplateService, NotificationTemplateService>();
        services.AddScoped<INotificationProcessor, NotificationProcessor>();
        services.AddSingleton<NotificationTokenProtector>();
        services.AddSingleton<DeliveryOtpHandoffProtector>();
        services.AddHostedService<NotificationWorker>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<INumberSeriesService, NumberSeriesService>();
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<IBranchService, BranchService>();
        services.AddSingleton<IDeliveryRealtimePublisher, NullDeliveryRealtimePublisher>();
        services.AddSingleton<IMilkTestImageValidator, MilkTestImageValidator>();
        services.AddSingleton<IMediaStorage, LocalMediaStorage>();
        services.AddSingleton<MockPaymentGateway>();
        services.AddHttpClient<RazorpayPaymentGateway>(client =>
        {
            client.BaseAddress = new Uri("https://api.razorpay.com/v1/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IPaymentGateway>(provider =>
            provider.GetRequiredService<RazorpayPaymentGateway>());
        services.AddHttpClient<GoogleAddressLocationLookup>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<AddressGeocodingOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        services.AddScoped<IAddressLocationLookup>(provider =>
            provider.GetRequiredService<GoogleAddressLocationLookup>());
        services.AddScoped<IdentitySeedService>();
        services.AddScoped<DevelopmentCustomerSeedService>();
        services.AddScoped<DevelopmentDeliveryStaffSeedService>();
        services.AddScoped<DevelopmentDairyManagerSeedService>();
        services.AddScoped<DevelopmentUatUserSeedService>();
        services.AddScoped<CatalogueSeedService>();
        services.AddScoped<NotificationTemplateSeedService>();
        services.AddScoped<NumberSeriesSeedService>();
        if (environment.IsDevelopment())
        {
            services.AddScoped<IDevelopmentNotificationService, DevelopmentNotificationService>();
        }
        services.AddSingleton<IOtpDeliveryService>(provider =>
            environment.IsDevelopment()
                ? ActivatorUtilities.CreateInstance<DevelopmentOtpDeliveryService>(provider)
                : new UnconfiguredOtpDeliveryService(provider.GetRequiredService<ILogger<UnconfiguredOtpDeliveryService>>()));
        services.AddSingleton<ICameraStreamGateway>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CameraStreamOptions>>().Value;
            return environment.IsDevelopment() && options.IsDevelopmentMock
                ? ActivatorUtilities.CreateInstance<DevelopmentCameraStreamGateway>(provider)
                : new UnconfiguredCameraStreamGateway();
        });
        foreach (var channel in Enum.GetValues<DoodhDirect.Domain.Notifications.NotificationChannel>())
        {
            services.AddSingleton<INotificationChannelGateway>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<NotificationOptions>>().Value;
                return environment.IsDevelopment()
                    && NotificationOptions.IsDevelopmentMock(options.ProviderFor(channel))
                    ? new DevelopmentNotificationGateway(channel)
                    : new UnconfiguredNotificationGateway(channel);
            });
        }

        return services;
    }

    private static bool IsSupportedNotificationProvider(string provider) =>
        string.Equals(provider, "Unconfigured", StringComparison.OrdinalIgnoreCase)
        || NotificationOptions.IsDevelopmentMock(provider);

    private sealed class SystemUtcClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
