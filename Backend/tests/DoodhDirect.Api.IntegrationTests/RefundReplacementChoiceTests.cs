using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-B: the owning customer records Refund / Re-Delivery on a delivery-fail
/// ticket awaiting choice. Records the choice only: no timeout worker, no
/// refund math, no wallet movement, no replacement delivery, no repricing.
/// </summary>
public sealed class RefundReplacementChoiceTests
{
    [Fact]
    public async Task Choose_RefundRecordsChoiceAlignsTypeAndAudits()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var requestId = await TicketPublicIdAsync(harness);

        var result = await ChoiceService(harness).ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, result.Status);
        Assert.Equal(CustomerChoice.Refund, result.CustomerChoice);
        Assert.NotNull(result.CustomerChoiceAt);
        Assert.Equal(RefundReplacementType.Refund, result.Type);
        Assert.False(string.IsNullOrWhiteSpace(result.RequestNumber));
        var deliveryNumber = (await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == result.DeliveryId)).DeliveryNumber;
        Assert.Equal(deliveryNumber, result.DeliveryNumber);

        var audit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.CUSTOMER_CHOICE").ToListAsync());
        Assert.Contains(result.RequestNumber, audit.NewValueJson ?? string.Empty);
        Assert.Contains("Refund", audit.NewValueJson ?? string.Empty);

        // No money moved, no replacement created, subscription untouched.
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
        var subscription = await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        Assert.Equal(harness.Subscription.PayableAmount, subscription.PayableAmount);
        Assert.Equal(harness.Subscription.ChargesTotal, subscription.ChargesTotal);
    }

    [Fact]
    public async Task Choose_ReDeliveryRecordsChoiceAsReplacementWithoutFulfillment()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var requestId = await TicketPublicIdAsync(harness);

        var result = await ChoiceService(harness).ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
            CancellationToken.None);

        Assert.Equal(CustomerChoice.ReDelivery, result.CustomerChoice);
        Assert.Equal(RefundReplacementType.Replacement, result.Type);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, result.Status);
        // Fulfillment belongs to D1-D: no replacement occurrence exists yet.
        Assert.Equal(1, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Choose_AfterDeadlineIsRejected()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var requestId = await TicketPublicIdAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        await Assert.ThrowsAsync<BusinessRuleException>(() => ChoiceService(harness).ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund),
            CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Null(ticket.CustomerChoice);
        // Expired tickets wait for D1-C timeout processing; nothing auto-resolves here.
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, ticket.Status);
    }

    [Fact]
    public async Task Choose_AnotherCustomersTicketIsNotFound()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var requestId = await TicketPublicIdAsync(harness);

        await Assert.ThrowsAsync<NotFoundException>(() => ChoiceService(harness).ChooseAsync(
            new RefundReplacementActor(harness.Manager.Id, new HashSet<long>(), false),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund),
            CancellationToken.None));
    }

    [Fact]
    public async Task Choose_StaffOrNonOwnerCannotResolve()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var requestId = await TicketPublicIdAsync(harness);

        await Assert.ThrowsAsync<NotFoundException>(() => ChoiceService(harness).ChooseAsync(
            new RefundReplacementActor(harness.Staff.Id, new HashSet<long> { harness.Branch.Id }, false),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund),
            CancellationToken.None));
    }

    [Fact]
    public async Task Choose_DifferentChoiceAfterResolutionIsRejected()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var requestId = await TicketPublicIdAsync(harness);
        var service = ChoiceService(harness);

        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None));

        var result = await service.GetForCustomerAsync(CustomerActor(harness), requestId, CancellationToken.None);
        Assert.Equal(CustomerChoice.Refund, result!.CustomerChoice);
        Assert.Equal(RefundReplacementType.Refund, result.Type);
    }

    [Fact]
    public async Task Choose_SameChoiceRetryIsIdempotentWithoutSideEffects()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await SeedStaffAsync(harness);
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var requestId = await TicketPublicIdAsync(harness);
        var service = ChoiceService(harness);
        var actor = CustomerActor(harness);

        var first = await service.ChooseAsync(
            actor, requestId, new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        var auditsBefore = await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.CUSTOMER_CHOICE").CountAsync();
        var eventsBefore = await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RefundReplacementChoiceRecorded).CountAsync();

        var second = await service.ChooseAsync(
            actor, requestId, new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        Assert.Equal(first.CustomerChoice, second.CustomerChoice);
        Assert.Equal(first.CustomerChoiceAt, second.CustomerChoiceAt);
        Assert.Equal(auditsBefore, await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.CUSTOMER_CHOICE").CountAsync());
        Assert.Equal(eventsBefore, await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RefundReplacementChoiceRecorded).CountAsync());
    }

    [Fact]
    public async Task Choose_NotifiesBranchStaffWithDeterministicKeys()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var (support, manager) = await SeedStaffAsync(harness);
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var requestId = await TicketPublicIdAsync(harness);

        await ChoiceService(harness).ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);

        var events = await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RefundReplacementChoiceRecorded)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, x => Assert.Contains(requestId.ToString("N"), x.EventKey));
        Assert.All(events, x => Assert.Contains("ReDelivery", x.EventKey));
        var recipientIds = events.Select(x => x.UserId).ToHashSet();
        Assert.Equal(new HashSet<long> { support.Id, manager.Id }, recipientIds);
        foreach (var notificationEvent in events)
        {
            var variables = JsonSerializer.Deserialize<JsonElement>(notificationEvent.PayloadJson).GetProperty("Variables");
            Assert.False(string.IsNullOrWhiteSpace(variables.GetProperty("requestNumber").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(variables.GetProperty("deliveryNumber").GetString()));
            Assert.Equal("ReDelivery", variables.GetProperty("choice").GetString());
        }
        var payload = JsonSerializer.Deserialize<JsonElement>(events[0].PayloadJson);
        Assert.Equal(
            $"/staff/refund-replacements/{requestId}",
            payload.GetProperty("DeepLink").GetString());
    }

    [Fact]
    public async Task Choose_ExposesDeliveryNumberWithoutInternalIds()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var requestId = await TicketPublicIdAsync(harness);

        var result = await ChoiceService(harness).ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        var occurrencePublicId = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.Id == harness.SubscriptionDelivery.Id)
            .Select(x => x.PublicId).SingleAsync();
        Assert.Equal(occurrencePublicId, result.SubscriptionDeliveryPublicId);
        Assert.Null(result.OrderId);
        Assert.False(string.IsNullOrWhiteSpace(result.DeliveryNumber));
        Assert.False(string.IsNullOrWhiteSpace(result.RequestNumber));
    }

    [Fact]
    public async Task Choose_MilkTestTicketSupportsChoice()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var milkService = new MilkTestService(
            harness.Db,
            harness.TimeProvider,
            harness.ProofStorage,
            new PassThroughImageValidator(),
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider));
        var customerActor = new MilkTestActor(harness.Customer.Id, new HashSet<long>(), false);
        var staffActor = new MilkTestActor(harness.Staff.Id, new HashSet<long> { harness.Branch.Id }, false);

        var requested = await milkService.RequestAsync(customerActor, deliveryId, CancellationToken.None);
        await milkService.UploadImageAsync(
            staffActor, requested.MilkTestId, new MemoryStream([0xFF, 0xD8, 0xFF], writable: false),
            "test.jpg", "image/jpeg", CancellationToken.None);
        await milkService.CompleteAsync(
            staffActor, requested.MilkTestId,
            new CompleteMilkTestRequest([new MilkTestParameterRequest("FAT", "Fat", 4.2m, "%")], null),
            CancellationToken.None);
        await milkService.RejectAsync(
            customerActor, requested.MilkTestId, new DecideMilkTestRequest("Milk was sour"), CancellationToken.None);

        var requestId = await TicketPublicIdAsync(harness);
        var result = await ChoiceService(harness).ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        Assert.Equal(CustomerChoice.Refund, result.CustomerChoice);
        Assert.Equal(RefundReplacementSource.MilkTestRejected, result.Source);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, result.Status);
    }

    [Fact]
    public async Task Choose_NonAwaitingTicketIsRejected()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var requestId = await TicketPublicIdAsync(harness);
        var service = ChoiceService(harness);

        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        // Staff approval after the recorded choice still works; a later choice
        // for the same value stays idempotent, anything else is rejected.
        await service.ApproveAsync(
            new RefundReplacementActor(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, true),
            requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        var same = await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        Assert.Equal(CustomerChoice.Refund, same.CustomerChoice);
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None));
    }

    [Fact]
    public async Task Choose_ConcurrentResolutionsOnSqlServer_ExactlyOneWins()
    {
        await using var harness = await SqlChoiceHarness.CreateAsync();
        var requestId = harness.RequestPublicId;

        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();
        var firstService = harness.CreateService(firstDb);
        var secondService = harness.CreateService(secondDb);
        var actor = new RefundReplacementActor(harness.CustomerId, new HashSet<long>(), false);

        var outcomes = await Task.WhenAll(
            new[]
            {
                firstService.ChooseAsync(actor, requestId, new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None),
                secondService.ChooseAsync(actor, requestId, new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None)
            }.Select(async attempt =>
            {
                try
                {
                    return (Result: await attempt, Error: (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (Result: (RefundReplacementRequestResult?)null, Error: exception);
                }
            }));

        Assert.Single(outcomes, x => x.Result is not null);
        var loser = Assert.Single(outcomes, x => x.Error is not null);
        Assert.IsType<ConflictException>(loser.Error);

        await using var verifyDb = harness.CreateContext();
        var ticket = Assert.Single(await verifyDb.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.True(ticket.CustomerChoice is CustomerChoice.Refund or CustomerChoice.ReDelivery);
        Assert.Equal(
            ticket.CustomerChoice,
            outcomes.Single(x => x.Result is not null).Result!.CustomerChoice);
        // No duplicate side effects: one audit, and notifications only for the winner.
        Assert.Single(await verifyDb.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.CUSTOMER_CHOICE").ToListAsync());
        var events = await verifyDb.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RefundReplacementChoiceRecorded)
            .ToListAsync();
        Assert.NotEmpty(events);
        Assert.All(events, x => Assert.Contains(ticket.CustomerChoice.ToString(), x.EventKey));
    }

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static async Task<Guid> FailTicketAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        return deliveryId;
    }

    private static async Task<Guid> TicketPublicIdAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.PublicId).SingleAsync();

    private static RefundReplacementService ChoiceService(DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

    private static async Task<(User Support, User Manager)> SeedStaffAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        var support = new User(UserType.Employee);
        support.SetProfile("Support Agent");
        support.SetContact("9000000011", null);
        var manager = new User(UserType.Employee);
        manager.SetProfile("Dairy Manager");
        manager.SetContact("9000000012", null);
        var supportRole = new Role(AuthorizationCodes.CustomerSupport, "Customer Support");
        var managerRole = new Role(AuthorizationCodes.DairyManager, "Dairy Manager");
        harness.Db.AddRange(support, manager, supportRole, managerRole);
        await harness.Db.SaveChangesAsync();
        support.AssignRole(supportRole, harness.Branch.Id);
        manager.AssignRole(managerRole, harness.Branch.Id);
        await harness.Db.SaveChangesAsync();
        return (support, manager);
    }

    private sealed class PassThroughImageValidator : IMilkTestImageValidator
    {
        public long MaximumFileSize => 10L * 1024L * 1024L;

        public Task<ValidatedMilkTestImage> ValidateAsync(
            Stream content,
            string fileName,
            string? declaredContentType,
            CancellationToken cancellationToken)
        {
            var buffered = new MemoryStream();
            content.CopyTo(buffered);
            buffered.Position = 0;
            return Task.FromResult(new ValidatedMilkTestImage(
                Path.GetFileName(fileName),
                declaredContentType ?? "image/jpeg",
                buffered.Length,
                buffered));
        }
    }

    /// <summary>
    /// Throwaway SQL Server database so the choice race exercises real
    /// row-version locking instead of pretending SQLite proves it.
    /// </summary>
    private sealed class SqlChoiceHarness : IAsyncDisposable
    {
        private const string SqlServerInstance = @".\SQLEXPRESS";
        private readonly string connectionString;

        private SqlChoiceHarness(string connectionString, Guid requestPublicId, long customerId)
        {
            this.connectionString = connectionString;
            RequestPublicId = requestPublicId;
            CustomerId = customerId;
        }

        public Guid RequestPublicId { get; }
        public long CustomerId { get; }

        public DoodhDirectDbContext CreateContext()
        {
            // Mirrors production resiliency (DependencyInjection: retry 5x) so a
            // deadlock victim retries into the recorded-choice conflict path.
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlServer(
                    connectionString,
                    sql =>
                    {
                        sql.CommandTimeout(60);
                        sql.EnableRetryOnFailure(maxRetryCount: 5);
                    })
                .Options;
            return new DoodhDirectDbContext(options);
        }

        public RefundReplacementService CreateService(DoodhDirectDbContext db)
        {
            var clock = new TestClock(new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified));
            var time = new TestIndiaTimeProvider(clock);
            return new RefundReplacementService(
                db,
                time,
                new Infrastructure.RefundReplacement.RefundReplacementConfigurationService(db, time),
                new NumberSeriesService(db, time),
                new ThrowawayMediaStorage(),
                new TestNotificationEventWriter(db, time));
        }

        public static async Task<SqlChoiceHarness> CreateAsync()
        {
            var databaseName = $"DoodhDirect_Test_Choice_{Guid.NewGuid():N}";
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
                .UseSqlServer(builder.ConnectionString, sql => sql.CommandTimeout(60))
                .Options;
            var db = new DoodhDirectDbContext(options);
            try
            {
                await db.Database.EnsureCreatedAsync();
            }
            catch (SqlException ex)
            {
                await db.DisposeAsync();
                throw new InvalidOperationException(
                    $"This test requires the local SQL Server Express instance ('{SqlServerInstance}').", ex);
            }

            var now = new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified);
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            customer.SetContact("9999999999", null);
            var staff = new User(UserType.Employee);
            staff.SetProfile("Delivery Staff");
            staff.SetContact("9000000001", null);
            var support = new User(UserType.Employee);
            support.SetProfile("Support Agent");
            support.SetContact("9000000011", null);
            var supportRole = new Role(AuthorizationCodes.CustomerSupport, "Customer Support");
            db.AddRange(branch, customer, staff, support, supportRole);
            await db.SaveChangesAsync();
            support.AssignRole(supportRole, branch.Id);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            db.AddRange(product, address);
            await db.SaveChangesAsync();

            var subscription = new Subscription(
                customer.Id, product.Id, address.Id, branch.Id, "choice-sql-1",
                DateOnly.FromDateTime(now), DateOnly.FromDateTime(now).AddDays(7),
                1m, 80m, 2, product.Sku, product.Name, product.UnitOfMeasure,
                branch.Code, branch.Name, "1 Main Road, Bengaluru");
            subscription.AddDelivery(DateOnly.FromDateTime(now));
            subscription.Activate(now);
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();
            var occurrence = subscription.Deliveries.Single();

            var delivery = Delivery.ForSubscriptionOccurrence(
                occurrence.Id, customer.Id, branch.Id, DateOnly.FromDateTime(now),
                $"SUB-{subscription.PublicId:N}-{DateOnly.FromDateTime(now):yyyyMMdd}",
                "Customer", "9999999999", "1 Main Road, Bengaluru", null, 12.9716m, 77.5946m);
            delivery.AssignDeliveryNumber("DLV/MAIN/000001");
            delivery.Assign(staff.Id, staff.Id, now, null);
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();

            delivery.Fail(
                staff.Id, now, DeliveryFailureReasons.CustomerNotAvailable, "No response", null, null);
            var ticket = new RefundReplacementRequest(
                null, delivery.Id, customer.Id, branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed,
                DeliveryFailureReasons.CustomerNotAvailable, null, now, now.AddHours(24),
                occurrence.Id, now, 24, 10m, DeliveryFailureReasons.CustomerNotAvailable,
                RefundReplacementStatus.AwaitingCustomerChoice);
            ticket.AssignRequestNumber("RR/TEST/000001");
            db.RefundReplacementRequests.Add(ticket);
            await db.SaveChangesAsync();

            var harness = new SqlChoiceHarness(builder.ConnectionString, ticket.PublicId, customer.Id);
            await db.DisposeAsync();
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = CreateContext();
            try
            {
                await db.Database.EnsureDeletedAsync();
            }
            catch
            {
                // Best-effort cleanup; the database name is unique per run.
            }
        }

        private sealed class ThrowawayMediaStorage : IMediaStorage
        {
            public Task<StoredMediaResult> SaveAsync(
                string storageKey, Stream content, string contentType, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Choice records no media.");

            public Task<StoredMediaContent> OpenReadAsync(
                string storageKey, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Choice records no media.");

            public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }
}
