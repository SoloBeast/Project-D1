using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class NotificationDuplicationRegressionTests
{
    [Fact]
    public async Task SeededTemplates_DoNotDuplicateTitleInBody_ForOrderCreatedAndPaymentSucceeded()
    {
        await using var harness = await DuplicationHarness.CreateAsync();
        await new NotificationTemplateSeedService(harness.Db).SeedAsync(default);

        foreach (var eventType in new[] { NotificationEventTypes.OrderCreated, NotificationEventTypes.PaymentSucceeded })
        {
            var template = await harness.Db.NotificationTemplates
                .AsNoTracking()
                .SingleAsync(x => x.EventType == eventType && x.Channel == NotificationChannel.Push && x.Language == "en");
            Assert.False(string.IsNullOrWhiteSpace(template.TitleTemplate));
            Assert.Equal("{{message}}", template.BodyTemplate);
        }
    }

    [Fact]
    public async Task Processor_RendersTitleOnceAndBodyOnce_ForTwoEventTypes()
    {
        await using var harness = await DuplicationHarness.CreateAsync();
        await new NotificationTemplateSeedService(harness.Db).SeedAsync(default);

        await harness.AddEventAsync(
            harness.Customer.Id,
            NotificationEventTypes.OrderCreated,
            "order:dup:1",
            new Dictionary<string, string> { ["message"] = "Order ORD/NEAR/000001 has been created." },
            "/orders/11111111-1111-1111-1111-111111111111");
        await harness.AddEventAsync(
            harness.Customer.Id,
            NotificationEventTypes.PaymentSucceeded,
            "payment:dup:1",
            new Dictionary<string, string> { ["message"] = "Payment of 100.00 succeeded." },
            "/wallet");

        Assert.Equal(2, await harness.Processor.ProcessPendingEventsAsync(default));

        var notifications = await harness.Db.Notifications.AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(2, notifications.Count);

        var orderNotification = notifications.Single(x => x.EventType == NotificationEventTypes.OrderCreated);
        Assert.Equal("Order created", orderNotification.Title);
        Assert.Equal("Order ORD/NEAR/000001 has been created.", orderNotification.Body);
        Assert.DoesNotContain(orderNotification.Title, orderNotification.Body, StringComparison.Ordinal);

        var paymentNotification = notifications.Single(x => x.EventType == NotificationEventTypes.PaymentSucceeded);
        Assert.Equal("Payment succeeded", paymentNotification.Title);
        Assert.Equal("Payment of 100.00 succeeded.", paymentNotification.Body);
        Assert.DoesNotContain(paymentNotification.Title, paymentNotification.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seed_MigratesLegacyDuplicatedBodies_WithoutTouchingCustomTemplates()
    {
        await using var harness = await DuplicationHarness.CreateAsync();
        harness.Db.NotificationTemplates.Add(new NotificationTemplate(
            NotificationEventTypes.OrderCreated,
            NotificationChannel.Push,
            "en",
            "Order created",
            "Order created: {{message}}"));
        harness.Db.NotificationTemplates.Add(new NotificationTemplate(
            NotificationEventTypes.PaymentSucceeded,
            NotificationChannel.Push,
            "en",
            "Payment succeeded",
            "Custom body keeps {{message}} with extra context."));
        await harness.Db.SaveChangesAsync();

        await new NotificationTemplateSeedService(harness.Db).SeedAsync(default);

        var migrated = await harness.Db.NotificationTemplates.AsNoTracking()
            .SingleAsync(x => x.EventType == NotificationEventTypes.OrderCreated && x.Channel == NotificationChannel.Push);
        Assert.Equal("{{message}}", migrated.BodyTemplate);
        Assert.Equal("Order created", migrated.TitleTemplate);

        var custom = await harness.Db.NotificationTemplates.AsNoTracking()
            .SingleAsync(x => x.EventType == NotificationEventTypes.PaymentSucceeded && x.Channel == NotificationChannel.Push);
        Assert.Equal("Custom body keeps {{message}} with extra context.", custom.BodyTemplate);
    }

    private sealed class DuplicationHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private DuplicationHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            User customer,
            NotificationProcessor processor)
        {
            _connection = connection;
            Db = db;
            Clock = clock;
            Customer = customer;
            Processor = processor;
        }

        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public User Customer { get; }
        public NotificationProcessor Processor { get; }

        public async Task AddEventAsync(
            long userId,
            string eventType,
            string eventKey,
            IReadOnlyDictionary<string, string> variables,
            string? deepLink)
        {
            var writer = new NotificationEventWriter(Db, new TestIndiaTimeProvider(Clock));
            writer.Add(new NotificationEventRequest(userId, eventType, eventKey, variables, deepLink, Clock.Now));
            await Db.SaveChangesAsync();
        }

        public static async Task<DuplicationHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            customer.SetContact("9999999999", "customer@example.com");
            db.Users.Add(customer);
            await db.SaveChangesAsync();
            var clock = new TestClock(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Unspecified));
            var dataProtectionProvider = new EphemeralDataProtectionProvider();
            var tokenProtector = new NotificationTokenProtector(dataProtectionProvider);
            var otpProtector = new DeliveryOtpHandoffProtector(dataProtectionProvider);
            var gateways = Enum.GetValues<NotificationChannel>()
                .ToDictionary(channel => channel, channel => (INotificationChannelGateway)new DuplicationGateway(channel));
            var notificationOptions = Options.Create(new NotificationOptions
            {
                BatchSize = 25,
                MaxAttempts = 4,
                InitialRetryDelayMinutes = 2,
                PollIntervalSeconds = 10
            });
            var processor = new NotificationProcessor(
                db,
                gateways.Values,
                tokenProtector,
                otpProtector,
                notificationOptions,
                new TestIndiaTimeProvider(clock));
            return new DuplicationHarness(connection, db, clock, customer, processor);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private sealed class DuplicationGateway(NotificationChannel channel) : INotificationChannelGateway
        {
            public NotificationChannel Channel { get; } = channel;
            public string ProviderCode => $"TEST_{Channel.ToString().ToUpperInvariant()}";

            public Task<NotificationProviderResult> SendAsync(
                NotificationProviderMessage message,
                CancellationToken cancellationToken) =>
                Task.FromResult(new NotificationProviderResult(NotificationAttemptOutcome.Delivered, $"test-{message.DeliveryId:N}"));
        }
    }
}
