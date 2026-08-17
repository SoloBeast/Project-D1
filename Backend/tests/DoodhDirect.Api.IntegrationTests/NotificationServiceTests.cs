using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class NotificationServiceTests
{
    [Fact]
    public async Task Processor_materializes_renders_and_delivers_all_configured_channels()
    {
        await using var harness = await NotificationHarness.CreateAsync();
        await harness.RegisterDeviceAsync(harness.Customer, "customer-device", "push-token");
        await harness.AddTemplatesAsync(
            NotificationEventTypes.OrderCreated,
            "Order {{orderNumber}}",
            "Order {{orderNumber}} is ready.");
        await harness.AddEventAsync(
            harness.Customer.Id,
            NotificationEventTypes.OrderCreated,
            "order:100:created",
            new Dictionary<string, string> { ["orderNumber"] = "DD-100" },
            "/orders/100");

        Assert.Equal(1, await harness.Processor.ProcessPendingEventsAsync(default));
        Assert.Equal(4, await harness.Processor.ProcessDueDeliveriesAsync(default));

        var notification = await harness.Db.Notifications
            .AsNoTracking()
            .Include(x => x.Deliveries)
            .SingleAsync();
        Assert.Equal("Order DD-100", notification.Title);
        Assert.Equal("Order DD-100 is ready.", notification.Body);
        Assert.Equal("/orders/100", notification.DeepLink);
        Assert.Equal(4, notification.Deliveries.Count);
        Assert.All(notification.Deliveries, delivery =>
        {
            Assert.Equal(NotificationDeliveryStatus.Delivered, delivery.Status);
            Assert.Equal(1, delivery.AttemptCount);
        });

        var attempts = await harness.Db.NotificationAttempts.AsNoTracking().ToListAsync();
        Assert.Equal(4, attempts.Count);
        Assert.All(attempts, attempt => Assert.Equal(NotificationAttemptOutcome.Delivered, attempt.Outcome));
        Assert.Equal("push-token", harness.Gateway(NotificationChannel.Push).Messages.Single().Destination);
        Assert.Equal("9999999999", harness.Gateway(NotificationChannel.Sms).Messages.Single().Destination);
        Assert.Equal("customer@example.com", harness.Gateway(NotificationChannel.Email).Messages.Single().Destination);
    }

    [Fact]
    public async Task Preferences_suppress_optional_channels_and_reject_disabling_critical_events()
    {
        await using var harness = await NotificationHarness.CreateAsync();
        var actor = new NotificationActor(harness.Customer.Id);
        await harness.AddTemplatesAsync(
            NotificationEventTypes.OrderCreated,
            "Order created",
            "Your order was created.",
            NotificationChannel.Sms);
        var preference = await harness.Service.UpdatePreferenceAsync(
            actor,
            new UpdateNotificationPreferenceRequest(
                NotificationEventTypes.OrderCreated,
                NotificationChannel.Sms,
                false),
            default);
        Assert.False(preference.IsEnabled);
        Assert.False(preference.IsCritical);

        await harness.AddEventAsync(
            harness.Customer.Id,
            NotificationEventTypes.OrderCreated,
            "order:optional:suppressed");
        Assert.Equal(1, await harness.Processor.ProcessPendingEventsAsync(default));

        var delivery = await harness.Db.NotificationDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Suppressed, delivery.Status);
        Assert.Equal("PREFERENCE_SUPPRESSED", delivery.FailureCode);
        Assert.Empty(harness.Gateway(NotificationChannel.Sms).Messages);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.UpdatePreferenceAsync(
            actor,
            new UpdateNotificationPreferenceRequest(
                NotificationEventTypes.PaymentFailed,
                NotificationChannel.Sms,
                false),
            default));
    }

    [Fact]
    public async Task Device_registration_rotates_tokens_and_transfers_token_ownership()
    {
        await using var harness = await NotificationHarness.CreateAsync();
        var firstRegistration = await harness.RegisterDeviceAsync(
            harness.Customer,
            "customer-device",
            "token-one");
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        var rotated = await harness.RegisterDeviceAsync(
            harness.Customer,
            "customer-device",
            "token-two");

        Assert.Equal(firstRegistration.DeviceId, rotated.DeviceId);
        var customerDevice = await harness.Db.UserDevices.AsNoTracking().SingleAsync();
        Assert.True(customerDevice.IsActive);
        Assert.Equal(harness.TokenGenerator.Hash("token-two"), customerDevice.TokenHash);
        Assert.Equal("token-two", harness.TokenProtector.Unprotect(customerDevice.ProtectedToken));
        Assert.Equal(harness.Clock.UtcNow, customerDevice.LastSeenAtUtc);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.RegisterDeviceAsync(harness.OtherCustomer, "other-device", "token-two");

        var devices = await harness.Db.UserDevices.AsNoTracking().OrderBy(x => x.UserId).ToListAsync();
        var oldOwner = devices.Single(x => x.UserId == harness.Customer.Id);
        var newOwner = devices.Single(x => x.UserId == harness.OtherCustomer.Id);
        Assert.False(oldOwner.IsActive);
        Assert.Equal(harness.Clock.UtcNow, oldOwner.InvalidatedAtUtc);
        Assert.True(newOwner.IsActive);
        Assert.Null(newOwner.InvalidatedAtUtc);
        Assert.Equal(harness.TokenGenerator.Hash("token-two"), newOwner.TokenHash);
    }

    [Fact]
    public async Task Retryable_failures_use_exponential_delays_and_stop_at_max_attempts()
    {
        await using var harness = await NotificationHarness.CreateAsync(maxAttempts: 3, retryDelayMinutes: 2);
        await harness.RegisterDeviceAsync(harness.Customer, "customer-device", "push-token");
        await harness.AddTemplatesAsync(
            NotificationEventTypes.OrderCreated,
            "Order created",
            "Your order was created.",
            NotificationChannel.Push);
        var push = harness.Gateway(NotificationChannel.Push);
        push.Enqueue(Retryable("TEMPORARY_ONE"));
        push.Enqueue(Retryable("TEMPORARY_TWO"));
        push.Enqueue(Retryable("TEMPORARY_THREE"));
        await harness.AddEventAsync(
            harness.Customer.Id,
            NotificationEventTypes.OrderCreated,
            "order:retry:created");
        await harness.Processor.ProcessPendingEventsAsync(default);

        Assert.Equal(1, await harness.Processor.ProcessDueDeliveriesAsync(default));
        var delivery = await harness.ReloadDeliveryAsync();
        Assert.Equal(NotificationDeliveryStatus.RetryScheduled, delivery.Status);
        Assert.Equal(harness.Clock.UtcNow.AddMinutes(2), delivery.NextAttemptAtUtc);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await harness.Processor.ProcessDueDeliveriesAsync(default));
        delivery = await harness.ReloadDeliveryAsync();
        Assert.Equal(NotificationDeliveryStatus.RetryScheduled, delivery.Status);
        Assert.Equal(harness.Clock.UtcNow.AddMinutes(4), delivery.NextAttemptAtUtc);

        harness.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(1, await harness.Processor.ProcessDueDeliveriesAsync(default));
        delivery = await harness.ReloadDeliveryAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(3, delivery.AttemptCount);
        Assert.Equal("TEMPORARY_THREE", delivery.FailureCode);
        Assert.Null(delivery.NextAttemptAtUtc);

        var attempts = await harness.Db.NotificationAttempts.AsNoTracking().OrderBy(x => x.AttemptNumber).ToListAsync();
        Assert.Equal([1, 2, 3], attempts.Select(x => x.AttemptNumber).ToArray());
    }

    [Fact]
    public async Task Provider_can_permanently_fail_delivery_and_invalidate_push_destination()
    {
        await using var harness = await NotificationHarness.CreateAsync();
        await harness.RegisterDeviceAsync(harness.Customer, "customer-device", "invalid-token");
        await harness.AddTemplatesAsync(
            NotificationEventTypes.OrderCreated,
            "Order created",
            "Your order was created.",
            NotificationChannel.Push);
        harness.Gateway(NotificationChannel.Push).Enqueue(new NotificationProviderResult(
            NotificationAttemptOutcome.PermanentFailure,
            FailureCode: "TOKEN_INVALID",
            FailureMessage: "The token is invalid.",
            InvalidateDestination: true));
        await harness.AddEventAsync(
            harness.Customer.Id,
            NotificationEventTypes.OrderCreated,
            "order:invalid-token:created");

        await harness.Processor.ProcessPendingEventsAsync(default);
        Assert.Equal(1, await harness.Processor.ProcessDueDeliveriesAsync(default));

        var delivery = await harness.ReloadDeliveryAsync();
        var device = await harness.Db.UserDevices.AsNoTracking().SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Equal("TOKEN_INVALID", delivery.FailureCode);
        Assert.False(device.IsActive);
        Assert.Equal(harness.Clock.UtcNow, device.InvalidatedAtUtc);
    }

    [Fact]
    public async Task Inbox_enforces_ownership_and_mark_read_is_idempotent()
    {
        await using var harness = await NotificationHarness.CreateAsync();
        await harness.AddTemplatesAsync(
            NotificationEventTypes.OrderCreated,
            "Order created",
            "Your order was created.",
            NotificationChannel.Email);
        await harness.AddEventAsync(
            harness.Customer.Id,
            NotificationEventTypes.OrderCreated,
            "order:inbox:created",
            deepLink: "/orders/inbox");
        await harness.Processor.ProcessPendingEventsAsync(default);

        var actor = new NotificationActor(harness.Customer.Id);
        var page = await harness.Service.GetAsync(actor, new NotificationListRequest(), default);
        Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.False(page.Items.Single().IsRead);
        Assert.Equal(1, (await harness.Service.GetUnreadCountAsync(actor, default)).UnreadCount);

        var notificationId = page.Items.Single().NotificationId;
        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.MarkReadAsync(
            new NotificationActor(harness.OtherCustomer.Id),
            notificationId,
            default));

        await harness.Service.MarkReadAsync(actor, notificationId, default);
        var firstReadAt = (await harness.Db.Notifications.AsNoTracking().SingleAsync()).ReadAtUtc;
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Service.MarkReadAsync(actor, notificationId, default);
        var notification = await harness.Db.Notifications.AsNoTracking().SingleAsync();
        Assert.Equal(firstReadAt, notification.ReadAtUtc);
        Assert.Equal(0, (await harness.Service.GetUnreadCountAsync(actor, default)).UnreadCount);
    }

    [Fact]
    public async Task Template_updates_are_audited_and_invalid_events_fail_without_notifications()
    {
        await using var harness = await NotificationHarness.CreateAsync();
        await harness.AddTemplatesAsync(
            NotificationEventTypes.OrderCreated,
            "Old title",
            "Old body",
            NotificationChannel.Email);
        var template = await harness.Db.NotificationTemplates.AsNoTracking().SingleAsync();

        var updated = await harness.TemplateService.UpdateAsync(
            harness.OtherCustomer.Id,
            template.PublicId,
            new UpdateNotificationTemplateRequest(
                "New {{orderNumber}} title",
                "New body",
                true,
                "Clarify customer wording"),
            default);
        Assert.Equal("New {{orderNumber}} title", updated.TitleTemplate);
        var audit = await harness.Db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal("NOTIFICATION_TEMPLATE_UPDATED", audit.Action);
        Assert.Equal(harness.OtherCustomer.Id, audit.UserId);
        Assert.Equal("Clarify customer wording", audit.Reason);
        Assert.Contains("Old title", audit.OldValueJson);
        Assert.Contains("New {{orderNumber}} title", audit.NewValueJson);

        harness.Db.NotificationEvents.Add(new NotificationEvent(
            harness.Customer.Id,
            NotificationEventTypes.OrderCreated,
            "event:invalid-json",
            "{",
            false,
            harness.Clock.UtcNow));
        harness.Db.NotificationEvents.Add(new NotificationEvent(
            harness.Customer.Id,
            NotificationEventTypes.SubscriptionCreated,
            "event:missing-template",
            "{\"Variables\":{},\"DeepLink\":null}",
            false,
            harness.Clock.UtcNow));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(2, await harness.Processor.ProcessPendingEventsAsync(default));
        var events = await harness.Db.NotificationEvents.AsNoTracking().OrderBy(x => x.EventKey).ToListAsync();
        Assert.Equal("INVALID_PAYLOAD", events.Single(x => x.EventKey == "event:invalid-json").FailureCode);
        Assert.Equal("TEMPLATE_NOT_FOUND", events.Single(x => x.EventKey == "event:missing-template").FailureCode);
        Assert.Empty(await harness.Db.Notifications.AsNoTracking().ToListAsync());
    }

    private static NotificationProviderResult Retryable(string code) => new(
        NotificationAttemptOutcome.RetryableFailure,
        FailureCode: code,
        FailureMessage: "Retry later.");

    private sealed class NotificationHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IReadOnlyDictionary<NotificationChannel, ScriptedNotificationGateway> _gateways;
        private int _eventSequence;

        private NotificationHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            User customer,
            User otherCustomer,
            SecureTokenGenerator tokenGenerator,
            NotificationTokenProtector tokenProtector,
            IReadOnlyDictionary<NotificationChannel, ScriptedNotificationGateway> gateways,
            NotificationProcessor processor,
            NotificationService service,
            NotificationTemplateService templateService)
        {
            _connection = connection;
            Db = db;
            Clock = clock;
            Customer = customer;
            OtherCustomer = otherCustomer;
            TokenGenerator = tokenGenerator;
            TokenProtector = tokenProtector;
            _gateways = gateways;
            Processor = processor;
            Service = service;
            TemplateService = templateService;
        }

        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public SecureTokenGenerator TokenGenerator { get; }
        public NotificationTokenProtector TokenProtector { get; }
        public NotificationProcessor Processor { get; }
        public NotificationService Service { get; }
        public NotificationTemplateService TemplateService { get; }

        public ScriptedNotificationGateway Gateway(NotificationChannel channel) => _gateways[channel];

        public async Task<UserDeviceResult> RegisterDeviceAsync(User user, string identifier, string token) =>
            await Service.RegisterDeviceAsync(
                new NotificationActor(user.Id),
                new RegisterDeviceRequest(identifier, token, "android", $"Device {identifier}"),
                default);

        public async Task AddTemplatesAsync(
            string eventType,
            string title,
            string body,
            params NotificationChannel[] channels)
        {
            var selectedChannels = channels.Length == 0 ? Enum.GetValues<NotificationChannel>() : channels;
            Db.NotificationTemplates.AddRange(selectedChannels.Select(channel =>
                new NotificationTemplate(eventType, channel, "en", title, body)));
            await Db.SaveChangesAsync();
        }

        public async Task AddEventAsync(
            long userId,
            string eventType,
            string? eventKey = null,
            IReadOnlyDictionary<string, string>? variables = null,
            string? deepLink = null)
        {
            var writer = new NotificationEventWriter(Db, Clock);
            writer.Add(new NotificationEventRequest(
                userId,
                eventType,
                eventKey ?? $"notification-test:{Interlocked.Increment(ref _eventSequence)}",
                variables ?? new Dictionary<string, string>(),
                deepLink,
                Clock.UtcNow));
            await Db.SaveChangesAsync();
        }

        public async Task<NotificationDelivery> ReloadDeliveryAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.NotificationDeliveries.AsNoTracking().SingleAsync();
        }

        public static async Task<NotificationHarness> CreateAsync(
            int maxAttempts = 4,
            int retryDelayMinutes = 2)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("Customer", "9999999999", "customer@example.com");
            var otherCustomer = CreateCustomer("Other Customer", "8888888888", "other@example.com");
            db.Users.AddRange(customer, otherCustomer);
            await db.SaveChangesAsync();

            var clock = new TestClock(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc));
            var tokenGenerator = new SecureTokenGenerator();
            var tokenProtector = new NotificationTokenProtector(new EphemeralDataProtectionProvider());
            var gateways = Enum.GetValues<NotificationChannel>()
                .ToDictionary(channel => channel, channel => new ScriptedNotificationGateway(channel));
            var notificationOptions = Options.Create(new NotificationOptions
            {
                BatchSize = 25,
                MaxAttempts = maxAttempts,
                InitialRetryDelayMinutes = retryDelayMinutes,
                PollIntervalSeconds = 10
            });
            var processor = new NotificationProcessor(
                db,
                gateways.Values,
                tokenProtector,
                notificationOptions,
                clock);
            var service = new NotificationService(db, tokenGenerator, tokenProtector, clock);
            var templateService = new NotificationTemplateService(db, clock);

            return new NotificationHarness(
                connection,
                db,
                clock,
                customer,
                otherCustomer,
                tokenGenerator,
                tokenProtector,
                gateways,
                processor,
                service,
                templateService);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static User CreateCustomer(string name, string mobile, string email)
        {
            var user = new User(UserType.Customer);
            user.SetProfile(name);
            user.SetContact(mobile, email);
            return user;
        }
    }

    private sealed class ScriptedNotificationGateway(NotificationChannel channel) : INotificationChannelGateway
    {
        private readonly Queue<NotificationProviderResult> _results = new();

        public NotificationChannel Channel { get; } = channel;
        public string ProviderCode => $"TEST_{Channel.ToString().ToUpperInvariant()}";
        public List<NotificationProviderMessage> Messages { get; } = [];

        public void Enqueue(NotificationProviderResult result) => _results.Enqueue(result);

        public Task<NotificationProviderResult> SendAsync(
            NotificationProviderMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(message);
            return Task.FromResult(_results.Count == 0
                ? new NotificationProviderResult(
                    NotificationAttemptOutcome.Delivered,
                    $"test-{message.DeliveryId:N}")
                : _results.Dequeue());
        }
    }
}
