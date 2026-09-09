using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Branches;
using DoodhDirect.Application.Cameras;
using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Customer;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Dairy;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Integrations;
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
using DoodhDirect.Infrastructure.Integrations;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.OtpProvider;
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
            .ValidateOnStart();
        // NOTE: Razorpay credentials are not validated at boot. They may be configured at
        // runtime through the Integration settings store (Integration.Razorpay.*), which
        // overrides static appsettings values per call.
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
        services.AddScoped<UatBootstrapSeedService>();
        services.AddScoped<CatalogueSeedService>();
        services.AddScoped<NotificationTemplateSeedService>();
        services.AddScoped<NumberSeriesSeedService>();
        if (environment.IsDevelopment())
        {
            services.AddScoped<IDevelopmentNotificationService, DevelopmentNotificationService>();
        }
        // Delivery OTP transport selection is configuration-driven. There is no default
        // Development logging fake; the Unconfigured transport keeps delivery OTPs
        // pending (fail closed) until a real configured provider is available.
        services.AddSingleton<IOtpDeliveryService>(provider =>
            new UnconfiguredOtpDeliveryService(provider.GetRequiredService<ILogger<UnconfiguredOtpDeliveryService>>()));
        services.AddSingleton<OtpProviderSecretProtector>();
        services.AddScoped<IMsg91ProviderSettingsProvider, Msg91ProviderSettingsProvider>();
        services.AddScoped<IOtpProviderConfigurationService, OtpProviderConfigurationService>();
        // Integration (SMTP / Razorpay runtime / Google Maps runtime) configuration is
        // stored as SystemConfiguration rows. Secrets are protected with DataProtection
        // and DB values override environment/appsettings at runtime.
        services.AddSingleton<IntegrationSecretProtector>();
        services.AddScoped<IIntegrationSettingsProvider, IntegrationSettingsProvider>();
        services.AddScoped<IIntegrationConfigurationService, IntegrationConfigurationService>();
        services.AddScoped<IClientConfigurationService, ClientConfigurationService>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddHttpClient<Msg91ApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.msg91.com/api/v5/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        // MSG91 provider selection is configuration-driven: the real MSG91 client is
        // always used and fails closed (503 OTP_PROVIDER_UNAVAILABLE) when the provider
        // is not configured. There is no silent fallback to a Development logging fake.
        services.AddScoped<IMsg91OtpProvider>(provider =>
            provider.GetRequiredService<Msg91ApiClient>());
        services.AddSingleton<ICameraStreamGateway>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CameraStreamOptions>>().Value;
            return options.IsDevelopmentMock
                ? ActivatorUtilities.CreateInstance<DevelopmentCameraStreamGateway>(provider)
                : new UnconfiguredCameraStreamGateway();
        });
        foreach (var channel in Enum.GetValues<DoodhDirect.Domain.Notifications.NotificationChannel>())
        {
            services.AddSingleton<INotificationChannelGateway>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<NotificationOptions>>().Value;
                return NotificationOptions.IsDevelopmentMock(options.ProviderFor(channel))
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
