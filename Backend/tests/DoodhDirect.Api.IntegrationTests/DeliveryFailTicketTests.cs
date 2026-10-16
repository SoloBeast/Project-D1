using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-A: subscription delivery-failure remedy ticket foundation.
/// Covers delivery-fail configuration, staff customer-fault failure with proof,
/// ticket creation/snapshots/deadline, notifications, idempotency/concurrency,
/// snapshot immutability, the subscription milk-test path, and preservation of
/// subscription financials and one-time order behavior. Explicitly does NOT cover
/// timeout workers, automatic refunds, deductions, wallets or re-delivery.
/// </summary>
public sealed class DeliveryFailTicketTests
{
    [Fact]
    public async Task Configuration_DefaultsExposeFailWindowAndDeductionBesideLegacyWindow()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();

        var result = await harness.ConfigurationService.GetAsync(CancellationToken.None);

        Assert.Equal(RefundReplacementConfigurationService.DefaultWindowHours, result.WindowHours);
        Assert.Equal(48, result.WindowHours);
        Assert.Equal(RefundReplacementConfigurationService.DefaultDeliveryFailWindowHours, result.DeliveryFailWindowHours);
        Assert.Equal(24, result.DeliveryFailWindowHours);
        Assert.Equal(RefundReplacementConfigurationService.DefaultDeliveryFailDeductionPercentage, result.DeliveryFailDeductionPercentage);
        Assert.Equal(10m, result.DeliveryFailDeductionPercentage);
    }

    [Fact]
    public async Task Configuration_CanReadAndConfigureFailWindowAndDeduction()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();

        var updated = await harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null, 36, 12.5m),
            harness.Manager.Id,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(48, updated.WindowHours);
        Assert.Equal(36, updated.DeliveryFailWindowHours);
        Assert.Equal(12.5m, updated.DeliveryFailDeductionPercentage);

        var reread = await harness.ConfigurationService.GetAsync(CancellationToken.None);
        Assert.Equal(36, reread.DeliveryFailWindowHours);
        Assert.Equal(12.5m, reread.DeliveryFailDeductionPercentage);
    }

    [Fact]
    public async Task Configuration_RejectsOutOfRangeFailValuesAndKeepsLegacyWindow()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null, 0, null),
            harness.Manager.Id, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null, null, -1m),
            harness.Manager.Id, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null, null, 100.01m),
            harness.Manager.Id, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(0, null, null),
            harness.Manager.Id, null, null, CancellationToken.None));

        var unchanged = await harness.ConfigurationService.GetAsync(CancellationToken.None);
        Assert.Equal(48, unchanged.WindowHours);
        Assert.Equal(24, unchanged.DeliveryFailWindowHours);
        Assert.Equal(10m, unchanged.DeliveryFailDeductionPercentage);
    }

    [Fact]
    public async Task StaffFail_CreatesExactlyOneTicketWithSnapshotsAndDeadline()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        var failedAt = harness.TimeProvider.Now;

        var failed = await harness.FailSubscriptionAsync(deliveryId, harness.Staff);

        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests
            .Include(x => x.Images)
            .Include(x => x.Delivery)
            .AsNoTracking()
            .ToListAsync());
        Assert.Equal(RefundReplacementSource.DeliveryFailed, ticket.Source);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, ticket.Status);
        Assert.Null(ticket.OrderId);
        Assert.Equal(harness.SubscriptionDelivery.Id, ticket.SubscriptionDeliveryId);
        Assert.Equal(failedAt, ticket.FailedAt);
        Assert.Equal(failedAt.AddHours(24), ticket.Deadline);
        Assert.Equal(24, ticket.DeliveryFailWindowHoursSnapshot);
        Assert.Equal(10m, ticket.DeliveryFailDeductionPercentageSnapshot);
        Assert.Equal(DeliveryFailureReasons.CustomerNotAvailable, ticket.FailureReasonSnapshot);
        Assert.Equal(DeliveryFailureReasons.CustomerNotAvailable, ticket.Reason);
        Assert.False(string.IsNullOrWhiteSpace(ticket.RequestNumber));
        // Public reference is the immutable DeliveryNumber, never internal ids.
        Assert.Equal(deliveryId, ticket.Delivery.PublicId);
        Assert.False(string.IsNullOrWhiteSpace(ticket.Delivery.DeliveryNumber));
    }

    [Fact]
    public async Task StaffFail_ReasonIsRequired()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest("Not a supported reason", null, null, null),
            CancellationToken.None,
            DeliveryServiceTests.DeliveryHarness.ProofImage()));

        Assert.Empty(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StaffFail_ProofImageIsRequiredForSubscriptionDeliveries()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        var missing = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(DeliveryFailureReasons.CustomerNotAvailable, null, null, null),
            CancellationToken.None));

        Assert.Equal("proof", missing.Field);
        Assert.Empty(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StaffFail_InvalidProofIsRejectedWithoutTicketOrOrphan()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        var badType = new FailDeliveryProof(new MemoryStream([0x01, 0x02]), "proof.txt", "text/plain", 2);
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(DeliveryFailureReasons.CustomerNotAvailable, null, null, null),
            CancellationToken.None,
            badType));

        // The rolled-back attempt left the tracked delivery mutated in memory;
        // reload the persisted (still active) state before the next attempt.
        harness.Db.ChangeTracker.Clear();
        var oversized = new FailDeliveryProof(new MemoryStream([0xFF]), "proof.jpg", "image/jpeg", 11L * 1024 * 1024);
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(DeliveryFailureReasons.CustomerNotAvailable, null, null, null),
            CancellationToken.None,
            oversized));

        Assert.Empty(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Empty(harness.ProofStorage.Files);
    }

    [Fact]
    public async Task StaffFail_SizeMismatchCleansUpStoredProof()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        var mismatched = new FailDeliveryProof(new MemoryStream([0xFF, 0xD8, 0xFF]), "proof.jpg", "image/jpeg", 99);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(DeliveryFailureReasons.CustomerNotAvailable, null, null, null),
            CancellationToken.None,
            mismatched));

        Assert.Empty(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Empty(harness.ProofStorage.Files);
        Assert.Single(harness.ProofStorage.DeletedKeys);
    }

    [Fact]
    public async Task StaffFail_OneTimeOrderFailureCreatesNoTicket()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        var failed = await harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(DeliveryFailureReasons.CustomerNotAvailable, "No response", null, null),
            CancellationToken.None);

        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Empty(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StaffFail_ProofIsRejectedForOneTimeOrderDeliveries()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(DeliveryFailureReasons.CustomerNotAvailable, null, null, null),
            CancellationToken.None,
            DeliveryServiceTests.DeliveryHarness.ProofImage()));
    }

    [Fact]
    public async Task StaffFail_NotifiesCustomerExactlyOnceWithDeliveryNumberAndDeadline()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        await harness.FailSubscriptionAsync(deliveryId, harness.Staff);

        var choiceEvents = await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RefundReplacementChoiceRequired)
            .ToListAsync();
        var choice = Assert.Single(choiceEvents);
        Assert.Equal(harness.Customer.Id, choice.UserId);
        var variables = JsonSerializer.Deserialize<JsonElement>(choice.PayloadJson).GetProperty("Variables");
        var deliveryNumber = (await harness.Db.Deliveries.AsNoTracking().SingleAsync(x => x.PublicId == deliveryId)).DeliveryNumber;
        Assert.Equal(deliveryNumber, variables.GetProperty("deliveryNumber").GetString());
        Assert.False(string.IsNullOrWhiteSpace(variables.GetProperty("deadline").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(variables.GetProperty("requestNumber").GetString()));
        // No internal ids or storage keys leak into the customer notification.
        var storageKey = Assert.Single(harness.ProofStorage.Files.Keys);
        Assert.DoesNotContain(storageKey, choice.PayloadJson, StringComparison.Ordinal);
        // Existing delivery-failed notification is preserved alongside.
        Assert.Single(await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.DeliveryFailed)
            .ToListAsync());
    }

    [Fact]
    public async Task StaffFail_WritesAuditsWithoutStorageKeysAndKeepsDeliveryAudit()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        await harness.FailSubscriptionAsync(deliveryId, harness.Staff);

        var audits = await harness.Db.AuditLogs.AsNoTracking().ToListAsync();
        Assert.Contains(audits, x => x.Action == "DELIVERY.FAIL");
        var created = Assert.Single(audits, x => x.Action == "REFUND_REPLACEMENT.DELIVERY_FAIL_TICKET_CREATED");
        Assert.Contains("DeliveryNumber", created.NewValueJson ?? string.Empty);
        var storageKey = Assert.Single(harness.ProofStorage.Files.Keys);
        Assert.DoesNotContain(storageKey, created.NewValueJson ?? string.Empty);
        Assert.DoesNotContain(storageKey, created.OldValueJson ?? string.Empty);
    }

    [Fact]
    public async Task StaffFail_PreservesHistoryProofAndSubscriptionFinancials()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        var payableBefore = harness.Subscription.PayableAmount;
        var chargesBefore = harness.Subscription.ChargesTotal;

        await harness.FailSubscriptionAsync(deliveryId, harness.Staff);

        harness.Db.ChangeTracker.Clear();
        var delivery = await harness.Db.Deliveries
            .Include(x => x.SubscriptionDelivery)
            .AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Equal(DeliveryFailureReasons.CustomerNotAvailable, delivery.FailureReason);
        Assert.NotNull(delivery.FailedAt);

        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests
            .Include(x => x.Images)
            .AsNoTracking()
            .ToListAsync());
        var image = Assert.Single(ticket.Images);
        Assert.Equal("proof.jpg", image.FileName);
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.True(image.FileSize > 0);
        Assert.True(harness.ProofStorage.Files.ContainsKey(image.StorageKey));

        var subscription = await harness.Db.Subscriptions
            .AsNoTracking()
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        Assert.Equal(payableBefore, subscription.PayableAmount);
        Assert.Equal(chargesBefore, subscription.ChargesTotal);
        Assert.Equal(0, subscription.UsedEntitlement);
    }

    [Fact]
    public async Task StaffFail_RetryCannotCreateDuplicateTicket()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        await harness.FailSubscriptionAsync(deliveryId, harness.Staff);
        // The already-failed transition is rejected by the pre-existing delivery
        // state guard (BusinessRuleException); no second ticket is created.
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.FailSubscriptionAsync(deliveryId, harness.Staff));

        Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StaffFail_TwoConcurrentFailuresProduceOneTicket()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();
        var firstService = harness.CreateService(firstDb);
        var secondService = harness.CreateService(secondDb);
        var request = new FailDeliveryRequest(
            DeliveryFailureReasons.CustomerNotAvailable, "No response", 12.972m, 77.595m);

        var outcomes = await Task.WhenAll(
            new[]
            {
                firstService.FailAsync(harness.StaffActor(harness.Staff), deliveryId, request, CancellationToken.None, DeliveryServiceTests.DeliveryHarness.ProofImage()),
                secondService.FailAsync(harness.StaffActor(harness.Staff), deliveryId, request, CancellationToken.None, DeliveryServiceTests.DeliveryHarness.ProofImage())
            }.Select(async attempt =>
            {
                try
                {
                    return (Result: await attempt, Error: (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (Result: (DeliveryResult?)null, Error: exception);
                }
            }));

        // Both attempts may succeed through the idempotent reuse path, or the
        // loser may be rejected on the already-failed delivery — but exactly one
        // failure and one ticket must exist either way.
        Assert.Contains(outcomes, x => x.Result is not null);
        harness.Db.ChangeTracker.Clear();
        Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        var delivery = await harness.Db.Deliveries.AsNoTracking().SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
    }

    [Fact]
    public async Task StaffFail_LaterConfigChangesDoNotMutateTicket()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        await harness.FailSubscriptionAsync(deliveryId, harness.Staff);
        var before = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());

        await harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null, 72, 25m),
            harness.Manager.Id, null, null, CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var after = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(before.Deadline, after.Deadline);
        Assert.Equal(24, after.DeliveryFailWindowHoursSnapshot);
        Assert.Equal(10m, after.DeliveryFailDeductionPercentageSnapshot);
    }

    [Fact]
    public async Task MilkTestRejection_SubscriptionCreatesOneTicketWithChoiceNotification()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var milkService = CreateMilkService(harness);
        var customerActor = new MilkTestActor(harness.Customer.Id, new HashSet<long>(), false);
        var staffActor = new MilkTestActor(harness.Staff.Id, new HashSet<long> { harness.Branch.Id }, false);

        var requested = await milkService.RequestAsync(customerActor, deliveryId, CancellationToken.None);
        await milkService.UploadImageAsync(
            staffActor, requested.MilkTestId, new MemoryStream([0xFF, 0xD8, 0xFF], writable: false),
            "test.jpg", "image/jpeg", CancellationToken.None);
        await milkService.CompleteAsync(
            staffActor,
            requested.MilkTestId,
            new CompleteMilkTestRequest([new MilkTestParameterRequest("FAT", "Fat", 4.2m, "%")], null),
            CancellationToken.None);
        var failedAt = harness.TimeProvider.Now;
        await milkService.RejectAsync(customerActor, requested.MilkTestId, new DecideMilkTestRequest("Milk was sour"), CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var delivery = await harness.Db.Deliveries.AsNoTracking().SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Equal(DeliveryFailureReasons.CustomerRejectedMilkTest, delivery.FailureReason);

        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(RefundReplacementSource.MilkTestRejected, ticket.Source);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, ticket.Status);
        Assert.Null(ticket.OrderId);
        Assert.Equal(harness.SubscriptionDelivery.Id, ticket.SubscriptionDeliveryId);
        Assert.Equal(failedAt, ticket.FailedAt);
        Assert.Equal(failedAt.AddHours(24), ticket.Deadline);
        Assert.Equal(24, ticket.DeliveryFailWindowHoursSnapshot);
        Assert.Equal(10m, ticket.DeliveryFailDeductionPercentageSnapshot);
        var milkTestId = await harness.Db.MilkTests
            .AsNoTracking()
            .Where(x => x.PublicId == requested.MilkTestId)
            .Select(x => x.Id)
            .SingleAsync();
        Assert.Equal(milkTestId, ticket.MilkTestId);

        Assert.Single(await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.DeliveryFailed)
            .ToListAsync());
        var choice = Assert.Single(await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RefundReplacementChoiceRequired)
            .ToListAsync());
        Assert.Contains(
            delivery.DeliveryNumber ?? string.Empty,
            JsonSerializer.Deserialize<JsonElement>(choice.PayloadJson).GetProperty("Variables").GetProperty("deliveryNumber").GetString() ?? string.Empty);
    }

    [Fact]
    public async Task MilkTestRejection_RejectAfterStaffFailReusesSingleTicket()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        // Staff failure path runs first via the creator directly so the delivery
        // stays active for the milk-test rejection that follows.
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var milkService = CreateMilkService(harness);
        var customerActor = new MilkTestActor(harness.Customer.Id, new HashSet<long>(), false);
        var staffActor = new MilkTestActor(harness.Staff.Id, new HashSet<long> { harness.Branch.Id }, false);

        var requested = await milkService.RequestAsync(customerActor, deliveryId, CancellationToken.None);
        await milkService.UploadImageAsync(
            staffActor, requested.MilkTestId, new MemoryStream([0xFF, 0xD8, 0xFF], writable: false),
            "test.jpg", "image/jpeg", CancellationToken.None);
        await milkService.CompleteAsync(
            staffActor,
            requested.MilkTestId,
            new CompleteMilkTestRequest([new MilkTestParameterRequest("FAT", "Fat", 4.2m, "%")], null),
            CancellationToken.None);

        // Simulate the staff-fail path winning the race: fail the delivery with
        // proof first, then record the customer rejection directly. The rejection
        // reuses the existing ticket instead of creating a duplicate.
        await harness.FailSubscriptionAsync(deliveryId, harness.Staff);
        var first = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());

        harness.Db.ChangeTracker.Clear();
        var delivery = await harness.Db.Deliveries.SingleAsync(x => x.PublicId == deliveryId);
        var creator = new DeliveryFailTicketCreator(
            harness.Db,
            harness.ConfigurationService,
            new DoodhDirect.Infrastructure.Setup.NumberSeriesService(harness.Db, harness.TimeProvider),
            harness.ProofStorage,
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider));
        var milkTestId = await harness.Db.MilkTests
            .AsNoTracking()
            .Where(x => x.PublicId == requested.MilkTestId)
            .Select(x => x.Id)
            .SingleAsync();
        var reused = await creator.CreateOrReuseAsync(
            delivery,
            milkTestId,
            RefundReplacementSource.MilkTestRejected,
            null,
            harness.Customer.Id,
            harness.TimeProvider.Now,
            CancellationToken.None);

        Assert.False(reused.Created);
        Assert.Equal(first.PublicId, reused.Ticket.PublicId);
        await harness.Db.SaveChangesAsync();
        Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
    }

    private static MilkTestService CreateMilkService(DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ProofStorage,
        new PassThroughImageValidator(),
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

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
}
