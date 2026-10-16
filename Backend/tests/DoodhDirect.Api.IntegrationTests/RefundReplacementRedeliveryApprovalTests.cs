using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 2: approval-time re-delivery creation inside
/// <c>DecideAsync(approve: true)</c>. Successful approvals create exactly one
/// Scheduled replacement from the ticket's own source occurrence; proposal and
/// staff dates obey weekday/cutoff/branch/conflict rules; entitlement and
/// financials stay untouched; retries and concurrent approvals cannot
/// duplicate the replacement; one-time approval behavior is unchanged.
/// </summary>
public sealed class RefundReplacementRedeliveryApprovalTests
{
    [Fact]
    public async Task Approve_ReDelivery_CreatesScheduledReplacementFromTicketSource()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        // No schedules on the harness subscription: first cutoff-satisfiable
        // date after the approval day (2026-08-18).
        Assert.Equal(new DateOnly(2026, 8, 18), approved.ReplacementDate);
        Assert.NotNull(approved.ReplacementDeliveryPublicId);

        var rows = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .OrderBy(x => x.ScheduledDate).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(SubscriptionDeliveryStatus.Failed, rows[0].Status);
        Assert.False(rows[0].IsReplacement);
        var replacement = rows[1];
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
        Assert.True(replacement.IsReplacement);
        Assert.Equal(rows[0].Id, replacement.ReplacesSubscriptionDeliveryId);
        var ticketId = await TicketDbIdAsync(harness);
        Assert.Equal(ticketId, replacement.RefundReplacementRequestId);
        Assert.Equal(approved.ReplacementDeliveryPublicId, replacement.PublicId);

        // Frozen copy from the failed occurrence.
        Assert.Equal(rows[0].Slot, replacement.Slot);
        Assert.Equal(rows[0].BranchId, replacement.BranchId);
        Assert.Equal(rows[0].Quantity, replacement.Quantity);
        Assert.Equal(rows[0].BranchCodeSnapshot, replacement.BranchCodeSnapshot);
        Assert.Equal(rows[0].BranchNameSnapshot, replacement.BranchNameSnapshot);
        Assert.Equal(rows[0].AddressSnapshot, replacement.AddressSnapshot);

        // Entitlement and financials untouched by approval.
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(0, subscription.UsedEntitlement);
        Assert.Equal(2, subscription.TotalEntitlement);
        Assert.Equal(harness.Subscription.PayableAmount, subscription.PayableAmount);
        Assert.Equal(harness.Subscription.ChargesTotal, subscription.ChargesTotal);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Approve_ReDelivery_UsesStaffEditedDateAndAuditsIt()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        var approved = await service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest("Re-drive on Thursday", new DateOnly(2026, 8, 20)),
            CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 8, 20), approved.ReplacementDate);
        var replacement = Assert.Single(await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.IsReplacement).ToListAsync());
        Assert.Equal(new DateOnly(2026, 8, 20), replacement.ScheduledDate);
        Assert.Equal("Re-drive on Thursday", replacement.ReplacementReason);

        var audit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.APPROVE").ToListAsync());
        Assert.Contains("2026-08-20", audit.NewValueJson ?? string.Empty);
        Assert.Contains(requestId.ToString(), audit.EntityId);

        var decided = Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventKey == $"refund-replacement:{requestId:N}:decided:Approved").ToListAsync());
        Assert.Contains("2026-08-20", decided.PayloadJson ?? string.Empty);
    }

    [Fact]
    public async Task Approve_ReDelivery_UsesFailureSnapshotWhenRemarksAbsent()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        var snapshot = (await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync()).FailureReasonSnapshot;

        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        var replacement = Assert.Single(await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.IsReplacement).ToListAsync());
        Assert.Equal(snapshot, replacement.ReplacementReason);
    }

    [Fact]
    public async Task Approve_ReDelivery_RejectsConflictingDate()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        // The failed occurrence itself sits on 2026-08-16.
        var exception = await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest(null, new DateOnly(2026, 8, 16)),
            CancellationToken.None));
        Assert.Equal("replacementDate", exception.Field);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
        Assert.Equal(
            RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Approve_ReDelivery_RejectsOffScheduleWeekday()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        await AddScheduleAsync(harness, DayOfWeek.Wednesday);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        // 2026-08-24 is a Monday: cutoff and conflict pass, weekday fails.
        var exception = await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest(null, new DateOnly(2026, 8, 24)),
            CancellationToken.None));
        Assert.Equal("replacementDate", exception.Field);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Approve_ReDelivery_RejectsDateInsideCutoff()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        // Next-day 00:00 minus the default 24h cutoff already passed at 09:30.
        var exception = await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest(null, new DateOnly(2026, 8, 17)),
            CancellationToken.None));
        Assert.Equal("replacementDate", exception.Field);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Approve_ReDelivery_SchedulesBeyondEndDateWithoutExtendingTerm()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        // Only Mondays: the next eligible Monday (2026-08-24) falls after the
        // subscription EndDate (2026-08-23).
        await AddScheduleAsync(harness, DayOfWeek.Monday);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 8, 24), approved.ReplacementDate);
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(new DateOnly(2026, 8, 23), subscription.EndDate);
        Assert.Equal(0, subscription.UsedEntitlement);
    }

    [Fact]
    public async Task Approve_ReDelivery_CopiesFrozenSnapshotsNotLiveData()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);

        // Mutate live master/operational data after the failure: the
        // replacement must still mirror the frozen occurrence snapshots.
        var product = await harness.Db.Products.SingleAsync();
        product.Update(product.CategoryId, product.Sku, "Premium Milk", null, product.UnitOfMeasure, 100m);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        var rows = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .OrderBy(x => x.ScheduledDate).ToListAsync();
        Assert.Equal(rows[0].Quantity, rows[1].Quantity);
        Assert.Equal(rows[0].BranchId, rows[1].BranchId);
        Assert.Equal(rows[0].BranchCodeSnapshot, rows[1].BranchCodeSnapshot);
        Assert.Equal(rows[0].BranchNameSnapshot, rows[1].BranchNameSnapshot);
        Assert.Equal(rows[0].AddressSnapshot, rows[1].AddressSnapshot);
        Assert.Equal(rows[0].Slot, rows[1].Slot);
        Assert.Equal(100m, (await harness.Db.Products.AsNoTracking().SingleAsync()).Price);
    }

    [Fact]
    public async Task Approve_ReDelivery_FailsClosedWhenBranchInactive()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var branch = await harness.Db.Branches.SingleAsync(x => x.Id == harness.Branch.Id);
        branch.Deactivate();
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        await Assert.ThrowsAsync<ConflictException>(() => service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
        Assert.Equal(
            RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Approve_RefundChoice_CreatesNoReplacement()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await service.ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund),
            CancellationToken.None);

        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Null(approved.ReplacementDate);
        Assert.Null(approved.ReplacementDeliveryPublicId);
        Assert.Equal(1, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Approve_PendingTicketWithoutChoice_CreatesNoReplacement()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);

        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Null(approved.ReplacementDate);
        Assert.Equal(1, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Approve_ReDelivery_RejectsWhenReplacementAlreadyExists()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        // Simulate a replacement already routed from this ticket.
        var subscription = await harness.Db.Subscriptions
            .Include(x => x.Deliveries).SingleAsync();
        var source = subscription.Deliveries.Single(x => x.Status == SubscriptionDeliveryStatus.Failed);
        subscription.AddReplacementDelivery(source, new DateOnly(2026, 8, 18), TicketDbId(harness), null);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<ConflictException>(() => service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(2, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
        Assert.Equal(
            RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Approve_RetryAfterSuccess_ConflictsWithoutDuplicatingReplacement()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(2, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Propose_ReturnsNextEligibleDate()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        var proposal = await service.ProposeReplacementDateAsync(
            StaffActor(harness), requestId, CancellationToken.None);

        Assert.Equal(requestId, proposal.RequestId);
        Assert.Equal(new DateOnly(2026, 8, 18), proposal.ProposedDate);
        // Advisory only: nothing persisted.
        Assert.Equal(1, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Propose_RespectsWeekdaySchedule()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        await AddScheduleAsync(harness, DayOfWeek.Wednesday);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        var proposal = await service.ProposeReplacementDateAsync(
            StaffActor(harness), requestId, CancellationToken.None);

        // Wednesday 2026-08-19 is the first eligible weekday.
        Assert.Equal(new DateOnly(2026, 8, 19), proposal.ProposedDate);
    }

    [Fact]
    public async Task Propose_RejectsNonRedeliveryTicket()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await service.ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => service.ProposeReplacementDateAsync(
            StaffActor(harness), requestId, CancellationToken.None));
    }

    [Fact]
    public async Task Propose_UnknownOrOutOfScopeTicketIsNotFound()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        await Assert.ThrowsAsync<NotFoundException>(() => service.ProposeReplacementDateAsync(
            StaffActor(harness), Guid.NewGuid(), CancellationToken.None));

        var outsider = new RefundReplacementActor(
            harness.Manager.Id, new HashSet<long> { harness.Branch.Id + 999 }, false);
        await Assert.ThrowsAsync<NotFoundException>(() => service.ProposeReplacementDateAsync(
            outsider, requestId, CancellationToken.None));
    }

    [Fact]
    public async Task Approve_ConcurrentApprovalsOnSqlServer_CreateExactlyOneReplacement()
    {
        await using var harness = await SqlRedeliveryHarness.CreateAsync();
        var actor = new RefundReplacementActor(harness.StaffId, new HashSet<long> { harness.BranchId }, false);

        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();
        var firstService = harness.CreateService(firstDb);
        var secondService = harness.CreateService(secondDb);

        var outcomes = await Task.WhenAll(
            new[]
            {
                firstService.ApproveAsync(
                    actor, harness.RequestPublicId, new ApproveRefundReplacementRequest(null), CancellationToken.None),
                secondService.ApproveAsync(
                    actor, harness.RequestPublicId, new ApproveRefundReplacementRequest(null), CancellationToken.None)
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
        Assert.Equal(RefundReplacementStatus.Approved, ticket.Status);
        var replacements = await verifyDb.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.IsReplacement).ToListAsync();
        var replacement = Assert.Single(replacements);
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
        Assert.Equal(ticket.SubscriptionDeliveryId, replacement.ReplacesSubscriptionDeliveryId);
        Assert.Equal(ticket.Id, replacement.RefundReplacementRequestId);
        var winner = outcomes.Single(x => x.Result is not null).Result!;
        Assert.Equal(replacement.ScheduledDate, winner.ReplacementDate);
        Assert.Equal(replacement.PublicId, winner.ReplacementDeliveryPublicId);
    }

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static RefundReplacementActor StaffActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, true);

    private static async Task<Guid> FailTicketAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        return deliveryId;
    }

    private static async Task FailAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);

    private static async Task<Guid> TicketPublicIdAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.PublicId).SingleAsync();

    private static async Task<long> TicketDbIdAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.Id).SingleAsync();

    private static long TicketDbId(DeliveryServiceTests.DeliveryHarness harness) =>
        harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.Id).Single();

    private static async Task ChooseReDeliveryAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid requestId) =>
        await service.ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
            CancellationToken.None);

    private static async Task AddScheduleAsync(
        DeliveryServiceTests.DeliveryHarness harness, DayOfWeek day)
    {
        var subscription = await harness.Db.Subscriptions
            .Include(x => x.Schedules).SingleAsync();
        subscription.AddSchedule(day);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private static RefundReplacementService CreateService(DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

    /// <summary>
    /// Throwaway SQL Server database so the approval race exercises real
    /// serializable locking and row-version checks instead of SQLite.
    /// </summary>
    private sealed class SqlRedeliveryHarness : IAsyncDisposable
    {
        private const string SqlServerInstance = @".\SQLEXPRESS";
        private readonly string connectionString;
        private readonly TestClock clock;

        private SqlRedeliveryHarness(
            string connectionString, TestClock clock, Guid requestPublicId, long staffId, long branchId)
        {
            this.connectionString = connectionString;
            this.clock = clock;
            RequestPublicId = requestPublicId;
            StaffId = staffId;
            BranchId = branchId;
        }

        public Guid RequestPublicId { get; }
        public long StaffId { get; }
        public long BranchId { get; }

        public DoodhDirectDbContext CreateContext()
        {
            // Mirrors production resiliency so a deadlock victim retries into
            // the already-approved conflict path.
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
            var time = new TestIndiaTimeProvider(clock);
            return new RefundReplacementService(
                db,
                time,
                new Infrastructure.RefundReplacement.RefundReplacementConfigurationService(db, time),
                new NumberSeriesService(db, time),
                new ThrowawayMediaStorage(),
                new TestNotificationEventWriter(db, time));
        }

        public static async Task<SqlRedeliveryHarness> CreateAsync()
        {
            var databaseName = $"DoodhDirect_Test_Redelivery_{Guid.NewGuid():N}";
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = SqlServerInstance,
                InitialCatalog = databaseName,
                IntegratedSecurity = true,
                Encrypt = true,
                TrustServerCertificate = true,
                MultipleActiveResultSets = true
            };
            var clock = new TestClock(new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified));
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

            var now = clock.Now;
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            customer.SetContact("9999999999", null);
            var staff = new User(UserType.Employee);
            staff.SetProfile("Support Agent");
            staff.SetContact("9000000011", null);
            db.AddRange(branch, customer, staff);
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
                customer.Id, product.Id, address.Id, branch.Id, "redelivery-sql-1",
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

            // Fail the occurrence and route the ticket to a recorded
            // ReDelivery choice, mirroring the staff failure path.
            subscription.MarkFailed(occurrence, now);
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

            ticket.RecordCustomerChoice(CustomerChoice.ReDelivery, now);
            await db.SaveChangesAsync();
            var requestPublicId = ticket.PublicId;
            var staffId = staff.Id;
            var branchId = branch.Id;
            await db.DisposeAsync();

            return new SqlRedeliveryHarness(builder.ConnectionString, clock, requestPublicId, staffId, branchId);
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = CreateContext();
            await db.Database.EnsureDeletedAsync();
        }

        private sealed class ThrowawayMediaStorage : IMediaStorage
        {
            public Task<StoredMediaResult> SaveAsync(
                string storageKey, Stream content, string contentType, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Approval records no media.");

            public Task<StoredMediaContent> OpenReadAsync(
                string storageKey, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Approval records no media.");

            public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }
}
