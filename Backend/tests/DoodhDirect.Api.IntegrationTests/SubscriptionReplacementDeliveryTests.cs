using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 1: replacement-occurrence persistence. Proves the replacement row
/// round-trips with its linkage, the failed source stays Failed, entitlement
/// and financials are unchanged by creation, ordinary rows keep null linkage,
/// and the EF model preserves uniqueness while adding the required FKs/index.
/// </summary>
public sealed class SubscriptionReplacementDeliveryTests
{
    private static readonly DateTime IndiaNow =
        new(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified);

    [Fact]
    public async Task Replacement_PersistsWithLinkage_OriginalStaysFailed()
    {
        await using var harness = await ReplacementHarness.CreateAsync();
        var db = harness.Db;

        var replacement = harness.Subscription.AddReplacementDelivery(
            harness.FailedDelivery,
            new DateOnly(2026, 9, 5),
            harness.Request.Id,
            "Approved re-delivery");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var rows = await db.SubscriptionDeliveries
            .Where(x => x.SubscriptionId == harness.Subscription.Id)
            .OrderBy(x => x.ScheduledDate)
            .ToListAsync();
        Assert.Equal(2, rows.Count);

        var original = rows.Single(x => !x.IsReplacement);
        Assert.Equal(SubscriptionDeliveryStatus.Failed, original.Status);
        Assert.Null(original.ReplacesSubscriptionDeliveryId);
        Assert.Null(original.RefundReplacementRequestId);
        Assert.Null(original.ReplacementReason);

        var persisted = rows.Single(x => x.IsReplacement);
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, persisted.Status);
        Assert.Equal(harness.FailedDelivery.Id, persisted.ReplacesSubscriptionDeliveryId);
        Assert.Equal(harness.Request.Id, persisted.RefundReplacementRequestId);
        Assert.Equal("Approved re-delivery", persisted.ReplacementReason);
        Assert.Equal(new DateOnly(2026, 9, 5), persisted.ScheduledDate);

        // Frozen copy, not live reads.
        Assert.Equal(original.Slot, persisted.Slot);
        Assert.Equal(original.BranchId, persisted.BranchId);
        Assert.Equal(original.Quantity, persisted.Quantity);
        Assert.Equal(original.BranchCodeSnapshot, persisted.BranchCodeSnapshot);
        Assert.Equal(original.BranchNameSnapshot, persisted.BranchNameSnapshot);
        Assert.Equal(original.AddressSnapshot, persisted.AddressSnapshot);

        // Term, entitlement, and financials untouched by creation.
        var subscription = await db.Subscriptions.SingleAsync();
        Assert.Equal(new DateOnly(2026, 8, 31), subscription.EndDate);
        Assert.Equal(0, subscription.UsedEntitlement);
        Assert.Equal(320m, subscription.PayableAmount);
        Assert.Equal(0m, subscription.ChargesTotal);
        Assert.Empty(subscription.Charges);

        // Replacement may sit beyond EndDate.
        Assert.True(persisted.ScheduledDate > subscription.EndDate);

        // Navigation fix-up resolves both links.
        var withLinks = await db.SubscriptionDeliveries
            .Include(x => x.ReplacesSubscriptionDelivery)
            .Include(x => x.RefundReplacementRequest)
            .SingleAsync(x => x.Id == persisted.Id);
        Assert.Equal(original.Id, withLinks.ReplacesSubscriptionDelivery!.Id);
        Assert.Equal(harness.Request.Id, withLinks.RefundReplacementRequest!.Id);

        var requestLinks = await db.RefundReplacementRequests
            .Include(x => x.ReplacementDeliveries)
            .SingleAsync(x => x.Id == harness.Request.Id);
        Assert.Equal(persisted.Id, Assert.Single(requestLinks.ReplacementDeliveries).Id);
    }

    [Fact]
    public async Task Replacement_DeliveringReplacement_ConsumesOutstandingEntitlement()
    {
        await using var harness = await ReplacementHarness.CreateAsync();
        var db = harness.Db;

        var replacement = harness.Subscription.AddReplacementDelivery(
            harness.FailedDelivery,
            new DateOnly(2026, 9, 5),
            harness.Request.Id,
            null);
        harness.Subscription.MarkDelivered(replacement, IndiaNow.AddDays(1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var subscription = await db.Subscriptions.SingleAsync();
        Assert.Equal(1, subscription.UsedEntitlement);
        var statuses = await db.SubscriptionDeliveries
            .Where(x => x.SubscriptionId == subscription.Id)
            .OrderBy(x => x.ScheduledDate)
            .Select(x => x.Status)
            .ToListAsync();
        Assert.Equal(
            [SubscriptionDeliveryStatus.Failed, SubscriptionDeliveryStatus.Delivered],
            statuses);
    }

    [Fact]
    public async Task Model_PreservesUniqueness_AddsRequiredKeys()
    {
        await using var harness = await ReplacementHarness.CreateAsync();
        var entityType = harness.Db.Model.FindEntityType(typeof(SubscriptionDelivery));
        Assert.NotNull(entityType);

        var dateIndex = entityType.GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name)
                .SequenceEqual(["SubscriptionId", "ScheduledDate"]));
        Assert.True(dateIndex.IsUnique);

        var requestIndex = entityType.GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name)
                .SequenceEqual(["RefundReplacementRequestId"]));
        Assert.False(requestIndex.IsUnique);

        var replacesFk = entityType.GetForeignKeys()
            .Single(x => x.Properties.Select(p => p.Name)
                .SequenceEqual(["ReplacesSubscriptionDeliveryId"]));
        Assert.Equal(DeleteBehavior.Restrict, replacesFk.DeleteBehavior);
        Assert.True(replacesFk.Properties.Single().IsNullable);

        var requestFk = entityType.GetForeignKeys()
            .Single(x => x.Properties.Select(p => p.Name)
                .SequenceEqual(["RefundReplacementRequestId"]));
        Assert.Equal(DeleteBehavior.Restrict, requestFk.DeleteBehavior);
        Assert.True(requestFk.Properties.Single().IsNullable);

        Assert.Equal(200, entityType.FindProperty(nameof(SubscriptionDelivery.ReplacementReason))!
            .GetMaxLength());
    }

    private sealed class ReplacementHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ReplacementHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            Subscription subscription,
            SubscriptionDelivery failedDelivery,
            RefundReplacementRequest request)
        {
            this.connection = connection;
            Db = db;
            Subscription = subscription;
            FailedDelivery = failedDelivery;
            Request = request;
        }

        public DoodhDirectDbContext Db { get; }
        public Subscription Subscription { get; }
        public SubscriptionDelivery FailedDelivery { get; }
        public RefundReplacementRequest Request { get; }

        public static async Task<ReplacementHarness> CreateAsync()
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
            db.Users.Add(customer);

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
                "subscription-001",
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
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();

            var failed = await db.SubscriptionDeliveries.SingleAsync();
            subscription.MarkFailed(failed, IndiaNow);
            await db.SaveChangesAsync();

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
            var request = new RefundReplacementRequest(
                null,
                delivery.Id,
                customer.Id,
                branch.Id,
                null,
                RefundReplacementType.Replacement,
                RefundReplacementSource.DeliveryFailed,
                "Milk was not delivered",
                null,
                submittedAt,
                submittedAt.AddHours(48),
                failed.Id,
                submittedAt,
                48,
                10m,
                "Customer not available",
                RefundReplacementStatus.AwaitingCustomerChoice);
            request.AssignRequestNumber("RR-D1D-000001");
            db.RefundReplacementRequests.Add(request);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var trackedSubscription = await db.Subscriptions
                .Include(x => x.Deliveries)
                .SingleAsync();
            var trackedFailed = trackedSubscription.Deliveries.Single();
            var trackedRequest = await db.RefundReplacementRequests.SingleAsync();
            return new ReplacementHarness(connection, db, trackedSubscription, trackedFailed, trackedRequest);
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            await Db.DisposeAsync();
        }
    }
}
