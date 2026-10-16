using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Subscriptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 7.2 — calendar replacement metadata. Proves the read-model
/// calendar flags replacement occurrences directly from the persisted
/// <c>SubscriptionDelivery.IsReplacement</c> (never inferred), ordinary rows
/// stay unflagged, date ordering and ownership authorization are preserved,
/// and the failed original remains visible alongside the replacement on its
/// own scheduled date. Read contract only: no entitlement, term, financial,
/// or materialization changes.
/// </summary>
public sealed class SubscriptionCalendarReplacementMetadataTests
{
    private static readonly DateTime IndiaNow =
        new(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified);

    [Fact]
    public async Task Calendar_IdentifiesReplacement_OrdinaryRowsUnchanged()
    {
        await using var harness = await CalendarHarness.CreateAsync(withReplacement: true);

        var calendar = await harness.Service.GetCalendarAsync(
            harness.Customer.Id,
            harness.Subscription.PublicId,
            CancellationToken.None);

        Assert.Equal(3, calendar.Count);
        // Date ordering preserved: failed original, ordinary upcoming, then
        // the replacement on its own (post-term) date.
        Assert.Equal(
            [new DateOnly(2026, 8, 18), new DateOnly(2026, 8, 19), new DateOnly(2026, 9, 5)],
            calendar.Select(x => x.ScheduledDate).ToArray());

        var original = calendar[0];
        Assert.Equal(SubscriptionDeliveryStatus.Failed, original.Status);
        Assert.False(original.IsReplacement);

        var ordinary = calendar[1];
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, ordinary.Status);
        Assert.False(ordinary.IsReplacement);

        var replacement = calendar[2];
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
        Assert.True(replacement.IsReplacement);

        // Frozen copy carried through the read model, not live reads.
        Assert.Equal(original.Quantity, replacement.Quantity);
        Assert.Equal(original.BranchId, replacement.BranchId);
        Assert.Equal(original.BranchCode, replacement.BranchCode);
        Assert.Equal(original.BranchName, replacement.BranchName);
        Assert.Equal(original.Address, replacement.Address);
        Assert.Equal(original.Slot, replacement.Slot);
    }

    [Fact]
    public async Task Calendar_PreservesFailedOriginalAlongsideReplacement()
    {
        await using var harness = await CalendarHarness.CreateAsync(withReplacement: true);

        var calendar = await harness.Service.GetCalendarAsync(
            harness.Customer.Id,
            harness.Subscription.PublicId,
            CancellationToken.None);

        // The original failure keeps its own date and Failed status even
        // after the replacement is approved; the replacement appears as an
        // additional row on its own scheduled date.
        var failed = Assert.Single(calendar, x =>
            x is { Status: SubscriptionDeliveryStatus.Failed, IsReplacement: false });
        Assert.Equal(new DateOnly(2026, 8, 18), failed.ScheduledDate);

        var replacement = Assert.Single(calendar, x => x.IsReplacement);
        Assert.Equal(new DateOnly(2026, 9, 5), replacement.ScheduledDate);
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
    }

    [Fact]
    public async Task Calendar_WithoutReplacement_AllRowsOrdinary()
    {
        await using var harness = await CalendarHarness.CreateAsync(withReplacement: false);

        var calendar = await harness.Service.GetCalendarAsync(
            harness.Customer.Id,
            harness.Subscription.PublicId,
            CancellationToken.None);

        Assert.Equal(2, calendar.Count);
        Assert.All(calendar, x => Assert.False(x.IsReplacement));
        Assert.Equal(
            [new DateOnly(2026, 8, 18), new DateOnly(2026, 8, 19)],
            calendar.Select(x => x.ScheduledDate).ToArray());
    }

    [Fact]
    public async Task Calendar_OtherCustomerCannotRead()
    {
        await using var harness = await CalendarHarness.CreateAsync(withReplacement: true);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.GetCalendarAsync(
                harness.OtherCustomer.Id,
                harness.Subscription.PublicId,
                CancellationToken.None));
    }

    private sealed class CalendarHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private CalendarHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            User customer,
            User otherCustomer,
            Subscription subscription,
            SubscriptionService service)
        {
            this.connection = connection;
            Db = db;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Subscription = subscription;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public Subscription Subscription { get; }
        public SubscriptionService Service { get; }

        public static async Task<CalendarHarness> CreateAsync(bool withReplacement)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            customer.SetContact("9999999999", null);
            var otherCustomer = new User(UserType.Customer);
            otherCustomer.SetProfile("Other Customer");
            otherCustomer.SetContact("9999999998", null);
            db.Users.AddRange(customer, otherCustomer);

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

            var subscription = new Subscription(
                customer.Id,
                product.Id,
                address.Id,
                branch.Id,
                "subscription-calendar-001",
                new DateOnly(2026, 8, 17),
                new DateOnly(2026, 8, 31),
                1m,
                80m,
                4,
                "MILK-001",
                "Fresh Milk",
                "LITRE",
                "MAIN",
                "Main Branch",
                "Home, 1 Main Road, Central, Bengaluru");
            subscription.Activate(IndiaNow);
            subscription.AddDelivery(new DateOnly(2026, 8, 18));
            subscription.AddDelivery(new DateOnly(2026, 8, 19));
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();

            var failed = await db.SubscriptionDeliveries
                .SingleAsync(x => x.ScheduledDate == new DateOnly(2026, 8, 18));
            subscription.MarkFailed(failed, IndiaNow);
            await db.SaveChangesAsync();

            if (withReplacement)
            {
                // The replacement needs its originating request link; the
                // calendar itself only surfaces the persisted flag.
                var delivery = Delivery.ForSubscriptionOccurrence(
                    failed.Id,
                    customer.Id,
                    branch.Id,
                    failed.ScheduledDate,
                    "REF-D1D-000001",
                    "Customer",
                    "9999999999",
                    "Home, 1 Main Road, Central, Bengaluru",
                    null,
                    12.9716m,
                    77.5946m);
                delivery.AssignDeliveryNumber("DLV-D1D-000001");
                db.Deliveries.Add(delivery);
                await db.SaveChangesAsync();

                var submittedAt = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Unspecified);
                var request = new DoodhDirect.Domain.RefundReplacement.RefundReplacementRequest(
                    null,
                    delivery.Id,
                    customer.Id,
                    branch.Id,
                    null,
                    DoodhDirect.Domain.RefundReplacement.RefundReplacementType.Replacement,
                    DoodhDirect.Domain.RefundReplacement.RefundReplacementSource.DeliveryFailed,
                    "Milk was not delivered",
                    null,
                    submittedAt,
                    submittedAt.AddHours(48),
                    failed.Id,
                    submittedAt,
                    48,
                    10m,
                    "Customer not available",
                    DoodhDirect.Domain.RefundReplacement.RefundReplacementStatus.AwaitingCustomerChoice);
                request.AssignRequestNumber("RR-D1D-000001");
                db.RefundReplacementRequests.Add(request);
                await db.SaveChangesAsync();

                subscription.AddReplacementDelivery(
                    failed,
                    new DateOnly(2026, 9, 5),
                    request.Id,
                    "Approved re-delivery");
                await db.SaveChangesAsync();
            }

            db.ChangeTracker.Clear();
            var trackedSubscription = await db.Subscriptions
                .Include(x => x.Deliveries)
                .SingleAsync(x => x.CustomerId == customer.Id);
            var trackedCustomer = await db.Users.SingleAsync(x => x.Id == customer.Id);
            var trackedOther = await db.Users.SingleAsync(x => x.Id == otherCustomer.Id);

            var timeProvider = new FixedIndiaTimeProvider(IndiaNow);
            var service = new SubscriptionService(
                db,
                new FixedBranchAllocationService(branch),
                new UnusedPaymentService(),
                timeProvider,
                new TestNotificationEventWriter(db, timeProvider));
            return new CalendarHarness(connection, db, trackedCustomer, trackedOther, trackedSubscription, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedIndiaTimeProvider(DateTime now) : IIndiaTimeProvider
    {
        public DateTime Now => now;
        public DateTime ToUtc(DateTime indiaLocal) => DateTime.SpecifyKind(indiaLocal, DateTimeKind.Unspecified);
        public DateOnly Today => DateOnly.FromDateTime(now);
        public DateOnly CurrentDate => Today;
        public DateTime CurrentDateTime => now;
        public string FormatDateTime(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss");
        public string FormatDate(DateOnly value) => value.ToString("yyyy-MM-dd");
        public DateTime ParseApplicationDateTime(string value) =>
            DateTime.SpecifyKind(DateTime.Parse(value), DateTimeKind.Unspecified);
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

    /// <summary>
    /// The calendar read path never touches payments; every member throws.
    /// </summary>
    private sealed class UnusedPaymentService : IPaymentService
    {
        public Task<PaymentResult> CreateAsync(long customerId, CreatePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PaymentResult> CreateForSubscriptionAsync(long customerId, long subscriptionId, PaymentMethod method, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PaymentResult> RetrySubscriptionAsync(long customerId, Guid subscriptionId, PaymentMethod method, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PaymentResult> CreateWalletTopUpAsync(long customerId, decimal amount, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PaymentResult> VerifyAsync(long customerId, VerifyPaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PaymentResult> CompleteDevelopmentAsync(long customerId, Guid paymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PaymentResult> CancelAsync(long customerId, Guid paymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PaymentResult> GetAsync(long userId, Guid paymentId, bool bypassOwnership, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<RefundResult> RefundAsync(long requestedByUserId, Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PaymentReconciliationResult> ReconcileAsync(long requestedByUserId, Guid paymentId, bool bypassOwnership, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task ProcessWebhookAsync(byte[] payload, string signature, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
