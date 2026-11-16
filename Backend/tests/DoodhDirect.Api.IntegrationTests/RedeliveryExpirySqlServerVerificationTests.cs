using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.Deliveries;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Focused SQL Server verification for the <see cref="RedeliveryExpiryProcessor"/>
/// candidate query. Executes the SQL Server-specific GUID-to-EventKey
/// expression against an isolated SQL Server test database (never the
/// development database): a noticed request must be excluded before
/// BatchSize is applied, an un-noticed request must remain eligible, and the
/// SQL-rendered key must exactly match the deterministic C# key.
/// Skips gracefully when no local SQL Server is reachable.
/// </summary>
public sealed class RedeliveryExpirySqlServerVerificationTests
{
    private const string SqlServerInstance = @".\SQLEXPRESS";

    [Fact]
    public async Task CandidateQuery_OnSqlServer_ExcludesNoticedRequestBeforeBatchSize()
    {
        if (!await SqlServerReachableAsync())
        {
            throw new InvalidOperationException(
                $"This verification requires the local SQL Server Express instance ('{SqlServerInstance}').");
        }

        var databaseName = $"DoodhDirect_RedeliveryVerify_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = SqlServerInstance,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            MultipleActiveResultSets = true
        };
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlServer(builder.ConnectionString, sql => sql.CommandTimeout(180))
            .Options;
        await using var db = new DoodhDirectDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync();

            var clock = new TestClock(new DateTime(2026, 8, 16, 9, 30, 0, DateTimeKind.Unspecified));
            var time = new TestIndiaTimeProvider(clock);
            var seed = await SeedAsync(db, time, clock);

            // Two expired re-delivery tickets on the real SQL Server flow.
            var firstId = await FailTicketAsync(seed, null);
            await ChooseAsync(seed.RefundService, seed.CustomerId, firstId);
            var secondId = await FailTicketAsync(seed, new DateOnly(2026, 8, 19));
            await ChooseAsync(seed.RefundService, seed.CustomerId, secondId);
            clock.Advance(TimeSpan.FromHours(2));

            // Notice the first ticket through the lazy on-demand path.
            await seed.RefundService.NotifyRedeliveryExpiredAsync(
                new RefundReplacementActor(seed.CustomerId, new HashSet<long>(), false),
                firstId,
                CancellationToken.None);

            // The SQL-rendered key must exactly match the deterministic key.
            var storedKey = await db.NotificationEvents.AsNoTracking()
                .Where(x => x.EventType == NotificationEventTypes.RedeliveryExpired)
                .Select(x => x.EventKey)
                .SingleAsync();
            Assert.Equal($"refund-replacement:{firstId:N}:redelivery-expired", storedKey);

            // With batch size 1 the sweep must skip the noticed ticket and
            // select the remaining one: exclusion happens in SQL before Take.
            var processor = new RedeliveryExpiryProcessor(
                db,
                time,
                seed.RefundService,
                Options.Create(new RedeliveryExpiryOptions { BatchSize = 1 }),
                NullLogger<RedeliveryExpiryProcessor>.Instance);
            Assert.Equal(1, await processor.ProcessExpiredRedeliveriesAsync(CancellationToken.None));

            db.ChangeTracker.Clear();
            Assert.Equal(1, await db.NotificationEvents.AsNoTracking()
                .CountAsync(x => x.EventKey == $"refund-replacement:{firstId:N}:redelivery-expired"));
            Assert.Equal(1, await db.NotificationEvents.AsNoTracking()
                .CountAsync(x => x.EventKey == $"refund-replacement:{secondId:N}:redelivery-expired"));
            Assert.Equal(0, await processor.ProcessExpiredRedeliveriesAsync(CancellationToken.None));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static async Task<bool> SqlServerReachableAsync()
    {
        try
        {
            await using var probe = new SqlConnection(
                $"Server={SqlServerInstance};Integrated Security=true;Connection Timeout=3;TrustServerCertificate=true");
            await probe.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record SeedData(
        DoodhDirectDbContext Db,
        DeliveryService DeliveryService,
        RefundReplacementService RefundService,
        RefundReplacementConfigurationService ConfigurationService,
        long CustomerId,
        long ManagerId,
        long StaffId,
        long BranchId,
        TestClock Clock,
        TestIndiaTimeProvider Time);

    private static async Task<SeedData> SeedAsync(
        DoodhDirectDbContext db, TestIndiaTimeProvider time, TestClock clock)
    {
        db.NumberSeries.Add(new NumberSeries(
            "DELIVERY", "Delivery Number", "DEL/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never));
        db.NumberSeries.Add(new NumberSeries(
            "DELIVERY_MAIN", "Delivery MAIN", "DLV/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));
        db.NumberSeries.Add(new NumberSeries(
            RefundReplacementService.SeriesCode,
            "Refund/replacement request numbers",
            "RR/{NUMBER:000000}",
            1,
            1,
            NumberSeriesResetPolicy.Never));

        var customer = new User(UserType.Customer);
        customer.SetProfile("Customer");
        customer.SetContact("9999999999", null);
        var manager = new User(UserType.Employee);
        manager.SetProfile("Delivery Manager");
        manager.SetContact("9000000000", null);
        var staff = new User(UserType.Employee);
        staff.SetProfile("Delivery Staff One");
        staff.SetContact("9000000001", null);
        var role = new Role(AuthorizationCodes.DeliveryStaff, "Delivery Staff");
        var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
        db.AddRange(customer, manager, staff, role, branch);
        await db.SaveChangesAsync();

        staff.AssignRole(role, branch.Id);
        var category = new ProductCategory("MILK", "Milk");
        db.ProductCategories.Add(category);
        await db.SaveChangesAsync();
        var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
        var address = new CustomerAddress(
            customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
            "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
        db.AddRange(product, address);
        await db.SaveChangesAsync();

        var today = time.Today;
        var subscription = new Subscription(
            customer.Id,
            product.Id,
            address.Id,
            branch.Id,
            "sql-verify-sub-1",
            today,
            today.AddDays(7),
            1m,
            80m,
            2,
            product.Sku,
            product.Name,
            product.UnitOfMeasure,
            branch.Code,
            branch.Name,
            "1 Main Road, Central, Bengaluru, Karnataka 560001");
        subscription.AddDelivery(today);
        subscription.Activate(time.Now);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var configurationService = new RefundReplacementConfigurationService(db, time);
        var deliveryService = new DeliveryService(
            db,
            time,
            new TestPasswordHasher(),
            new DeliveryServiceTests.CapturingOtpDeliveryService(),
            new DeliveryServiceTests.CapturingRealtimePublisher(),
            Options.Create(new DeliveryOptions
            {
                OtpCodeLength = 6,
                OtpExpiryMinutes = 10,
                OtpMaximumAttempts = 3,
                MaximumLocationAgeMinutes = 15,
                MaximumLocationFutureSkewMinutes = 5,
                MaximumLocationsPerDelivery = 10,
                LocationRetentionDays = 30,
                SubscriptionGenerationWindowDays = 31
            }),
            new TestNotificationEventWriter(db, clock),
            new NumberSeriesService(db, time),
            new DeliveryOtpHandoffProtector(new EphemeralDataProtectionProvider()),
            refundReplacementConfigurationService: configurationService,
            refundReplacementMediaStorage: new DeliveryServiceTests.CapturingMediaStorage());
        var refundService = new RefundReplacementService(
            db,
            time,
            configurationService,
            new NumberSeriesService(db, time),
            new DeliveryServiceTests.CapturingMediaStorage(),
            new TestNotificationEventWriter(db, time));
        return new SeedData(
            db, deliveryService, refundService, configurationService,
            customer.Id, manager.Id, staff.Id, branch.Id, clock, time);
    }

    private static async Task<Guid> FailTicketAsync(SeedData seed, DateOnly? scheduledDate)
    {
        Guid deliveryId;
        if (scheduledDate.HasValue)
        {
            var subscription = await seed.Db.Subscriptions
                .Include(x => x.Deliveries)
                .SingleAsync();
            subscription.AddDelivery(scheduledDate.Value);
            await seed.Db.SaveChangesAsync();
            seed.Db.ChangeTracker.Clear();
            await seed.DeliveryService.MaterializeEligibleAsync(
                new DeliveryActor(seed.ManagerId, [seed.BranchId]),
                scheduledDate.Value,
                CancellationToken.None);
            deliveryId = await seed.Db.Deliveries.AsNoTracking()
                .Where(x => x.SubscriptionDelivery != null
                    && x.SubscriptionDelivery.ScheduledDate == scheduledDate.Value
                    && !x.SubscriptionDelivery.IsReplacement)
                .Select(x => x.PublicId)
                .SingleAsync();
        }
        else
        {
            await seed.DeliveryService.MaterializeEligibleAsync(
                new DeliveryActor(seed.ManagerId, [seed.BranchId]),
                seed.Time.Today,
                CancellationToken.None);
            deliveryId = await seed.Db.Deliveries.AsNoTracking()
                .Select(x => x.PublicId)
                .SingleAsync();
        }

        var production = new MilkProduction(
            seed.BranchId, seed.Time.Now.AddHours(-1), 12, 2m, "L", seed.ManagerId, "Morning", null);
        seed.Db.MilkProductions.Add(production);
        await seed.Db.SaveChangesAsync();
        var batch = new MilkBatch(
            seed.BranchId, production.Id, $"SQLV-{Guid.NewGuid():N}"[..12],
            seed.Time.Now.AddHours(-1), 2m, "L");
        seed.Db.MilkBatches.Add(batch);
        await seed.Db.SaveChangesAsync();

        await seed.DeliveryService.SaveBatchAllocationsAsync(
            new DeliveryActor(seed.ManagerId, [seed.BranchId]),
            deliveryId,
            new SaveDeliveryBatchAllocationsRequest(
                [new DeliveryBatchAllocationRequest(batch.Id, 1m)]),
            CancellationToken.None);
        await seed.DeliveryService.AssignAsync(
            new DeliveryActor(seed.ManagerId, [seed.BranchId]),
            deliveryId,
            new AssignDeliveryRequest(
                (await seed.Db.Users.AsNoTracking().SingleAsync(x => x.Id == seed.StaffId)).PublicId,
                null),
            CancellationToken.None);
        await seed.DeliveryService.FailAsync(
            new DeliveryActor(seed.StaffId, [seed.BranchId]),
            deliveryId,
            new FailDeliveryRequest(
                DeliveryFailureReasons.CustomerNotAvailable,
                "No response",
                12.972m,
                77.595m),
            CancellationToken.None,
            DeliveryServiceTests.DeliveryHarness.ProofImage());
        seed.Db.ChangeTracker.Clear();
        return await seed.Db.RefundReplacementRequests.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => x.PublicId)
            .LastAsync();
    }

    private static Task ChooseAsync(RefundReplacementService service, long customerId, Guid requestId) =>
        service.ChooseAsync(
            new RefundReplacementActor(customerId, new HashSet<long>(), false),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
            CancellationToken.None);
}
