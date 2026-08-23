using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Subscriptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class SubscriptionServiceTests
{
    [Fact]
    public async Task Create_GeneratesFiniteOccurrencesFromWeekdaysAndUsesAuthoritativePrice()
    {
        await using var harness = await SubscriptionHarness.CreateAsync();
        var request = harness.Request(
            quantity: 1.5m,
            startDate: new DateOnly(2026, 8, 17),
            days: [DayOfWeek.Monday, DayOfWeek.Wednesday],
            entitlement: 5);

        var result = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "subscription-1", CancellationToken.None);

        Assert.Equal(600m, result.Subscription.PayableAmount);
        Assert.Equal(80m, result.Subscription.UnitPrice);
        Assert.Equal(5, result.Subscription.TotalEntitlement);
        Assert.Equal(5, result.Subscription.RemainingEntitlement);
        Assert.Equal(new DateOnly(2026, 8, 31), result.Subscription.EndDate);
        Assert.Equal(PaymentMethod.Razorpay, result.Payment.Method);
        Assert.Equal(result.Subscription.PublicId, result.Payment.SubscriptionId);

        var deliveries = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .ToListAsync();
        Assert.Equal(
            [
                new DateOnly(2026, 8, 17),
                new DateOnly(2026, 8, 19),
                new DateOnly(2026, 8, 24),
                new DateOnly(2026, 8, 26),
                new DateOnly(2026, 8, 31)
            ],
            deliveries.Select(x => x.ScheduledDate));
        Assert.All(deliveries, x => Assert.Equal(SubscriptionDeliveryStatus.Scheduled, x.Status));
        Assert.Single(harness.PaymentService.Calls);
        Assert.Equal("subscription-1", harness.PaymentService.Calls[0].IdempotencyKey);
    }

    [Fact]
    public async Task Create_ReplayReturnsSameSubscriptionAndRejectsChangedRequest()
    {
        await using var harness = await SubscriptionHarness.CreateAsync();
        var request = harness.Request();

        var first = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "subscription-1", CancellationToken.None);
        var replay = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "subscription-1", CancellationToken.None);

        Assert.Equal(first.Subscription.PublicId, replay.Subscription.PublicId);
        Assert.Single(await harness.Db.Subscriptions.AsNoTracking().ToListAsync());
        Assert.Equal(2, harness.PaymentService.Calls.Count);

        var events = await harness.Db.NotificationEvents
            .AsNoTracking()
            .OrderBy(x => x.EventType)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, notificationEvent =>
        {
            Assert.Equal(harness.Customer.Id, notificationEvent.UserId);
            Assert.Equal(harness.TimeProvider.Now, notificationEvent.OccurredAt);
            Assert.Equal(
                $"/subscriptions/{first.Subscription.PublicId}",
                Payload(notificationEvent).GetProperty("DeepLink").GetString());
            Assert.Equal(
                first.Subscription.PublicId.ToString(),
                Variables(notificationEvent).GetProperty("subscriptionId").GetString());
        });
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.SubscriptionCreated &&
            notificationEvent.EventKey ==
                $"subscription:{first.Subscription.PublicId:N}:created" &&
            !notificationEvent.IsCritical);
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.SubscriptionPaymentPending &&
            notificationEvent.EventKey ==
                $"subscription:{first.Subscription.PublicId:N}:payment-pending" &&
            notificationEvent.IsCritical &&
            Variables(notificationEvent).GetProperty("amount").GetString() == "320.00" &&
            Variables(notificationEvent).GetProperty("currency").GetString() == "INR");

        await Assert.ThrowsAsync<ConflictException>(() => harness.Service.CreateAsync(
            harness.Customer.Id,
            request with { TotalEntitlement = request.TotalEntitlement + 1 },
            "subscription-1",
            CancellationToken.None));
        Assert.Equal(2, harness.PaymentService.Calls.Count);
    }

    [Fact]
    public async Task ReadsAndActions_AreCustomerScoped()
    {
        await using var harness = await SubscriptionHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-1", CancellationToken.None);

        Assert.Empty(await harness.Service.GetForCustomerAsync(
            harness.OtherCustomer.Id, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.GetAsync(
            harness.OtherCustomer.Id, created.Subscription.PublicId, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.CancelAsync(
            harness.OtherCustomer.Id, created.Subscription.PublicId, CancellationToken.None));
    }

    [Fact]
    public async Task PauseAndSkip_EnforceConfiguredCutoff()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 8, 16, 13, 0, 0, DateTimeKind.Utc),
            cutoffHours: 12);
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(startDate: new DateOnly(2026, 8, 17), days: [DayOfWeek.Monday]),
            "subscription-1",
            CancellationToken.None);
        await harness.ActivateAsync();
        var delivery = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .FirstAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.PauseAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.SkipAsync(
            harness.Customer.Id,
            created.Subscription.PublicId,
            new SkipSubscriptionDeliveryRequest(delivery.PublicId),
            CancellationToken.None));

        Assert.Equal(
            SubscriptionDeliveryStatus.Scheduled,
            (await harness.Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.PublicId == delivery.PublicId)).Status);
    }

    [Fact]
    public async Task PauseResumeSkipAndCancel_PersistExpectedCustomerState()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc),
            cutoffHours: 12);
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(
                startDate: new DateOnly(2026, 8, 17),
                days: [DayOfWeek.Monday, DayOfWeek.Wednesday],
                entitlement: 2),
            "subscription-1",
            CancellationToken.None);
        await harness.ActivateAsync();
        var firstDelivery = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .FirstAsync();

        var paused = await harness.Service.PauseAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);
        var resumed = await harness.Service.ResumeAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);
        var skipped = await harness.Service.SkipAsync(
            harness.Customer.Id,
            created.Subscription.PublicId,
            new SkipSubscriptionDeliveryRequest(firstDelivery.PublicId),
            CancellationToken.None);
        var cancelled = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);
        var replay = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Paused, paused.Status);
        Assert.Equal(SubscriptionStatus.Active, resumed.Status);
        Assert.Equal(SubscriptionDeliveryStatus.Skipped, skipped.Status);
        Assert.Equal(SubscriptionStatus.Cancelled, cancelled.Status);
        Assert.Equal(SubscriptionStatus.Cancelled, replay.Status);
        var statuses = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .Select(x => x.Status)
            .ToListAsync();
        Assert.Equal([SubscriptionDeliveryStatus.Skipped, SubscriptionDeliveryStatus.Cancelled], statuses);

        var events = await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x =>
                x.EventType == NotificationEventTypes.SubscriptionPaused ||
                x.EventType == NotificationEventTypes.SubscriptionResumed ||
                x.EventType == NotificationEventTypes.SubscriptionSkipped)
            .OrderBy(x => x.EventType)
            .ToListAsync();
        Assert.Equal(3, events.Count);
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.SubscriptionPaused &&
            notificationEvent.EventKey ==
                $"subscription:{created.Subscription.PublicId:N}:paused:{harness.TimeProvider.Now.Ticks}" &&
            notificationEvent.OccurredAt == harness.TimeProvider.Now);
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.SubscriptionResumed &&
            notificationEvent.EventKey ==
                $"subscription:{created.Subscription.PublicId:N}:resumed:{harness.TimeProvider.Now.Ticks}" &&
            notificationEvent.OccurredAt == harness.TimeProvider.Now);
        var skippedEvent = Assert.Single(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.SubscriptionSkipped);
        Assert.Equal(
            $"subscription-delivery:{firstDelivery.PublicId:N}:skipped",
            skippedEvent.EventKey);
        Assert.Equal(harness.TimeProvider.Now, skippedEvent.OccurredAt);
        Assert.Equal(
            "2026-08-17",
            Variables(skippedEvent).GetProperty("date").GetString());
        Assert.Equal(
            $"/subscriptions/{created.Subscription.PublicId}",
            Payload(skippedEvent).GetProperty("DeepLink").GetString());
    }

    private static JsonElement Payload(DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        JsonSerializer.Deserialize<JsonElement>(notificationEvent.PayloadJson);

    private static JsonElement Variables(DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        Payload(notificationEvent).GetProperty("Variables");

    [Fact]
    public async Task Update_RejectsChangesToGeneratedCommercialAndScheduleFields()
    {
        await using var harness = await SubscriptionHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-1", CancellationToken.None);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.UpdateAsync(
            harness.Customer.Id,
            created.Subscription.PublicId,
            new UpdateSubscriptionRequest(2m, null, null),
            CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.UpdateAsync(
            harness.Customer.Id,
            created.Subscription.PublicId,
            new UpdateSubscriptionRequest(null, null, [DayOfWeek.Friday]),
            CancellationToken.None));
    }

    private sealed class SubscriptionHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private SubscriptionHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            User customer,
            User otherCustomer,
            Product product,
            CustomerAddress address,
            TestClock clock,
            TestIndiaTimeProvider timeProvider,
            CapturingSubscriptionPaymentService paymentService,
            SubscriptionService service)
        {
            this.connection = connection;
            Db = db;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Product = product;
            Address = address;
            Clock = clock;
            TimeProvider = timeProvider;
            PaymentService = paymentService;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public Product Product { get; }
        public CustomerAddress Address { get; }
        public TestClock Clock { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public CapturingSubscriptionPaymentService PaymentService { get; }
        public SubscriptionService Service { get; }

        public CreateSubscriptionRequest Request(
            decimal quantity = 1m,
            DateOnly? startDate = null,
            IReadOnlyCollection<DayOfWeek>? days = null,
            int entitlement = 4) =>
            new(
                Product.PublicId,
                Address.PublicId,
                quantity,
                startDate ?? new DateOnly(2026, 8, 17),
                days ?? [DayOfWeek.Monday, DayOfWeek.Wednesday],
                entitlement,
                PaymentMethod.Razorpay);

        public async Task ActivateAsync()
        {
            var subscription = await Db.Subscriptions.SingleAsync();
            subscription.Activate(TimeProvider.Now);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public static async Task<SubscriptionHarness> CreateAsync(
            DateTime? utcNow = null,
            int? cutoffHours = null)
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
            var otherCustomer = new User(UserType.Customer);
            otherCustomer.SetProfile("Other Customer");
            db.Users.AddRange(customer, otherCustomer);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var address = new CustomerAddress(
                customer.Id,
                "Home",
                "1 Main Road",
                "Central",
                "Bengaluru",
                "Karnataka",
                "560001",
                "Customer",
                "9999999999",
                12.9716m,
                77.5946m);
            db.AddRange(product, branch, address);
            await db.SaveChangesAsync();
            db.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, true, 100m));
            if (cutoffHours.HasValue)
            {
                db.SystemConfigurations.Add(new SystemConfiguration(
                    "Subscription.SkipPauseCutoffHours",
                    cutoffHours.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "integer"));
            }
            await db.SaveChangesAsync();

            var clock = new TestClock(utcNow ?? new DateTime(2026, 8, 16, 2, 0, 0, DateTimeKind.Utc));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var paymentService = new CapturingSubscriptionPaymentService(db, clock);
            var allocation = new FixedBranchAllocationService(branch);
            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var service = new SubscriptionService(
                db,
                allocation,
                paymentService,
                timeProvider,
                notificationEventWriter);
            return new SubscriptionHarness(
                connection, db, customer, otherCustomer, product, address, clock, timeProvider, paymentService, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedBranchAllocationService(Branch branch) : IBranchAllocationService
    {
        public Task<BranchAllocationResult> AllocateAsync(
            decimal latitude,
            decimal longitude,
            IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BranchAllocationResult(
                branch.Id, branch.PublicId, branch.Code, branch.Name, 0m));
    }

    private sealed class CapturingSubscriptionPaymentService(
        DoodhDirectDbContext db,
        TestClock clock) : IPaymentService
    {
        public List<(long CustomerId, long SubscriptionId, PaymentMethod Method, string IdempotencyKey)> Calls { get; } = [];

        public async Task<PaymentResult> CreateForSubscriptionAsync(
            long customerId,
            long subscriptionId,
            PaymentMethod method,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            Calls.Add((customerId, subscriptionId, method, idempotencyKey));
            var subscription = await db.Subscriptions
                .AsNoTracking()
                .SingleAsync(x => x.Id == subscriptionId, cancellationToken);
            return new PaymentResult(
                Guid.NewGuid(),
                null,
                null,
                method,
                method switch
                {
                    PaymentMethod.Wallet => "Wallet",
                    PaymentMethod.Development => "Mock",
                    _ => "Razorpay"
                },
                PaymentStatus.Pending,
                subscription.PayableAmount,
                0m,
                "INR",
                $"order_{subscription.PublicId:N}",
                null,
                "rzp_test",
                null,
                null,
                clock.UtcNow.AddMinutes(15),
                null,
                clock.UtcNow,
                subscription.PublicId);
        }
        public Task<PaymentResult> RetrySubscriptionAsync(
            long customerId,
            Guid subscriptionId,
            PaymentMethod method,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CompleteDevelopmentAsync(
            long customerId,
            Guid paymentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CancelAsync(
            long customerId,
            Guid paymentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CreateAsync(
            long customerId,
            CreatePaymentRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> VerifyAsync(
            long customerId,
            VerifyPaymentRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> GetAsync(
            long userId,
            Guid paymentId,
            bool bypassOwnership,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentReconciliationResult> ReconcileAsync(
            long requestedByUserId,
            Guid paymentId,
            bool bypassOwnership,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RefundResult> RefundAsync(
            long requestedByUserId,
            Guid paymentId,
            RefundPaymentRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ProcessWebhookAsync(
            byte[] payload,
            string signature,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
