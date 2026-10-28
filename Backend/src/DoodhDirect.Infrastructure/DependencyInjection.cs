using System.IO;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Branding;
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
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Setup;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Infrastructure.Branding;
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
using DoodhDirect.Infrastructure.RefundReplacement;
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
        services.AddOptions<PaymentReconciliationOptions>()
            .Bind(configuration.GetSection(PaymentReconciliationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<DeliveryFailTimeoutOptions>()
            .Bind(configuration.GetSection(DeliveryFailTimeoutOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<VacationRefundOptions>()
            .Bind(configuration.GetSection(VacationRefundOptions.SectionName))
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
                    .All(channel => IsSupportedNotificationProvider(channel, options.ProviderFor(channel))),
                "Notifications providers must be 'Unconfigured' or 'DevelopmentMock', or 'Fcm' for the Push channel.")
            .ValidateOnStart();
        // FCM credentials are server-side only (service-account file path plus
        // environment overrides). Required values are enforced at startup only
        // when the Push channel actually selects 'Fcm'.
        services.AddOptions<FcmPushOptions>()
            .Bind(configuration.GetSection(FcmPushOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FcmPushOptions>, FcmPushOptionsValidator>();
        services.AddHttpClient(
            FcmPushGateway.HttpClientName,
            (provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<FcmPushOptions>>().Value;
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            });
        services.AddOptions<SeedOptions>()
            .Bind(configuration.GetSection(SeedOptions.SectionName))
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
        // Bank account numbers (and integration/OTP secrets) are encrypted with
        // ASP.NET Core DataProtection. The default key ring is ephemeral: keys
        // are lost on restart/redeploy/scale-out, which would make stored bank
        // details unreadable (fail-closed). Set DataProtection:KeyDirectory to a
        // durable, deployment-managed directory (persisted volume / file share
        // with OS-level ACLs) so keys survive restarts and are shared by every
        // instance. When unset, the ephemeral ring is kept for development only.
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("DoodhDirect");
        var keyDirectory = configuration["DataProtection:KeyDirectory"];
        if (string.IsNullOrWhiteSpace(keyDirectory) && environment.IsDevelopment())
        {
            // Local development convenience default: a durable directory outside
            // the repository, so bank-detail keys survive API restarts on a dev
            // machine with zero per-machine configuration. Explicit configuration
            // (including environment variables) always wins when present.
            // Production and shared environments must set DataProtection:KeyDirectory
            // to their own securely managed durable location; they never use this.
            keyDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DoodhDirect",
                "DataProtection-Keys");
        }
        if (!string.IsNullOrWhiteSpace(keyDirectory))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        }
        services.AddSingleton<IIndiaTimeProvider>(_ => new IndiaTimeProvider(timeZone));
        services.AddSingleton<IClock, SystemUtcClock>();
        services.AddSingleton<SecureTokenGenerator>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<IAuthorizationScopeValidator, AuthorizationScopeValidator>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<ICatalogueService, CatalogueService>();
        services.AddScoped<IBranchAllocationService, BranchAllocationService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IChargeService, ChargeService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddSingleton<DeliveryOtpSendGate>();
        services.AddScoped<DeliveryService>();
        services.AddScoped<IDeliveryService>(provider => provider.GetRequiredService<DeliveryService>());
        services.AddScoped<IOneTimeDeliveryCreator>(provider => provider.GetRequiredService<DeliveryService>());
        services.AddScoped<IDairyService, DairyService>();
        services.AddScoped<IMilkTestService, MilkTestService>();
        services.AddScoped<IRefundReplacementConfigurationService, RefundReplacementConfigurationService>();
        services.AddScoped<IDeliveryFailTicketCreator, DeliveryFailTicketCreator>();
        services.AddScoped<IRefundReplacementService, RefundReplacementService>();
        services.AddScoped<IRefundBankExportService, RefundBankExportService>();
        services.AddScoped<IDeliveryFailTimeoutProcessor, DeliveryFailTimeoutProcessor>();
        services.AddHostedService<DeliveryFailTimeoutWorker>();
        services.AddScoped<IVacationRefundService, VacationRefundService>();
        services.AddHostedService<VacationRefundWorker>();
        services.AddScoped<ICameraService, CameraService>();
        services.AddScoped<INotificationEventWriter, NotificationEventWriter>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationTemplateService, NotificationTemplateService>();
        services.AddScoped<INotificationProcessor, NotificationProcessor>();
        services.AddSingleton<NotificationTokenProtector>();
        services.AddSingleton<DeliveryOtpHandoffProtector>();
        services.AddHostedService<NotificationWorker>();
        services.AddHostedService<PaymentReconciliationWorker>();
        services.AddScoped<PaymentService>();
        services.AddScoped<IPaymentService>(provider => provider.GetRequiredService<PaymentService>());
        services.AddScoped<IPaymentReconciliationCoordinator, PaymentReconciliationCoordinator>();
        services.AddScoped<IWalletService, WalletService>();
        services.AddSingleton<BankDetailProtector>();
        services.AddSingleton<CompanyBankDetailProtector>();
        services.AddSingleton<ManualBankBeneficiaryProtector>();
        services.AddScoped<ICompanyBankService, CompanyBankService>();
        services.AddScoped<IWithdrawalService, WithdrawalService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<INumberSeriesService, NumberSeriesService>();
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<IBranchService, BranchService>();
        services.AddSingleton<IDeliveryRealtimePublisher, NullDeliveryRealtimePublisher>();
        services.AddSingleton<IMilkTestImageValidator, MilkTestImageValidator>();
        services.AddSingleton<IProductImageValidator, ProductImageValidator>();
        services.AddSingleton<IMediaStorage, LocalMediaStorage>();
        services.AddOptions<BrandingMediaOptions>()
            .Bind(configuration.GetSection(BrandingMediaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IBrandingAssetValidator, BrandingAssetValidator>();
        services.AddScoped<IBrandingService, BrandingService>();
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
        services.AddHostedService<StartupSeedWorker>();
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
                if (NotificationOptions.IsDevelopmentMock(options.ProviderFor(channel)))
                {
                    return new DevelopmentNotificationGateway(channel);
                }

                if (channel == DoodhDirect.Domain.Notifications.NotificationChannel.Push
                    && string.Equals(
                        options.PushProvider,
                        FcmPushOptions.ProviderName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return ActivatorUtilities.CreateInstance<FcmPushGateway>(provider);
                }

                return new UnconfiguredNotificationGateway(channel);
            });
        }

        return services;
    }

    private static bool IsSupportedNotificationProvider(
        DoodhDirect.Domain.Notifications.NotificationChannel channel,
        string provider) =>
        string.Equals(provider, "Unconfigured", StringComparison.OrdinalIgnoreCase)
        || NotificationOptions.IsDevelopmentMock(provider)
        || (channel == DoodhDirect.Domain.Notifications.NotificationChannel.Push
            && string.Equals(provider, FcmPushOptions.ProviderName, StringComparison.OrdinalIgnoreCase));

    private sealed class SystemUtcClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
