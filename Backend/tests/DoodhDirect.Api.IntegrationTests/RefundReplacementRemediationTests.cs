using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Legacy remediation: an approved, order-backed Replacement ticket for a
/// one-time order is superseded (explicit terminal status, history preserved)
/// and exactly one linked Pending Refund ticket is created atomically. No
/// wallet credit or financial refund moves during remediation; the new ticket
/// follows the standard apply/waive approval and settlement workflow.
/// </summary>
public sealed class RefundReplacementRemediationTests
{
    [Fact]
    public async Task Remediate_Success_CreatesLinkedPendingRefund()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "1");
        var service = CreateRefundService(harness);

        var pair = await service.RemediateLegacyReplacementAsync(
            StaffActor(harness),
            legacyId,
            new RemediateLegacyReplacementRequest("Replacement has no fulfillment workflow for one-time orders."),
            CancellationToken.None);

        Assert.Equal("RR-REM-1", pair.LegacyTicket.RequestNumber);
        Assert.Equal(RefundReplacementStatus.Superseded, pair.LegacyTicket.Status);
        Assert.Equal(RefundReplacementType.Replacement, pair.LegacyTicket.Type);
        Assert.Equal(RefundReplacementSource.CustomerPostDelivery, pair.LegacyTicket.Source);
        Assert.Equal("RR-REM-1", pair.RefundTicket.SupersedesRequestNumber);
        Assert.True(pair.RefundTicket.SupersedesRequestPublicId.HasValue);
        Assert.Equal(pair.LegacyTicket.RequestId, pair.RefundTicket.SupersedesRequestPublicId.Value);
        Assert.Equal(pair.RefundTicket.RequestNumber, pair.LegacyTicket.SupersededByRequestNumber);
        Assert.Equal(RefundReplacementStatus.Pending, pair.RefundTicket.Status);
        Assert.Equal(RefundReplacementType.Refund, pair.RefundTicket.Type);
        Assert.Equal(RefundReplacementSource.CustomerPostDelivery, pair.RefundTicket.Source);
        Assert.Equal(pair.LegacyTicket.OrderPublicId, pair.RefundTicket.OrderPublicId);
        Assert.Equal(pair.LegacyTicket.DeliveryId, pair.RefundTicket.DeliveryId);

        var stored = await harness.Db.RefundReplacementRequests
            .AsNoTracking()
            .Include(x => x.RemediationRefunds)
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(2, stored.Count);
        var legacy = stored[0];
        var refund = stored[1];
        Assert.Equal(RefundReplacementStatus.Superseded, legacy.Status);
        Assert.Equal(RefundReplacementType.Replacement, legacy.Type);
        Assert.Equal(harness.Manager.Id, legacy.RemediatedByUserId);
        Assert.NotNull(legacy.RemediatedAt);
        Assert.Equal("Replacement has no fulfillment workflow for one-time orders.", legacy.RemediationReason);
        // Approval history preserved; completion stays null.
        Assert.NotNull(legacy.DecidedByUserId);
        Assert.NotNull(legacy.DecidedAt);
        Assert.Null(legacy.CompletedByUserId);
        Assert.Null(legacy.CompletedAt);
        Assert.Equal(RefundReplacementStatus.Pending, refund.Status);
        Assert.Equal(legacy.Id, refund.SupersedesRequestId);
        Assert.Single(legacy.RemediationRefunds);

        // Audit + notification only for the committed pair; no money moved.
        Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.REMEDIATE").ToListAsync());
        Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.SUBMIT").ToListAsync());
        var notification = Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.LegacyReplacementSuperseded).ToListAsync());
        var payload = notification.PayloadJson ?? string.Empty;
        // Both public ticket numbers are named; the message must describe the
        // legacy request as superseded and must not claim money already moved.
        Assert.Contains("RR-REM-1", payload, StringComparison.Ordinal);
        Assert.Contains(pair.RefundTicket.RequestNumber, payload, StringComparison.Ordinal);
        Assert.Contains("superseded", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Refund", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("has been refunded", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("has been issued", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credited to your wallet", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Remediate_MilkTestSource_PreservesSourceAndTestLink()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "2", RefundReplacementSource.MilkTestRejected);
        var service = CreateRefundService(harness);

        var pair = await service.RemediateLegacyReplacementAsync(
            StaffActor(harness),
            legacyId,
            new RemediateLegacyReplacementRequest("Milk-test replacement cannot be re-delivered for one-time orders."),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Superseded, pair.LegacyTicket.Status);
        Assert.Equal(RefundReplacementSource.MilkTestRejected, pair.RefundTicket.Source);
        Assert.Equal(pair.LegacyTicket.MilkTestPublicId, pair.RefundTicket.MilkTestPublicId);
        Assert.NotNull(pair.RefundTicket.MilkTestPublicId);
        Assert.Null(pair.RefundTicket.Deadline);
    }

    [Fact]
    public async Task Remediate_Retry_ReturnsExistingPairWithoutDuplicates()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "3");
        var service = CreateRefundService(harness);
        var reason = new RemediateLegacyReplacementRequest("Duplicate-safe retry.");

        var first = await service.RemediateLegacyReplacementAsync(StaffActor(harness), legacyId, reason, CancellationToken.None);
        var second = await service.RemediateLegacyReplacementAsync(StaffActor(harness), legacyId, reason, CancellationToken.None);

        Assert.Equal(first.RefundTicket.RequestId, second.RefundTicket.RequestId);
        Assert.Equal(first.LegacyTicket.RequestId, second.LegacyTicket.RequestId);
        Assert.Equal(2, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
        Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.REMEDIATE").ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.LegacyReplacementSuperseded).ToListAsync());
    }

    [Fact]
    public async Task Remediate_ChangedState_AbortsWithoutMutation()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "4");
        var legacy = await harness.Db.RefundReplacementRequests.SingleAsync(x => x.PublicId == legacyId);
        legacy.Complete(harness.Manager.Id, harness.TimeProvider.Now, "Delivered manually.");
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<ConflictException>(() => service.RemediateLegacyReplacementAsync(
            StaffActor(harness), legacyId,
            new RemediateLegacyReplacementRequest("Too late."),
            CancellationToken.None));

        Assert.Contains("no longer be remediated", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RefundReplacementStatus.Completed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.REMEDIATE").ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Remediate_DeliveredDelivery_Aborts()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "5", failDelivery: false);
        var service = CreateRefundService(harness);

        await Assert.ThrowsAsync<ConflictException>(() => service.RemediateLegacyReplacementAsync(
            StaffActor(harness), legacyId,
            new RemediateLegacyReplacementRequest("Delivery no longer failed."),
            CancellationToken.None));

        Assert.Equal(RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Remediate_ActiveUniqueness_PreservedAfterRemediation()
    {
        // The filtered unique index on the delivery cannot be seeded around:
        // inserting a second active ticket fails at the database itself, so
        // the service guard is defense-in-depth and the index is the enforcer.
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "6");
        var service = CreateRefundService(harness);
        await service.RemediateLegacyReplacementAsync(
            StaffActor(harness), legacyId,
            new RemediateLegacyReplacementRequest("Index stays protective."),
            CancellationToken.None);

        var db = harness.Db;
        var refund = await db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.SupersedesRequestId != null);
        var now = harness.TimeProvider.Now;
        var intruder = new RefundReplacementRequest(
            refund.OrderId, refund.DeliveryId, refund.CustomerId, refund.BranchId, null,
            RefundReplacementType.Refund, RefundReplacementSource.CustomerPostDelivery,
            "Second request.", null, now, now.AddHours(48));
        intruder.AssignRequestNumber("RR-REM-6-B");
        db.RefundReplacementRequests.Add(intruder);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(2, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Remediate_PriorSucceededRefund_Aborts()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "7");
        await SeedOrderPaymentAsync(harness, legacyId, 200m, PaymentMethod.Wallet, "7");
        await RecordExternalRefundAsync(harness, legacyId, 200m, "7");
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<ConflictException>(() => service.RemediateLegacyReplacementAsync(
            StaffActor(harness), legacyId,
            new RemediateLegacyReplacementRequest("Already settled externally."),
            CancellationToken.None));

        Assert.Contains("prior refund settlement", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync(x => x.PublicId == legacyId)).Status);
        // Aborted before any insert: only the legacy ticket exists.
        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Remediate_PartialSucceededRefund_AbortsAsAmbiguous()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "8");
        await SeedOrderPaymentAsync(harness, legacyId, 200m, PaymentMethod.Wallet, "8");
        await RecordExternalRefundAsync(harness, legacyId, 50m, "8");
        var service = CreateRefundService(harness);

        await Assert.ThrowsAsync<ConflictException>(() => service.RemediateLegacyReplacementAsync(
            StaffActor(harness), legacyId,
            new RemediateLegacyReplacementRequest("Partially settled."),
            CancellationToken.None));

        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Remediate_BlankReason_RejectedWithoutMutation(string? reason)
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "9");
        var service = CreateRefundService(harness);

        await Assert.ThrowsAsync<ValidationAppException>(() => service.RemediateLegacyReplacementAsync(
            StaffActor(harness), legacyId,
            new RemediateLegacyReplacementRequest(reason),
            CancellationToken.None));

        Assert.Equal(RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Remediate_WrongBranchStaff_NotFound()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "10");
        var service = CreateRefundService(harness);
        var outsider = new RefundReplacementActor(harness.Manager.Id, new HashSet<long> { -99 }, false);

        await Assert.ThrowsAsync<NotFoundException>(() => service.RemediateLegacyReplacementAsync(
            outsider, legacyId,
            new RemediateLegacyReplacementRequest("Out of scope."),
            CancellationToken.None));

        Assert.Equal(RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Remediate_CustomerActor_NotFound()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "11");
        var service = CreateRefundService(harness);

        await Assert.ThrowsAsync<NotFoundException>(() => service.RemediateLegacyReplacementAsync(
            CustomerActor(harness), legacyId,
            new RemediateLegacyReplacementRequest("Customer cannot remediate."),
            CancellationToken.None));

        Assert.Equal(RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>
    /// Stage 2.1: the staff read contract projects the linked delivery's
    /// authoritative status — Failed here — on both detail and list, so
    /// the UI can gate remediation without inferring from other fields.
    /// </summary>
    [Fact]
    public async Task ReadContract_ExposesFailedDeliveryStatus()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "12a");
        var service = CreateRefundService(harness);

        var detail = await service.GetForStaffAsync(
            StaffActor(harness), legacyId, CancellationToken.None);
        Assert.Equal(DeliveryStatus.Failed, detail.DeliveryStatus);

        var page = await service.ListForStaffAsync(
            StaffActor(harness),
            new RefundReplacementListQuery(Page: 1, PageSize: 20),
            CancellationToken.None);
        var row = Assert.Single(page.Items);
        Assert.Equal(DeliveryStatus.Failed, row.DeliveryStatus);
    }

    /// <summary>
    /// Stage 2.1: a non-Failed delivery is projected verbatim (Delivered
    /// here), so the UI fails closed instead of offering remediation.
    /// Branch authorization is preserved on the read.
    /// </summary>
    [Fact]
    public async Task ReadContract_ExposesNonFailedDeliveryStatus()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "12b", failDelivery: false);
        var service = CreateRefundService(harness);

        var detail = await service.GetForStaffAsync(
            StaffActor(harness), legacyId, CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, detail.DeliveryStatus);

        var outsider = new RefundReplacementActor(harness.Manager.Id, new HashSet<long> { -99 }, false);
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetForStaffAsync(
            outsider, legacyId, CancellationToken.None));
    }

    [Fact]
    public async Task Remediate_NonLegacyTicket_Aborts()
    {
        await using var harness = await CreateHarnessAsync();
        var refundId = await SeedLegacyReplacementAsync(harness, "12", type: RefundReplacementType.Refund, approve: false);
        var service = CreateRefundService(harness);

        await Assert.ThrowsAsync<ConflictException>(() => service.RemediateLegacyReplacementAsync(
            StaffActor(harness), refundId,
            new RemediateLegacyReplacementRequest("Not a legacy replacement."),
            CancellationToken.None));

        Assert.Equal(RefundReplacementStatus.Pending,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Remediate_TimeoutWorker_IgnoresPair()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "13");
        var service = CreateRefundService(harness);
        await service.RemediateLegacyReplacementAsync(
            StaffActor(harness), legacyId,
            new RemediateLegacyReplacementRequest("Timeout must ignore superseded pair."),
            CancellationToken.None);

        var processor = new DeliveryFailTimeoutProcessor(
            harness.Db,
            harness.TimeProvider,
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
            Options.Create(new DeliveryFailTimeoutOptions()),
            Options.Create(new PaymentOptions { Currency = "INR" }),
            NullLogger<DeliveryFailTimeoutProcessor>.Instance);

        Assert.Equal(0, await processor.ProcessExpiredTicketsAsync(CancellationToken.None));
        var statuses = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .OrderBy(x => x.Id).Select(x => x.Status).ToListAsync();
        Assert.Equal(
            new[] { RefundReplacementStatus.Superseded, RefundReplacementStatus.Pending },
            statuses);
    }

    [Fact]
    public async Task RemediatedRefund_FollowsStandardApprovePreviewCompleteWorkflow()
    {
        await using var harness = await CreateHarnessAsync();
        var legacyId = await SeedLegacyReplacementAsync(harness, "14");
        await SeedOrderPaymentAsync(harness, legacyId, 200m, PaymentMethod.Wallet, "14");
        var service = CreateRefundService(harness);

        var pair = await service.RemediateLegacyReplacementAsync(
            StaffActor(harness), legacyId,
            new RemediateLegacyReplacementRequest("Standard workflow from here."),
            CancellationToken.None);

        var preview = await service.PreviewOrderRefundAsync(
            StaffActor(harness), pair.RefundTicket.RequestId, applyDeduction: true, CancellationToken.None);
        Assert.Equal(180m, preview.ProposedRefund);

        var approved = await service.ApproveAsync(
            StaffActor(harness), pair.RefundTicket.RequestId,
            new ApproveRefundReplacementRequest("Customer fault confirmed", ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        Assert.Equal(10m, approved.AppliedDeductionPercentage);

        var completed = await service.CompleteAsync(
            StaffActor(harness), pair.RefundTicket.RequestId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.Equal(180m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        // Legacy ticket untouched by the refund flow.
        Assert.Equal(RefundReplacementStatus.Superseded,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync(x => x.PublicId == legacyId)).Status);
    }

    [Fact]
    public async Task SqlServer_Remediate_SucceedsUnderRealConstraints()
    {
        await using var harness = await SqlRemediationHarness.CreateAsync();
        await using var db = harness.CreateContext();
        var service = harness.CreateRefundService(db);

        var pair = await service.RemediateLegacyReplacementAsync(
            harness.StaffActor,
            harness.LegacyPublicId,
            new RemediateLegacyReplacementRequest("Real CHECK, index and FK enforcement."),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Superseded, pair.LegacyTicket.Status);
        Assert.Equal(RefundReplacementStatus.Pending, pair.RefundTicket.Status);

        await using var verify = harness.CreateContext();
        Assert.Equal(2, await verify.RefundReplacementRequests.AsNoTracking().CountAsync());
        var check = await verify.Database.SqlQueryRaw<string>(
            "SELECT [definition] AS [Value] FROM [sys].[check_constraints] WHERE [name] = 'CK_RefundReplacementRequest_Lifecycle'").SingleAsync();
        Assert.Contains("'Superseded'", check, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlServer_CheckConstraint_RejectsBareSuperseded()
    {
        await using var harness = await SqlRemediationHarness.CreateAsync();
        await using var db = harness.CreateContext();

        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE [dbo].[RefundReplacementRequest] SET [Status] = 'Superseded' WHERE [RequestNumber] = 'RR-SQL-REM-1'"));

        Assert.Contains("CK_RefundReplacementRequest_Lifecycle", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlServer_ConcurrentRemediate_ExactlyOnePairCreated()
    {
        await using var harness = await SqlRemediationHarness.CreateAsync();
        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();
        var firstService = harness.CreateRefundService(firstDb);
        var secondService = harness.CreateRefundService(secondDb);

        var outcomes = await Task.WhenAll(
            new[]
            {
                firstService.RemediateLegacyReplacementAsync(
                    harness.StaffActor, harness.LegacyPublicId,
                    new RemediateLegacyReplacementRequest("first"),
                    CancellationToken.None),
                secondService.RemediateLegacyReplacementAsync(
                    harness.StaffActor, harness.LegacyPublicId,
                    new RemediateLegacyReplacementRequest("second"),
                    CancellationToken.None),
            }.Select(async attempt =>
            {
                try
                {
                    return (Result: await attempt, Error: (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (Result: (RemediateLegacyReplacementResult?)null, Error: exception);
                }
            }));

        // Either interleaving is safe: a deadlock loser retries inside the
        // execution strategy, finds the committed pair and returns it
        // idempotently; a concurrency-token loser surfaces ConflictException.
        // In all cases exactly one pair exists and every success names it.
        var successes = outcomes.Where(x => x.Result is not null).Select(x => x.Result!).ToList();
        Assert.NotEmpty(successes);
        Assert.All(successes, x => Assert.Equal(successes[0].RefundTicket.RequestId, x.RefundTicket.RequestId));
        foreach (var failure in outcomes.Where(x => x.Error is not null))
        {
            Assert.IsType<ConflictException>(failure.Error);
        }

        await using var verify = harness.CreateContext();
        Assert.Equal(2, await verify.RefundReplacementRequests.AsNoTracking().CountAsync());
        Assert.Equal(1, await verify.RefundReplacementRequests.AsNoTracking()
            .CountAsync(x => x.SupersedesRequestId != null));
        Assert.Equal(1, await verify.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REMEDIATE"));
        Assert.Equal(1, await verify.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.LegacyReplacementSuperseded));
        Assert.Equal(0, await verify.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await verify.Refunds.AsNoTracking().CountAsync());

        // The legacy ticket cannot be superseded twice: a late retry after
        // the race returns the same pair instead of a second refund.
        await using var lateDb = harness.CreateContext();
        var lateService = harness.CreateRefundService(lateDb);
        var late = await lateService.RemediateLegacyReplacementAsync(
            harness.StaffActor, harness.LegacyPublicId,
            new RemediateLegacyReplacementRequest("late"),
            CancellationToken.None);
        Assert.Equal(successes[0].RefundTicket.RequestId, late.RefundTicket.RequestId);
        await using var reverify = harness.CreateContext();
        Assert.Equal(2, await reverify.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task SqlServer_Migrate_CreatesRemediationSchema()
    {
        var databaseName = $"DoodhDirect_Test_RemediationMigrate_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = @".\SQLEXPRESS",
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            MultipleActiveResultSets = true
        };
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlServer(builder.ConnectionString, sql => sql.CommandTimeout(120))
            .Options;
        await using var db = new DoodhDirectDbContext(options);
        try
        {
            // Full migration chain on an isolated database: proves the
            // migration dependencies (including the Stage 1 decision columns)
            // apply cleanly, independent of any model-drift check.
            await db.Database.MigrateAsync();

            var columns = await db.Database.SqlQueryRaw<string>(
                "SELECT [COLUMN_NAME] AS [Value] FROM [INFORMATION_SCHEMA].[COLUMNS] WHERE [TABLE_NAME] = 'RefundReplacementRequest' AND [COLUMN_NAME] IN ('RemediatedByUserId','RemediatedAtUtc','RemediationReason','SupersedesRequestId','AppliedDeductionPercentage','DeductionWaived')").ToListAsync();
            Assert.Equal(6, columns.Count);
            var check = await db.Database.SqlQueryRaw<string>(
                "SELECT [definition] AS [Value] FROM [sys].[check_constraints] WHERE [name] = 'CK_RefundReplacementRequest_Lifecycle'").SingleAsync();
            Assert.Contains("'Superseded'", check, StringComparison.Ordinal);
            var index = await db.Database.SqlQueryRaw<string>(
                "SELECT [name] AS [Value] FROM [sys].[indexes] WHERE [name] = 'IX_RefundReplacementRequest_SupersedesRequestId'").SingleAsync();
            Assert.Equal("IX_RefundReplacementRequest_SupersedesRequestId", index);
            var applied = await db.Database.SqlQueryRaw<string>(
                "SELECT [MigrationId] AS [Value] FROM [__EFMigrationsHistory] WHERE [MigrationId] LIKE '%OneTimeOrderRefundDecision%' OR [MigrationId] LIKE '%SupersededRemedyStatus%' ORDER BY [MigrationId]").ToListAsync();
            Assert.Equal(2, applied.Count);
        }
        finally
        {
            try
            {
                await db.Database.EnsureDeletedAsync();
            }
            catch
            {
                // Best-effort cleanup; the database name is unique per run.
            }
        }
    }

    private static async Task<DeliveryServiceTests.DeliveryHarness> CreateHarnessAsync()
    {
        var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.Db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        return harness;
    }

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static RefundReplacementActor StaffActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, false);

    private static RefundReplacementService CreateRefundService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
        Options.Create(new PaymentOptions { Currency = "INR" }));

    /// <summary>
    /// Seeds a failed one-time-order delivery with an approved legacy
    /// Replacement ticket, mirroring RR/000001 (milk-test source) and
    /// RR/000002 (post-delivery source). Returns the legacy public id.
    /// </summary>
    private static async Task<Guid> SeedLegacyReplacementAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        string suffix,
        RefundReplacementSource source = RefundReplacementSource.CustomerPostDelivery,
        RefundReplacementType type = RefundReplacementType.Replacement,
        bool approve = true,
        bool failDelivery = true)
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var order = new Order(
            harness.Customer.Id,
            null,
            harness.Branch.Id,
            $"one-time-rem-{suffix}-{Guid.NewGuid():N}",
            $"ORD-REM-{suffix}",
            200m,
            0m,
            harness.Branch.Code,
            harness.Branch.Name,
            "Home",
            "1 Main Road",
            null,
            "Central",
            "Bengaluru",
            "Karnataka",
            "560001",
            null,
            null,
            "Customer",
            "9999999999",
            12.9716m,
            77.5946m);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var delivery = Delivery.ForOrder(
            order.Id,
            harness.Customer.Id,
            harness.Branch.Id,
            DateOnly.FromDateTime(now),
            $"REF-REM-{suffix}",
            "Customer",
            "9999999999",
            "Home, 1 Main Road, Central, Bengaluru",
            null,
            12.9716m,
            77.5946m);
        delivery.AssignDeliveryNumber($"DLV-REM-{suffix}");
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync();

        delivery.Assign(harness.Staff.Id, harness.Staff.Id, now, null);
        delivery.PickUp(harness.Staff.Id, now, null);
        delivery.Start(harness.Staff.Id, now);
        delivery.Arrive(harness.Staff.Id, now);
        if (failDelivery)
        {
            delivery.Fail(
                harness.Staff.Id, now, DeliveryFailureReasons.CustomerNotAvailable, null, 12.9716m, 77.5946m);
        }
        else
        {
            delivery.RecordOtpVerified(harness.Staff.Id, now);
            delivery.Complete(harness.Staff.Id, now, null);
        }
        await db.SaveChangesAsync();

        long? milkTestId = null;
        if (source == RefundReplacementSource.MilkTestRejected)
        {
            var test = new MilkTest(delivery.Id, harness.Customer.Id, harness.Branch.Id, harness.Staff.Id, now);
            test.AddParameter("FAT", "Fat", 4.2m, "percent");
            test.AddImage(new MilkTestImage(0, $"rem-{suffix}.jpg", "proof.jpg", "image/jpeg", 512, harness.Staff.Id, now));
            db.MilkTests.Add(test);
            await db.SaveChangesAsync();
            test.Complete(harness.Staff.Id, now, null);
            test.Reject(now, "Customer rejected the test.");
            await db.SaveChangesAsync();
            milkTestId = test.Id;
        }

        var ticket = new RefundReplacementRequest(
            order.Id,
            delivery.Id,
            harness.Customer.Id,
            harness.Branch.Id,
            milkTestId,
            type,
            source,
            "Customer missed the delivery call.",
            null,
            now,
            source == RefundReplacementSource.MilkTestRejected ? null : now.AddHours(48));
        ticket.AssignRequestNumber($"RR-REM-{suffix}");
        if (approve)
        {
            ticket.Approve(harness.Manager.Id, now, "Legacy approval.");
        }
        db.RefundReplacementRequests.Add(ticket);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return ticket.PublicId;
    }

    private static async Task SeedOrderPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        Guid ticketId,
        decimal amount,
        PaymentMethod method,
        string keySuffix)
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var ticket = await db.RefundReplacementRequests.AsNoTracking().SingleAsync(x => x.PublicId == ticketId);
        var payment = new Payment(
            ticket.OrderId!.Value,
            harness.Customer.Id,
            method,
            amount,
            "INR",
            $"rem-pay-{keySuffix}-{Guid.NewGuid():N}",
            now.AddMinutes(15));
        payment.Succeed(
            method == PaymentMethod.Wallet ? null : $"gw-rem-{keySuffix}",
            method == PaymentMethod.Wallet ? "WALLET" : "captured",
            now);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task RecordExternalRefundAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        Guid ticketId,
        decimal amount,
        string keySuffix)
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var payment = await db.Payments
            .Include(x => x.Refunds)
            .SingleAsync();
        var refund = payment.StartRefund(
            amount, "External settlement", $"external-rem-{keySuffix}-{Guid.NewGuid():N}", harness.Manager.Id);
        refund.Succeed(now);
        payment.CompleteRefund(amount);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Throwaway SQL Server database so remediation exercises real CHECK,
    /// filtered-unique-index and serializable-locking behavior.
    /// </summary>
    private sealed class SqlRemediationHarness : IAsyncDisposable
    {
        private const string SqlServerInstance = @".\SQLEXPRESS";
        private readonly string connectionString;

        private SqlRemediationHarness(
            string connectionString,
            Guid legacyPublicId,
            long staffId,
            long branchId)
        {
            this.connectionString = connectionString;
            LegacyPublicId = legacyPublicId;
            StaffActor = new RefundReplacementActor(staffId, new HashSet<long> { branchId }, false);
        }

        public Guid LegacyPublicId { get; }
        public RefundReplacementActor StaffActor { get; }

        public DoodhDirectDbContext CreateContext()
        {
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

        public RefundReplacementService CreateRefundService(DoodhDirectDbContext db)
        {
            var time = new TestIndiaTimeProvider(
                new TestClock(new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified)));
            return new RefundReplacementService(
                db,
                time,
                new RefundReplacementConfigurationService(db, time),
                new NumberSeriesService(db, time),
                new ThrowawayMediaStorage(),
                new TestNotificationEventWriter(db, time),
                Options.Create(new PaymentOptions { Currency = "INR" }));
        }

        public static async Task<SqlRemediationHarness> CreateAsync()
        {
            var databaseName = $"DoodhDirect_Test_Remediation_{Guid.NewGuid():N}";
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
            staff.SetProfile("Support Agent");
            staff.SetContact("9000000011", null);
            db.AddRange(branch, customer, staff);
            await db.SaveChangesAsync();
            db.NumberSeries.Add(new NumberSeries(
                RefundReplacementService.SeriesCode, "Refund/replacement request numbers", "RR/{NUMBER:000000}",
                1, 1, NumberSeriesResetPolicy.Never));
            await db.SaveChangesAsync();

            var order = new Order(
                customer.Id, null, branch.Id, $"sql-rem-{Guid.NewGuid():N}", "ORD-SQL-REM-1",
                200m, 0m, branch.Code, branch.Name, "Home", "1 Main Road", null,
                "Central", "Bengaluru", "Karnataka", "560001", null, null,
                "Customer", "9999999999", 12.9716m, 77.5946m);
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            var delivery = Delivery.ForOrder(
                order.Id, customer.Id, branch.Id, DateOnly.FromDateTime(now),
                "REF-SQL-REM-1", "Customer", "9999999999",
                "Home, 1 Main Road, Central, Bengaluru", null, 12.9716m, 77.5946m);
            delivery.AssignDeliveryNumber("DLV-SQL-REM-1");
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();

            delivery.Assign(staff.Id, staff.Id, now, null);
            delivery.PickUp(staff.Id, now, null);
            delivery.Start(staff.Id, now);
            delivery.Arrive(staff.Id, now);
            delivery.Fail(staff.Id, now, DeliveryFailureReasons.CustomerNotAvailable, null, 12.9716m, 77.5946m);
            await db.SaveChangesAsync();

            var ticket = new RefundReplacementRequest(
                order.Id, delivery.Id, customer.Id, branch.Id, null,
                RefundReplacementType.Replacement, RefundReplacementSource.CustomerPostDelivery,
                "Customer missed the delivery call.", null, now, now.AddHours(48));
            ticket.AssignRequestNumber("RR-SQL-REM-1");
            ticket.Approve(staff.Id, now, "Legacy approval.");
            db.RefundReplacementRequests.Add(ticket);
            await db.SaveChangesAsync();

            var harness = new SqlRemediationHarness(
                builder.ConnectionString, ticket.PublicId, staff.Id, branch.Id);
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
                throw new NotSupportedException("Remediation records no media.");

            public Task<StoredMediaContent> OpenReadAsync(
                string storageKey, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Remediation records no media.");

            public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }
}
