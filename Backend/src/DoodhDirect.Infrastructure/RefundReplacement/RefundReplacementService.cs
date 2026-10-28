using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Customer refund/replacement request workflow.
///
/// The backend is the single source of truth for eligibility and for the
/// post-delivery window: the deadline is computed as
/// <c>Delivery.CompletedAt + configured window hours</c> (or
/// <c>Delivery.FailedAt + configured window hours</c> for a failed delivery)
/// using the server-authoritative timestamps and the India-local clock. The
/// client never supplies a timestamp, an order id or a branch — the request is
/// bound to the delivery the authenticated customer actually owns.
///
/// A request opened from a customer-rejected doorstep milk test follows the
/// special flow: it is exempt from the window and from the proof image
/// requirement, and it auto-links the order, delivery and milk test.
///
/// No settlement rule is invented here: accepting a request records the
/// decision only. Refund execution and replacement-delivery creation remain
/// separate, explicitly-authorized operations.
/// </summary>
public sealed class RefundReplacementService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider,
    IRefundReplacementConfigurationService configurationService,
    INumberSeriesService numberSeriesService,
    IMediaStorage mediaStorage,
    INotificationEventWriter notificationEventWriter,
    IOptions<PaymentOptions>? paymentOptions = null,
    BankDetailProtector? bankDetailProtector = null,
    ManualBankBeneficiaryProtector? beneficiaryProtector = null) : IRefundReplacementService
{
    // Protectors are optional so existing direct constructions keep compiling;
    // production DI always supplies the durable key-ring-backed instances.
    private readonly BankDetailProtector _bankDetails =
        bankDetailProtector ?? new BankDetailProtector(new EphemeralDataProtectionProvider());

    private readonly ManualBankBeneficiaryProtector _beneficiary =
        beneficiaryProtector ?? new ManualBankBeneficiaryProtector(new EphemeralDataProtectionProvider());
    /// <summary>Numbering series used for the human-readable request number.</summary>
    public const string SeriesCode = "REFUNDREPLACEMENT";

    private const string EntityType = "RefundReplacementRequest";
    private const int MaximumReasonLength = 1000;
    private const int MaximumRemarksLength = 1000;
    private const int MaximumFileNameLength = 255;
    private const int DefaultPageSize = 20;
    private const int MaximumPageSize = 100;
    private const long MaximumImageBytes = 10L * 1024L * 1024L;

    public async Task<RefundReplacementEligibilityResult> GetEligibilityAsync(
        RefundReplacementActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        // Ownership is enforced in the predicate: another customer's delivery is
        // indistinguishable from a missing one.
        var delivery = await dbContext.Deliveries
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.PublicId == deliveryId && x.CustomerId == actor.UserId,
                cancellationToken) ?? throw new NotFoundException("The delivery was not found.");

        var configuration = await configurationService.GetAsync(cancellationToken);
        var windowHours = configuration.WindowHours;

        var hasActiveRequest = await dbContext.RefundReplacementRequests
            .AnyAsync(
                x => x.DeliveryId == delivery.Id
                    && (x.Status == RefundReplacementStatus.Pending
                        || x.Status == RefundReplacementStatus.Approved),
                cancellationToken);

        // Resolve the relationship from persisted data. A failed delivery is eligible
        // only when this customer's rejected test belongs to this exact delivery.
        var hasRejectedMilkTest = await dbContext.MilkTests
            .AnyAsync(
                x => x.DeliveryId == delivery.Id
                    && x.CustomerId == actor.UserId
                    && x.CustomerDecision == MilkTestCustomerDecision.Rejected,
                cancellationToken);
        var isMilkTestRejectedFlow = delivery.Status == DeliveryStatus.Failed && hasRejectedMilkTest;
        // A failed one-time-order delivery (e.g. customer not available) opens
        // the same customer-raised flow with the window anchored at the
        // failure timestamp instead of the completion timestamp.
        var isFailedDeliveryFlow =
            !isMilkTestRejectedFlow
            && delivery.Status == DeliveryStatus.Failed
            && delivery.FailedAt is not null;
        // Stage 2 conditional evidence: a confirmed failed delivery with no
        // product received (Failed, failure timestamp, never completed) is
        // exempt from the proof image requirement. Derived from the
        // authoritative delivery row — never from client input.
        DateTime? deadline = isFailedDeliveryFlow
            ? delivery.FailedAt!.Value.AddHours(windowHours)
            : delivery.CompletedAt?.AddHours(windowHours);

        // A delivered subscription occurrence supports the same
        // customer-raised post-delivery complaint as a one-time order: the
        // deadline anchors at completion, the frozen deduction snapshot prices
        // the refund, and staff settle it through the shared
        // approve/complete path. Failed subscription deliveries keep their
        // dedicated fail-ticket choice flow and stay out of this branch.
        var isSubscriptionPostDelivery =
            delivery.SubscriptionDeliveryId is not null
            && delivery.OrderId is null
            && delivery.Status == DeliveryStatus.Delivered
            && delivery.CompletedAt is not null;

        string? ineligibleReason = null;
        if (isSubscriptionPostDelivery)
        {
            if (hasActiveRequest)
            {
                ineligibleReason =
                    "An active refund or replacement request already exists for this delivery.";
            }
            else if (deadline is null || timeProvider.Now >= deadline.Value)
            {
                ineligibleReason =
                    $"The refund or replacement request window of {windowHours} hours has closed.";
            }
        }
        else if (delivery.SubscriptionDeliveryId is not null)
        {
            // Subscription-linked but not a delivered occurrence: failed ones
            // resolve through their fail-ticket choice flow, anything earlier
            // is not raisable yet.
            ineligibleReason = hasActiveRequest
                ? "An active refund or replacement request already exists for this delivery."
                : "Refund or replacement requests are only available after the delivery is completed.";
        }
        else if (delivery.SourceType != DeliverySourceType.OneTimeOrder || delivery.OrderId is null)
        {
            ineligibleReason =
                "Refund or replacement requests are only available for one-time order deliveries.";
        }
        else if (isMilkTestRejectedFlow is false &&
                 !isFailedDeliveryFlow &&
                 (delivery.Status != DeliveryStatus.Delivered || delivery.CompletedAt is null))
        {
            ineligibleReason =
                "Refund or replacement requests are only available after the delivery is completed.";
        }
        else if (hasActiveRequest)
        {
            ineligibleReason =
                "An active refund or replacement request already exists for this delivery.";
        }
        else if (!isMilkTestRejectedFlow && (deadline is null || timeProvider.Now >= deadline.Value))
        {
            ineligibleReason =
                $"The refund or replacement request window of {windowHours} hours has closed.";
        }
        else if (await IsOneTimeOrderFullyRefundedAsync(
            delivery.OrderId!.Value, delivery.CustomerId, cancellationToken))
        {
            // A completed refund is terminal for the order (completed
            // ticket, or verified settlement with nothing outstanding), so
            // the UI must explain instead of offering a second request.
            var refundedOrderNumber = await dbContext.Orders.AsNoTracking()
                .Where(x => x.Id == delivery.OrderId!.Value)
                .Select(x => x.OrderNumber)
                .SingleOrDefaultAsync(cancellationToken);
            ineligibleReason = AlreadyRefundedMessage(refundedOrderNumber);
        }

        var isFailedDeliveryWithoutProduct =
            !isMilkTestRejectedFlow
            && delivery.Status == DeliveryStatus.Failed
            && delivery.FailedAt is not null
            && delivery.CompletedAt is null;

        return new RefundReplacementEligibilityResult(
            IsEligible: ineligibleReason is null,
            IneligibleReason: ineligibleReason,
            WindowHours: windowHours,
            Deadline: deadline,
            HasActiveRequest: hasActiveRequest,
            ProofImageRequired: !isMilkTestRejectedFlow && !isFailedDeliveryWithoutProduct,
            IsMilkTestRejectedFlow: isMilkTestRejectedFlow,
            IsFailedDeliveryWithoutProduct: isFailedDeliveryWithoutProduct);
    }

    public async Task<RefundReplacementRequestResult> SubmitAsync(
        RefundReplacementActor actor,
        SubmitRefundReplacementRequest request,
        CancellationToken cancellationToken,
        SubmitRefundReplacementImage? image = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Reason, "reason", MaximumReasonLength, required: true);
        ValidateText(request.Remarks, "remarks", MaximumRemarksLength, required: false);

        // Stage 2.1: pre-validate accompanying evidence before the transaction
        // (no side effects). Ownership is resolved inside the transaction from
        // the actor and the owned delivery; never from this payload.
        var normalizedContentType = image is null
            ? null
            : ValidateImage(image.ContentType, image.FileSize);
        var safeFileName = image is null ? null : ValidateFileName(image.FileName);

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var delivery = await dbContext.Deliveries
                .SingleOrDefaultAsync(
                    x => x.PublicId == request.DeliveryId && x.CustomerId == actor.UserId,
                    cancellationToken) ?? throw new NotFoundException("The delivery was not found.");

            if (delivery.SourceType != DeliverySourceType.OneTimeOrder || delivery.OrderId is null)
            {
                // Delivered subscription occurrences raise post-delivery
                // complaints through their own branch below; every other
                // shape keeps the historical one-time-only refusal.
                result = await SubmitSubscriptionPostDeliveryAsync(
                    actor, delivery, request, image,
                    normalizedContentType, safeFileName,
                    cancellationToken);
                return;
            }

            // Stage 1: refund only for one-time orders. No replacement /
            // re-delivery workflow exists for failed one-time deliveries.
            if (request.Type == RefundReplacementType.Replacement)
            {
                throw new BusinessRuleException(
                    "Only refund requests are supported for one-time orders. Replacement requests cannot be raised.");
            }

            // Resolve the milk-test-rejection special flow. An explicit reference
            // must be a rejected test on this delivery; otherwise a rejected test
            // is linked automatically so Order/Delivery/MilkTest stay linked and the
            // proof image requirement is waived.
            MilkTest? rejectedMilkTest;
            if (request.MilkTestId is not null)
            {
                rejectedMilkTest = await dbContext.MilkTests
                    .SingleOrDefaultAsync(
                        x => x.PublicId == request.MilkTestId.Value
                            && x.DeliveryId == delivery.Id
                            && x.CustomerId == actor.UserId,
                        cancellationToken) ?? throw new NotFoundException("The doorstep test was not found.");

                if (rejectedMilkTest.CustomerDecision != MilkTestCustomerDecision.Rejected)
                {
                    throw new BusinessRuleException(
                        "The refund/replacement special flow requires a customer-rejected doorstep milk test.");
                }
            }
            else
            {
                rejectedMilkTest = await dbContext.MilkTests
                    .Where(x => x.DeliveryId == delivery.Id
                        && x.CustomerId == actor.UserId
                        && x.CustomerDecision == MilkTestCustomerDecision.Rejected)
                    .OrderByDescending(x => x.RejectedAt)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            var source = rejectedMilkTest is null
                ? RefundReplacementSource.CustomerPostDelivery
                : RefundReplacementSource.MilkTestRejected;
            var isMilkTestRejectedFlow = source == RefundReplacementSource.MilkTestRejected;

            if (isMilkTestRejectedFlow)
            {
                if (delivery.Status != DeliveryStatus.Failed)
                {
                    throw new BusinessRuleException(
                        "The milk-test rejection flow requires a failed delivery.");
                }
            }
            else if (delivery.Status == DeliveryStatus.Failed && delivery.FailedAt is null)
            {
                throw new BusinessRuleException(
                    "Refund or replacement requests are only available after the delivery is completed.");
            }
            else if (delivery.Status != DeliveryStatus.Failed
                && (delivery.Status != DeliveryStatus.Delivered || delivery.CompletedAt is null))
            {
                throw new BusinessRuleException(
                    "Refund or replacement requests are only available after the delivery is completed.");
            }

            // One active request per delivery. The unique filtered index backs this
            // rule; the check gives a clean 409 instead of a constraint failure.
            if (await dbContext.RefundReplacementRequests.AnyAsync(
                    x => x.DeliveryId == delivery.Id
                        && (x.Status == RefundReplacementStatus.Pending
                            || x.Status == RefundReplacementStatus.Approved),
                    cancellationToken))
            {
                throw new ConflictException(
                    "An active refund or replacement request already exists for this delivery.");
            }

            // Stage 2.1 submit-time evidence enforcement (authoritative): the
            // requirement is derived here from the owned delivery row and the
            // resolved source — never from client input. A ticket that requires
            // a photo cannot proceed as a normal submitted ticket without valid
            // accompanying evidence, closing the direct-API submit bypass. The
            // approval gate stays as defense in depth. Runs after the existing
            // eligibility/duplicate checks so those errors keep precedence.
            var isFailedDeliveryWithoutProduct =
                !isMilkTestRejectedFlow
                && delivery.Status == DeliveryStatus.Failed
                && delivery.FailedAt is not null
                && delivery.CompletedAt is null;
            var proofImageRequired =
                !isMilkTestRejectedFlow && !isFailedDeliveryWithoutProduct;
            if (proofImageRequired && image is null)
            {
                throw new BusinessRuleException(
                    "A proof image is required with your request. " +
                    "Attach a clear photo of the delivered product showing the issue.");
            }

            if (isMilkTestRejectedFlow && image is not null)
            {
                throw new BusinessRuleException(
                    "A proof image is not required for a request opened from a rejected doorstep milk test.");
            }

            var configuration = await configurationService.GetAsync(cancellationToken);
            var now = timeProvider.Now;

            DateTime? deadline = null;
            if (source == RefundReplacementSource.CustomerPostDelivery)
            {
                // Authoritative completion timestamp + configured window (failure
                // timestamp for a failed delivery). The window is
                // exclusive: at the deadline the request is refused, which also keeps the
                // stored deadline strictly greater than the submission timestamp.
                var anchor = delivery.Status == DeliveryStatus.Failed
                    ? delivery.FailedAt!.Value
                    : delivery.CompletedAt!.Value;
                var computed = anchor.AddHours(configuration.WindowHours);
                if (now >= computed)
                {
                    throw new BusinessRuleException(
                        $"The refund or replacement request window of {configuration.WindowHours} hours has closed.");
                }

                deadline = computed;
            }

            var order = await dbContext.Orders
                .SingleAsync(x => x.Id == delivery.OrderId!.Value, cancellationToken);
            var customer = await dbContext.Users
                .SingleAsync(x => x.Id == delivery.CustomerId, cancellationToken);
            var branch = await dbContext.Branches
                .SingleAsync(x => x.Id == delivery.BranchId, cancellationToken);

            // A completed refund is terminal for the order: a completed
            // ticket already paid the policy maximum, and verified
            // settlement rows with nothing outstanding mean the same. A new
            // request would price nothing (or over-refund the deduction
            // remainder), so refuse it here instead of opening a second
            // ticket. Orders with no settlement yet (unpaid/COD) stay
            // submittable; completion still guards them.
            if (await IsOneTimeOrderFullyRefundedAsync(order.Id, delivery.CustomerId, cancellationToken))
            {
                throw new ConflictException(AlreadyRefundedMessage(order.OrderNumber));
            }

            var entity = new RefundReplacementRequest(
                order.Id,
                delivery.Id,
                delivery.CustomerId,
                delivery.BranchId,
                rejectedMilkTest?.Id,
                request.Type,
                source,
                request.Reason,
                request.Remarks,
                now,
                deadline);

            // Allocated inside the business transaction so a rolled-back submission
            // also rolls back the counter.
            var requestNumber = await numberSeriesService.GetNextNumberAsync(
                SeriesCode,
                actor.UserId,
                cancellationToken);
            Mutate(() => entity.AssignRequestNumber(requestNumber));

            dbContext.RefundReplacementRequests.Add(entity);
            // Flush inside the transaction (no commit) so accompanying evidence
            // can reference the assigned ticket id. A later failure rolls
            // everything back and the stored blob is compensating-deleted below.
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Concurrent-submit backstop: the loser of a simultaneous
                // duplicate-submission race hits the active-ticket unique
                // index. Re-read authoritatively; a now-present active ticket
                // means this attempt loses as a conflict, never duplicates.
                dbContext.ChangeTracker.Clear();
                var activeExists = await dbContext.RefundReplacementRequests
                    .AsNoTracking()
                    .AnyAsync(
                        x => x.DeliveryId == delivery.Id
                            && (x.Status == RefundReplacementStatus.Pending
                                || x.Status == RefundReplacementStatus.Approved),
                        cancellationToken);
                if (activeExists)
                {
                    throw new ConflictException(
                        "An active refund or replacement request already exists for this delivery.");
                }

                throw;
            }

            if (image is not null)
            {
                var extension = ImageExtension(normalizedContentType!);
                var storageKey =
                    $"{now:yyyy/MM}/{entity.BranchId}/{entity.PublicId:N}/{Guid.NewGuid():N}{extension}";
                var stored = await mediaStorage.SaveAsync(
                    storageKey,
                    image.Content,
                    normalizedContentType!,
                    cancellationToken);
                try
                {
                    if (stored.FileSize != image.FileSize)
                    {
                        throw new InvalidOperationException(
                            "The stored media size does not match the uploaded image size.");
                    }

                    Mutate(() => entity.AddImage(new RefundReplacementImage(
                        entity.Id,
                        stored.StorageKey,
                        safeFileName!,
                        normalizedContentType!,
                        stored.FileSize,
                        actor.UserId,
                        now)));

                    AddAudit(
                        actor.UserId,
                        "REFUND_REPLACEMENT.IMAGE_UPLOAD",
                        entity.PublicId,
                        null,
                        new { FileName = safeFileName, ContentType = normalizedContentType, stored.FileSize },
                        null,
                        now);
                }
                catch
                {
                    // The database write failed — remove the orphaned blob.
                    await mediaStorage.DeleteIfExistsAsync(stored.StorageKey, CancellationToken.None);
                    throw;
                }
            }

            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.SUBMIT",
                entity.PublicId,
                null,
                new
                {
                    entity.RequestNumber,
                    entity.OrderId,
                    entity.DeliveryId,
                    Type = entity.Type.ToString(),
                    Source = entity.Source.ToString(),
                    Status = entity.Status.ToString(),
                    entity.Deadline
                },
                request.Reason,
                now);

            AddCustomerEvent(
                entity,
                NotificationEventTypes.RefundReplacementSubmitted,
                $"refund-replacement:{entity.PublicId:N}:submitted",
                "Your refund/replacement request has been submitted for review.",
                now);

            await dbContext.SaveChangesAsync(cancellationToken);
            result = ToResult(entity, order, delivery, customer, branch, rejectedMilkTest, null, null);
        }, cancellationToken);

        return result!;
    }

    /// <summary>
    /// Customer-raised post-delivery complaint for a delivered subscription
    /// occurrence (quality, contamination, visible damage). Runs inside the
    /// caller's serializable transaction. Economics mirror the failed flow:
    /// the live Admin Centre window and delivery-fail deduction percentage
    /// are frozen on the ticket; staff approve (optionally creating a
    /// replacement occurrence) and completion settles the frozen refund.
    /// Failed subscription deliveries never reach here — they resolve
    /// through their fail-ticket choice flow.
    /// </summary>
    private async Task<RefundReplacementRequestResult> SubmitSubscriptionPostDeliveryAsync(
        RefundReplacementActor actor,
        Delivery delivery,
        SubmitRefundReplacementRequest request,
        SubmitRefundReplacementImage? image,
        string? normalizedContentType,
        string? safeFileName,
        CancellationToken cancellationToken)
    {
        if (delivery.SubscriptionDeliveryId is null || delivery.OrderId is not null)
        {
            throw new BusinessRuleException(
                "Refund or replacement requests are only available for one-time order deliveries.");
        }
        if (delivery.Status != DeliveryStatus.Delivered || delivery.CompletedAt is null)
        {
            throw new BusinessRuleException(
                "Refund or replacement requests are only available after the delivery is completed.");
        }
        if (request.MilkTestId is not null)
        {
            throw new BusinessRuleException(
                "The milk-test rejection flow requires a failed delivery.");
        }
        var occurrence = await dbContext.SubscriptionDeliveries
            .SingleOrDefaultAsync(x => x.Id == delivery.SubscriptionDeliveryId.Value, cancellationToken)
            ?? throw new ConflictException("The delivery no longer belongs to a subscription.");
        // Consistent with failed re-deliveries: a replacement occurrence can
        // only ever be refunded, never replaced again.
        if (occurrence.IsReplacement && request.Type == RefundReplacementType.Replacement)
        {
            throw new BusinessRuleException(
                "Only a refund is available for a re-delivery replacement; it cannot be replaced again.");
        }
        if (await dbContext.RefundReplacementRequests.AnyAsync(
                x => x.DeliveryId == delivery.Id
                    && (x.Status == RefundReplacementStatus.Pending
                        || x.Status == RefundReplacementStatus.Approved),
                cancellationToken))
        {
            throw new ConflictException(
                "An active refund or replacement request already exists for this delivery.");
        }

        var configuration = await configurationService.GetAsync(cancellationToken);
        var now = timeProvider.Now;
        var deadline = delivery.CompletedAt.Value.AddHours(configuration.WindowHours);
        if (now >= deadline)
        {
            throw new BusinessRuleException(
                $"The refund or replacement request window of {configuration.WindowHours} hours has closed.");
        }
        // A delivered-product claim always carries a photo; the approval gate
        // re-checks as defense in depth.
        if (image is null)
        {
            throw new BusinessRuleException(
                "A proof image is required with your request. " +
                "Attach a clear photo of the delivered product showing the issue.");
        }

        var customer = await dbContext.Users
            .SingleAsync(x => x.Id == delivery.CustomerId, cancellationToken);
        var branch = await dbContext.Branches
            .SingleAsync(x => x.Id == delivery.BranchId, cancellationToken);

        var entity = new RefundReplacementRequest(
            null,
            delivery.Id,
            delivery.CustomerId,
            delivery.BranchId,
            null,
            request.Type,
            RefundReplacementSource.CustomerPostDelivery,
            request.Reason,
            request.Remarks,
            now,
            deadline,
            delivery.SubscriptionDeliveryId.Value,
            null,
            configuration.WindowHours,
            configuration.DeliveryFailDeductionPercentage);

        var requestNumber = await numberSeriesService.GetNextNumberAsync(
            SeriesCode,
            actor.UserId,
            cancellationToken);
        Mutate(() => entity.AssignRequestNumber(requestNumber));

        dbContext.RefundReplacementRequests.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        var extension = ImageExtension(normalizedContentType!);
        var storageKey =
            $"{now:yyyy/MM}/{entity.BranchId}/{entity.PublicId:N}/{Guid.NewGuid():N}{extension}";
        var stored = await mediaStorage.SaveAsync(
            storageKey,
            image.Content,
            normalizedContentType!,
            cancellationToken);
        try
        {
            if (stored.FileSize != image.FileSize)
            {
                throw new InvalidOperationException(
                    "The stored media size does not match the uploaded image size.");
            }

            Mutate(() => entity.AddImage(new RefundReplacementImage(
                entity.Id,
                stored.StorageKey,
                safeFileName!,
                normalizedContentType!,
                stored.FileSize,
                actor.UserId,
                now)));

            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.IMAGE_UPLOAD",
                entity.PublicId,
                null,
                new { FileName = safeFileName, ContentType = normalizedContentType, stored.FileSize },
                null,
                now);
        }
        catch
        {
            await mediaStorage.DeleteIfExistsAsync(stored.StorageKey, CancellationToken.None);
            throw;
        }

        AddAudit(
            actor.UserId,
            "REFUND_REPLACEMENT.SUBMIT",
            entity.PublicId,
            null,
            new
            {
                entity.RequestNumber,
                entity.SubscriptionDeliveryId,
                entity.DeliveryId,
                Type = entity.Type.ToString(),
                Source = entity.Source.ToString(),
                Status = entity.Status.ToString(),
                entity.Deadline
            },
            request.Reason,
            now);

        AddCustomerEvent(
            entity,
            NotificationEventTypes.RefundReplacementSubmitted,
            $"refund-replacement:{entity.PublicId:N}:submitted",
            "Your refund/replacement request has been submitted for review.",
            now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity, null, delivery, customer, branch, null, null, null);
    }

    public async Task<IReadOnlyList<RefundReplacementRequestResult>> ListForCustomerAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken)
    {
        var requests = await BaseQuery(asNoTracking: true)
            .Where(x => x.CustomerId == actor.UserId)
            .OrderByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        // D1-D slice 6.1: same persisted-replacement enrichment as the staff
        // reads, in one batched lookup.
        var replacements = await LoadPersistedReplacementsAsync(
            requests.Select(x => x.Id).ToArray(), cancellationToken);
        return requests
            .Select(x => ToResult(
                x,
                x.Order,
                x.Delivery,
                x.Customer,
                x.Branch,
                x.MilkTest,
                x.DecidedByUser,
                x.CompletedByUser,
                replacements.TryGetValue(x.Id, out var replacement) ? replacement : null))
            .ToArray();
    }

    public async Task<RefundReplacementRequestResult?> GetForCustomerAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var request = await BaseQuery(asNoTracking: true)
            .SingleOrDefaultAsync(
                x => x.PublicId == requestId && x.CustomerId == actor.UserId,
                cancellationToken);

        if (request is null)
        {
            return null;
        }

        // D1-D slice 6.1: the persisted replacement (if any) is resolved
        // through the stored occurrence link, never from client input.
        var replacements = await LoadPersistedReplacementsAsync(
            [request.Id], cancellationToken);
        replacements.TryGetValue(request.Id, out var replacement);
        return ToResult(
            request,
            request.Order,
            request.Delivery,
            request.Customer,
            request.Branch,
            request.MilkTest,
            request.DecidedByUser,
            request.CompletedByUser,
            replacement);
    }

    public async Task<RefundReplacementPageResult> ListForStaffAsync(
        RefundReplacementActor actor,
        RefundReplacementListQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? DefaultPageSize : Math.Min(query.PageSize, MaximumPageSize);

        IQueryable<RefundReplacementRequest> requests =
            dbContext.RefundReplacementRequests.AsNoTracking();

        if (actor.HasGlobalAccess)
        {
            if (query.BranchId.HasValue)
            {
                requests = requests.Where(x => x.BranchId == query.BranchId.Value);
            }
        }
        else
        {
            var branchIds = actor.BranchIds.ToArray();
            if (query.BranchId.HasValue)
            {
                if (!branchIds.Contains(query.BranchId.Value))
                {
                    return new RefundReplacementPageResult([], page, pageSize, 0);
                }

                requests = requests.Where(x => x.BranchId == query.BranchId.Value);
            }
            else
            {
                requests = requests.Where(x => branchIds.Contains(x.BranchId));
            }
        }

        if (query.Status.HasValue)
        {
            requests = requests.Where(x => x.Status == query.Status.Value);
        }

        var totalCount = await requests.CountAsync(cancellationToken);

        var items = await requests
            .Include(x => x.Order)
            .Include(x => x.SubscriptionDelivery)
            .Include(x => x.Delivery)
            .Include(x => x.Customer)
            .Include(x => x.Branch)
            .Include(x => x.MilkTest)
            .Include(x => x.Images)
            .Include(x => x.SupersedesRequest)
            .OrderByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // D1-D slice 6.1: approved re-delivery rows carry their persisted
        // replacement date on the list. One batched read-only lookup; tickets
        // without a replacement keep a null date, exactly as before.
        var replacements = await LoadPersistedReplacementsAsync(
            items.Select(x => x.Id).ToArray(), cancellationToken);
        return new RefundReplacementPageResult(
            items.Select(x => ToListItem(
                x,
                replacements.TryGetValue(x.Id, out var replacement)
                    ? replacement.ScheduledDate
                    : null)).ToArray(),
            page,
            pageSize,
            totalCount);
    }

    public async Task<RefundReplacementRequestResult> GetForStaffAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var request = await BaseQuery(asNoTracking: true)
            .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
            ?? throw new NotFoundException("The refund/replacement request was not found.");

        EnsureStaffAccess(actor, request);

        var replacements = await LoadPersistedReplacementsAsync(
            [request.Id], cancellationToken);
        replacements.TryGetValue(request.Id, out var replacement);
        return ToResult(
            request,
            request.Order,
            request.Delivery,
            request.Customer,
            request.Branch,
            request.MilkTest,
            request.DecidedByUser,
            request.CompletedByUser,
            replacement);
    }

    /// <summary>
    /// D1-B: records the owning customer's explicit remedy choice. Same-choice
    /// retries return the current state with no side effects; a different choice
    /// after resolution, a non-awaiting ticket, or an expired window is
    /// rejected. The row-version token keeps this transition single-winner
    /// against concurrent resolutions (including the future timeout processor).
    /// </summary>
    public async Task<RefundReplacementRequestResult> ChooseAsync(
        RefundReplacementActor actor,
        Guid requestId,
        ChooseRefundReplacementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            // Retry safety: the SQL Server execution strategy may re-run this
            // operation after a transient failure. Discard any half-mutated
            // tracked state so every attempt reads authoritative state.
            dbContext.ChangeTracker.Clear();
            // Ownership is enforced in the predicate: another customer's ticket
            // is indistinguishable from a missing one.
            var entity = await BaseQuery()
                .SingleOrDefaultAsync(
                    x => x.PublicId == requestId && x.CustomerId == actor.UserId,
                    cancellationToken) ?? throw new NotFoundException("The refund/replacement request was not found.");

            if (entity.CustomerChoice == request.Choice)
            {
                result = ToResult(
                    entity,
                    entity.Order,
                    entity.Delivery,
                    entity.Customer,
                    entity.Branch,
                    entity.MilkTest,
                    entity.DecidedByUser,
                    entity.CompletedByUser);
                return;
            }

            if (entity.Status != RefundReplacementStatus.AwaitingCustomerChoice)
            {
                throw new BusinessRuleException("The ticket is not awaiting customer choice.");
            }

            if (entity.CustomerChoice.HasValue)
            {
                throw new ConflictException(
                    $"A different choice ({entity.CustomerChoice}) has already been recorded and cannot be changed.");
            }

            var now = timeProvider.Now;
            if (entity.Deadline is null || now >= entity.Deadline.Value)
            {
                throw new BusinessRuleException("The customer choice window has expired.");
            }

            // D1-D slice 4: a failed replacement is refund-only. The failed
            // occurrence is identified through the ticket's persisted source
            // reference, so this covers every failure path — customer fault
            // and milk-test rejection converge on the same ticket shape.
            if (request.Choice == CustomerChoice.ReDelivery
                && entity.SubscriptionDeliveryId.HasValue
                && await IsReplacementOccurrenceAsync(
                    entity.SubscriptionDeliveryId.Value, cancellationToken))
            {
                throw new BusinessRuleException(
                    "Only a refund is available for a failed re-delivery replacement; it cannot be replaced again.");
            }

            var previousType = entity.Type;
            Mutate(() => entity.RecordCustomerChoice(request.Choice, now));

            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.CUSTOMER_CHOICE",
                entity.PublicId,
                new
                {
                    entity.RequestNumber,
                    Status = RefundReplacementStatus.AwaitingCustomerChoice.ToString(),
                    CustomerChoice = (string?)null,
                    Type = previousType.ToString()
                },
                new
                {
                    entity.RequestNumber,
                    Status = entity.Status.ToString(),
                    CustomerChoice = entity.CustomerChoice.ToString(),
                    entity.CustomerChoiceAt,
                    Type = entity.Type.ToString()
                },
                $"Customer chose {request.Choice}.",
                now);

            await AddStaffChoiceEventsAsync(entity, request.Choice, now, cancellationToken);

            await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
            result = ToResult(
                entity,
                entity.Order,
                entity.Delivery,
                entity.Customer,
                entity.Branch,
                entity.MilkTest,
                entity.DecidedByUser,
                entity.CompletedByUser);
        }, cancellationToken);

        return result!;
    }

    public Task<RefundReplacementRequestResult> ApproveAsync(
        RefundReplacementActor actor,
        Guid requestId,
        ApproveRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        DecideAsync(actor, requestId, request, approve: true, cancellationToken);

    /// <summary>
    /// D1-D: advisory proposal of the next eligible replacement date. Approval
    /// re-validates (or validates a staff-edited date) inside the authoritative
    /// serializable transaction, so a stale proposal can never create a
    /// conflicting or ineligible occurrence.
    /// </summary>
    public async Task<ReplacementDateProposalResult> ProposeReplacementDateAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var entity = await BaseQuery(asNoTracking: true)
            .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
            ?? throw new NotFoundException("The refund/replacement request was not found.");

        EnsureStaffAccess(actor, entity);
        EnsureRedeliveryApprovable(entity, entity.Status);

        var subscription = await LoadSubscriptionForReplacementAsync(
            entity.SubscriptionDeliveryId!.Value, asNoTracking: true, cancellationToken);
        var proposed = ProposeReplacementDate(
            subscription, timeProvider.Now, await GetReplacementCutoffAsync(cancellationToken));
        return new ReplacementDateProposalResult(entity.PublicId, proposed);
    }

    public Task<RefundReplacementRequestResult> RejectAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        DecideAsync(actor, requestId, request, approve: false, cancellationToken);

    public async Task<RefundReplacementRequestResult> CompleteAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideRefundReplacementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Remarks, "remarks", MaximumRemarksLength, required: false);

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var entity = await BaseQuery()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
                ?? throw new NotFoundException("The refund/replacement request was not found.");            EnsureStaffAccess(actor, entity);

            // Employee-origin policy: an employee-originated ticket may only
            // settle here when a verifiable administrator approval is
            // recorded; customer tickets keep the existing workflow.
            await EmployeeOriginGuard.RequireVerifiableAdminApprovalAsync(
                dbContext, entity.CustomerId, entity.DecidedByUserId, entity.RequestNumber, "completed", cancellationToken);

            // D1-D slice 3 (Rule 8): a Re-Delivery ticket is completed only by
            // its replacement delivery (same-transaction hook in the delivery
            // completion path). Manual completion is rejected until the
            // linked replacement has actually been delivered; when it already
            // has (e.g. rows delivered before that hook existed), manual
            // completion remains available as the recovery path. A
            // post-delivery complaint approved as Replacement follows the
            // same rule: the remedy is the replacement delivery, not the
            // approval itself.
            var isRedeliveryRemedy =
                (entity.Source == RefundReplacementSource.DeliveryFailed
                    && entity.CustomerChoice == CustomerChoice.ReDelivery)
                || (entity.Source == RefundReplacementSource.CustomerPostDelivery
                    && entity.CustomerChoice is null
                    && entity.Type == RefundReplacementType.Replacement);
            if (isRedeliveryRemedy && entity.SubscriptionDeliveryId is not null)
            {
                var replacementDelivered = await dbContext.SubscriptionDeliveries.AsNoTracking()
                    .AnyAsync(
                        x => x.IsReplacement
                            && x.RefundReplacementRequestId == entity.Id
                            && x.Status == SubscriptionDeliveryStatus.Delivered,
                        cancellationToken);
                if (!replacementDelivered)
                {
                    throw new BusinessRuleException(
                        "The re-delivery ticket cannot be completed before its replacement delivery is delivered.");
                }
            }

            // Stage 2: a manual-bank ticket never settles here. Staff
            // completion would credit the wallet and record local refunds;
            // the transfer is confirmed through the manual-bank confirmation
            // only after the real bank transfer is made.
            if (entity.SettlementMethod == RefundSettlementMethod.ManualBankTransfer)
            {
                throw new BusinessRuleException(
                    $"Ticket {entity.RequestNumber} is designated for manual bank transfer and cannot be completed here. Confirm the bank transfer through the manual bank transfer confirmation once the transfer is made.");
            }

            var now = timeProvider.Now;
            var previousStatus = entity.Status;
            // Lifecycle first: a non-Approved ticket fails here with the
            // domain status error, never with the settlement error below.
            Mutate(() => entity.Complete(actor.UserId, now, request.Remarks));

            // Stage 1: a legacy order-backed Replacement ticket can never be
            // completed — no re-delivery workflow exists for one-time orders.
            // History is preserved; resolve manually (reject to close it
            // without delivery).
            if (entity.OrderId.HasValue && entity.Type == RefundReplacementType.Replacement)
            {
                throw new BusinessRuleException(
                    $"Only refunds are supported for one-time orders. Replacement ticket {entity.RequestNumber} " +
                    "cannot be completed; resolve it manually with staff support.");
            }

            // Stage 1: an approved order-backed Refund ticket settles here, at
            // the authoritative completion point — never on approval. The
            // frozen deduction decision, order total and eligible payments are
            // revalidated inside this serializable transaction; the wallet
            // credit, per-payment refund rows, ticket transition, audit and
            // outbox commit atomically. An infeasible refund throws before
            // SaveChanges, so the ticket stays Approved for repair and retry
            // — never half-completed. A ticket already made whole through an
            // external Payments settlement completes without new money
            // movement (see ExecuteOneTimeOrderRefundAsync).
            OneTimeOrderRefundOutcome? orderRefund = null;
            if (entity.OrderId.HasValue && entity.Type == RefundReplacementType.Refund)
            {
                orderRefund = await ExecuteOneTimeOrderRefundAsync(
                    entity, actor.UserId, now, cancellationToken);
            }

            // D1-D slice 5.2: a refund-only remedy for a failed replacement
            // carries its own money. Completing it executes the frozen D1-C
            // refund (same formula, same delivery-fail: idempotency key) and
            // resolves the original Approved ReDelivery ticket to the explicit
            // terminal Failed outcome — all inside this serializable
            // transaction, so the pair commits atomically. Any other ticket
            // shape (first failures, ReDelivery choices, order-backed
            // replacements) completes exactly as before: no money moves,
            // nothing resolves. Order-backed Refunds are gated above and never
            // reach this point without a verified Payments settlement.
            // An infeasible refund (no snapshot, no compatible payment, fully
            // refunded payment) throws before SaveChanges, so the completion
            // itself rolls back and both tickets stay Approved for repair and
            // retry — never half-completed.
            var staffRefund = await ExecuteSecondFailureRefundAsync(
                entity, actor.UserId, now, cancellationToken);
            if (staffRefund is not null)
            {
                // Best-effort like the timeout path: a missing link or an
                // already-resolved original never fails the refund itself.
                // This service has no logger; the COMPLETE audit below plus
                // the resolution audit/event record whatever happened.
                await FailedRemedyResolution.TryResolveOriginalAsync(
                    dbContext,
                    notificationEventWriter,
                    entity,
                    staffRefund.WalletCredit,
                    staffRefund.DeliveryNumber,
                    actor.UserId,
                    now,
                    cancellationToken);
            }

            var completer = await dbContext.Users
                .AsNoTracking()
                .SingleAsync(x => x.Id == actor.UserId, cancellationToken);
            var decider = entity.DecidedByUserId is null
                ? null
                : await dbContext.Users
                    .AsNoTracking()
                    .SingleAsync(x => x.Id == entity.DecidedByUserId.Value, cancellationToken);

            // Audit remediation A: an explicitly Refund-chosen first failure
            // pays here, at the authoritative completion point — never on
            // choice or approval. Order-backed Refunds never pay here: the
            // guard above requires a pre-existing Payments settlement and no
            // amount is invented (reported gap remains a business decision).
            var firstFailureRefund = await ExecuteFirstFailureRefundAsync(
                entity, actor.UserId, now, cancellationToken);

            object completeNewValue = (staffRefund, firstFailureRefund, orderRefund) switch
            {
                (not null, _, _) => new
                {
                    Status = entity.Status.ToString(),
                    entity.CompletedAt,
                    Resolution = "SecondFailureStaffRefund",
                    staffRefund.DeliveryNumber,
                    staffRefund.GrossRefund,
                    staffRefund.DeductionPercentage,
                    staffRefund.DeductionAmount,
                    staffRefund.WalletCredit,
                    staffRefund.SubscriptionPayable,
                    staffRefund.TotalEntitlement
                },
                (_, not null, _) => new
                {
                    Status = entity.Status.ToString(),
                    entity.CompletedAt,
                    Resolution = "FirstFailureStaffRefund",
                    firstFailureRefund.DeliveryNumber,
                    firstFailureRefund.GrossRefund,
                    firstFailureRefund.DeductionPercentage,
                    firstFailureRefund.DeductionAmount,
                    firstFailureRefund.WalletCredit,
                    firstFailureRefund.SubscriptionPayable,
                    firstFailureRefund.TotalEntitlement
                },
                (_, _, not null) => new
                {
                    Status = entity.Status.ToString(),
                    entity.CompletedAt,
                    Resolution = "OneTimeOrderRefund",
                    orderRefund.OrderNumber,
                    orderRefund.GrossBasis,
                    orderRefund.AppliedPercentage,
                    orderRefund.DeductionAmount,
                    orderRefund.EligiblePaid,
                    WalletCredit = orderRefund.WalletCredit,
                },
                _ => new { Status = entity.Status.ToString(), entity.CompletedAt }
            };
            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.COMPLETE",
                entity.PublicId,
                new { Status = previousStatus.ToString() },
                completeNewValue,
                request.Remarks,
                now);

            // Stage 1: the success notification names the committed wallet
            // credit. It is buffered in the same transaction, so a rolled-back
            // settlement never notifies.
            AddCustomerEvent(
                entity,
                NotificationEventTypes.RefundReplacementCompleted,
                $"refund-replacement:{entity.PublicId:N}:completed",
                orderRefund is not null
                    ? $"Your refund of {orderRefund.WalletCredit:0.00} has been credited to your wallet for order {orderRefund.OrderNumber}."
                    : "Your refund/replacement request has been completed.",
                now);

            await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
            // D1-D slice 6.1: a completed re-delivery ticket reports the same
            // persisted replacement as the detail reads (read-only lookup;
            // refund and other shapes still resolve to null).
            var completedReplacements = await LoadPersistedReplacementsAsync(
                [entity.Id], cancellationToken);
            completedReplacements.TryGetValue(entity.Id, out var completedReplacement);
            result = ToResult(
                entity,
                entity.Order,
                entity.Delivery,
                entity.Customer,
                entity.Branch,
                entity.MilkTest,
                decider,
                completer,
                completedReplacement);
        }, cancellationToken);

        return result!;
    }

    public async Task<RefundReplacementRequestResult> ConfirmManualBankTransferAsync(
        RefundReplacementActor actor,
        Guid requestId,
        ConfirmManualBankTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Remarks, "remarks", MaximumRemarksLength, required: false);
        var transferReference = request.TransferReference?.Trim() ?? string.Empty;
        if (transferReference.Length < 6 || transferReference.Length > 100)
        {
            throw new ValidationAppException(
                "A bank transfer reference (UTR) between 6 and 100 characters is required to confirm a manual bank transfer.",
                "transferReference");
        }

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var entity = await BaseQuery()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
                ?? throw new NotFoundException("The refund/replacement request was not found.");

            EnsureStaffAccess(actor, entity);

            if (entity.SettlementMethod != RefundSettlementMethod.ManualBankTransfer)
            {
                throw new ConflictException(
                    $"Ticket {entity.RequestNumber} is not designated for manual bank transfer.");
            }

            if (entity.Status == RefundReplacementStatus.Completed)
            {
                result = await LoadConfirmedResultAsync(entity.Id, cancellationToken);
                return;
            }

            // Employee-origin policy: confirming settles money, so an
            // employee-originated ticket requires a verifiable administrator
            // approval; the authorized finance role may then confirm. The
            // completed replay above stays idempotent by design.
            await EmployeeOriginGuard.RequireVerifiableAdminApprovalAsync(
                dbContext, entity.CustomerId, entity.DecidedByUserId, entity.RequestNumber, "confirmed", cancellationToken);

            if (entity.Status != RefundReplacementStatus.Approved)
            {
                throw new ConflictException(
                    $"Ticket {entity.RequestNumber} cannot be confirmed: only an approved manual bank transfer can be confirmed.");
            }

            var reservations = await ManualBankTransferReservation.FindTicketReservationsAsync(
                dbContext, entity.PublicId, cancellationToken);
            if (reservations.Count == 0)
            {
                throw new ConflictException(
                    $"Ticket {entity.RequestNumber} cannot be confirmed: no pending manual-bank reservation exists for it. Resolve it with staff support first.");
            }

            if (reservations.Any(x => x.Status != RefundStatus.Pending))
            {
                throw new ConflictException(
                    $"Ticket {entity.RequestNumber} cannot be confirmed: its manual-bank reservation is no longer pending. Resolve it with staff support first.");
            }

            // Stage 3: a ticket cannot be marked paid without the required
            // linked transfer record. Only a generated or submitted export
            // item authorizes settlement; generation/download never settle.
            // Checked inside this transaction so a concurrent void aborts
            // before any reservation settles. The rule itself lives on
            // ManualBankTransferReservation so it has one owner.
            await ManualBankTransferReservation.RequireExportItemLinkageAsync(
                dbContext, entity.Id, entity.RequestNumber, cancellationToken);

            var now = timeProvider.Now;
            foreach (var reservation in reservations)
            {
                ManualBankTransferReservation.Settle(reservation, now);
            }

            Mutate(() => entity.Complete(actor.UserId, now, request.Remarks));

            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.MANUAL_BANK_CONFIRMED",
                entity.PublicId,
                new { Status = RefundReplacementStatus.Approved.ToString() },
                new
                {
                    Status = entity.Status.ToString(),
                    entity.CompletedAt,
                    entity.ManualBankReservedAmount,
                    entity.ManualBankReservedCurrency,
                    MaskedDestination = $"{entity.ManualBankNameSnapshot} \u00b7\u00b7\u00b7\u00b7 {entity.ManualBankAccountLast4Snapshot}",
                    TransferReference = transferReference
                },
                request.Remarks,
                now);
            AddCustomerEvent(
                entity,
                NotificationEventTypes.RefundReplacementCompleted,
                $"refund-replacement:{entity.PublicId:N}:manual-bank-confirmed",
                $"Your refund of {entity.ManualBankReservedAmount:0.00} {entity.ManualBankReservedCurrency} has been paid to your bank account for ticket {entity.RequestNumber}.",
                now);

            await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
            result = await LoadConfirmedResultAsync(entity.Id, cancellationToken);
        }, cancellationToken);

        return result!;
    }

    public async Task<RefundReplacementRequestResult> ReleaseManualBankTransferAsync(
        RefundReplacementActor actor,
        Guid requestId,
        ReleaseManualBankTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Reason, "reason", MaximumRemarksLength, required: true);
        ValidateText(request.Remarks, "remarks", MaximumRemarksLength, required: false);

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var entity = await BaseQuery()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
                ?? throw new NotFoundException("The refund/replacement request was not found.");

            EnsureStaffAccess(actor, entity);

            if (entity.SettlementMethod != RefundSettlementMethod.ManualBankTransfer)
            {
                throw new ConflictException(
                    $"Ticket {entity.RequestNumber} is not designated for manual bank transfer.");
            }

            // Employee-origin policy: releasing moves the ticket to terminal
            // Rejected, which is a disposition decision — only an Owner or
            // System Administrator (never the requester) may release an
            // employee-originated ticket.
            await EmployeeOriginGuard.RequireDecisionAuthorityAsync(
                dbContext, entity.CustomerId, actor.UserId, "released", cancellationToken);

            if (entity.Status != RefundReplacementStatus.Approved)
            {
                throw new ConflictException(
                    $"Ticket {entity.RequestNumber} cannot be released: only an approved manual bank transfer can be released.");
            }

            var reservations = await ManualBankTransferReservation.FindTicketReservationsAsync(
                dbContext, entity.PublicId, cancellationToken);
            if (reservations.Count == 0 || reservations.Any(x => x.Status != RefundStatus.Pending))
            {
                throw new ConflictException(
                    $"Ticket {entity.RequestNumber} cannot be released: its manual-bank reservation is not pending. Resolve it with staff support first.");
            }

            var now = timeProvider.Now;
            var voidReason = request.Reason!.Trim();
            foreach (var reservation in reservations)
            {
                ManualBankTransferReservation.Release(reservation, "Manual bank transfer released before payment.", now);
            }

            Mutate(() => entity.VoidManualBankTransfer(actor.UserId, now, voidReason));

            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.MANUAL_BANK_RELEASED",
                entity.PublicId,
                new { Status = RefundReplacementStatus.Approved.ToString() },
                new
                {
                    Status = entity.Status.ToString(),
                    entity.ManualBankReservedAmount,
                    entity.ManualBankReservedCurrency,
                    MaskedDestination = $"{entity.ManualBankNameSnapshot} \u00b7\u00b7\u00b7\u00b7 {entity.ManualBankAccountLast4Snapshot}",
                    ManualBankVoidReason = entity.ManualBankVoidReason
                },
                request.Remarks,
                now);
            AddCustomerEvent(
                entity,
                NotificationEventTypes.RefundReplacementDecided,
                $"refund-replacement:{entity.PublicId:N}:manual-bank-released",
                $"Your manual bank transfer request {entity.RequestNumber} was closed without payment. Contact support if you need further help.",
                now);

            await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
            result = await LoadConfirmedResultAsync(entity.Id, cancellationToken);
        }, cancellationToken);

        return result!;
    }

    private async Task<RefundReplacementRequestResult> LoadConfirmedResultAsync(
        long entityId,
        CancellationToken cancellationToken)
    {
        var entity = await BaseQuery()
            .SingleAsync(x => x.Id == entityId, cancellationToken);
        var decider = entity.DecidedByUserId is null
            ? null
            : await dbContext.Users
                .AsNoTracking()
                .SingleAsync(x => x.Id == entity.DecidedByUserId.Value, cancellationToken);
        var completer = entity.CompletedByUserId is null
            ? null
            : await dbContext.Users
                .AsNoTracking()
                .SingleAsync(x => x.Id == entity.CompletedByUserId.Value, cancellationToken);
        var replacements = await LoadPersistedReplacementsAsync([entity.Id], cancellationToken);
        replacements.TryGetValue(entity.Id, out var replacement);
        return ToResult(
            entity,
            entity.Order,
            entity.Delivery,
            entity.Customer,
            entity.Branch,
            entity.MilkTest,
            decider,
            completer,
            replacement);
    }

    /// <summary>
    /// Legacy remediation: atomically supersedes an approved, order-backed
    /// Replacement ticket for a one-time order and creates exactly one linked
    /// Pending Refund ticket for the same order, delivery, customer and
    /// branch inside one serializable transaction. Requires
    /// <c>REFUND_REPLACEMENT.MANAGE_BRANCH</c> branch scope via
    /// <see cref="EnsureStaffAccess"/>.
    ///
    /// The legacy ticket keeps its type, source, approval identity/timestamp,
    /// evidence and audit history; only its status moves to the explicit
    /// terminal <c>Superseded</c> outcome (no fulfillment or refund implied).
    /// The supersede is persisted before the linked refund row is inserted so
    /// the filtered active-ticket unique index on the delivery is respected.
    ///
    /// Aborts without mutation when the legacy state changed, another active
    /// ticket exists, or prior settlement records make a second refund
    /// unsafe. Issues no wallet credit and creates no financial refund row;
    /// the new ticket follows the standard apply/waive approval and wallet
    /// settlement workflow. Idempotent: a retry returns the existing pair.
    /// </summary>
    public async Task<RemediateLegacyReplacementResult> RemediateLegacyReplacementAsync(
        RefundReplacementActor actor,
        Guid requestId,
        RemediateLegacyReplacementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Reason, "reason", MaximumRemarksLength, required: true);

        RemediateLegacyReplacementResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var legacy = await BaseQuery()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
                ?? throw new NotFoundException("The refund/replacement request was not found.");

            EnsureStaffAccess(actor, legacy);

            // Employee-origin policy: superseding the legacy ticket is a
            // disposition decision — only an Owner or System Administrator
            // (never the requester) may remediate an employee-originated one.
            await EmployeeOriginGuard.RequireDecisionAuthorityAsync(
                dbContext, legacy.CustomerId, actor.UserId, "remediated", cancellationToken);

            // Idempotent retry: the pair already exists — return it without
            // side effects (no duplicate ticket, audit or notification).
            var existingRefund = await BaseQuery(asNoTracking: true)
                .SingleOrDefaultAsync(x => x.SupersedesRequestId == legacy.Id, cancellationToken);
            if (existingRefund is not null)
            {
                if (legacy.Status != RefundReplacementStatus.Superseded)
                {
                    throw new ConflictException(
                        $"Ticket {legacy.RequestNumber} has a linked refund ticket but is not superseded. Resolve it with staff support first.");
                }

                result = new RemediateLegacyReplacementResult(
                    ToResult(
                        legacy,
                        legacy.Order,
                        legacy.Delivery,
                        legacy.Customer,
                        legacy.Branch,
                        legacy.MilkTest,
                        legacy.DecidedByUser,
                        legacy.CompletedByUser),
                    ToResult(
                        existingRefund,
                        existingRefund.Order,
                        existingRefund.Delivery,
                        existingRefund.Customer,
                        existingRefund.Branch,
                        existingRefund.MilkTest,
                        existingRefund.DecidedByUser,
                        existingRefund.CompletedByUser));
                return;
            }

            if (legacy.Status != RefundReplacementStatus.Approved)
            {
                throw new ConflictException(
                    $"Ticket {legacy.RequestNumber} is {legacy.Status} and can no longer be remediated. Resolve it with staff support first.");
            }

            if (!legacy.OrderId.HasValue
                || legacy.SubscriptionDeliveryId.HasValue
                || legacy.Type != RefundReplacementType.Replacement)
            {
                throw new ConflictException(
                    $"Ticket {legacy.RequestNumber} is not an order-backed replacement ticket. Resolve it with staff support first.");
            }

            var order = legacy.Order
                ?? throw new ConflictException(
                    $"Ticket {legacy.RequestNumber} cannot be remediated: its order is missing. Resolve it with staff support first.");
            var delivery = legacy.Delivery;
            if (delivery.Status != DeliveryStatus.Failed)
            {
                throw new ConflictException(
                    $"Ticket {legacy.RequestNumber} cannot be remediated because delivery {delivery.DeliveryNumber ?? delivery.ReferenceNumber} is {delivery.Status}. Resolve it with staff support first.");
            }

            if (await dbContext.RefundReplacementRequests.AnyAsync(
                    x => x.DeliveryId == legacy.DeliveryId
                        && x.Id != legacy.Id
                        && (x.Status == RefundReplacementStatus.Pending
                            || x.Status == RefundReplacementStatus.Approved),
                    cancellationToken))
            {
                throw new ConflictException(
                    $"Another active refund or replacement request already exists for delivery {delivery.DeliveryNumber ?? delivery.ReferenceNumber}.");
            }

            // Settlement safety: a succeeded financial refund already recorded
            // against this order's payments cannot be attributed to this
            // failed delivery's obligation, so remediation stops for manual
            // review instead of risking a second refund.
            var priorSettled = await dbContext.Refunds.AsNoTracking()
                .Where(r => r.Status == RefundStatus.Succeeded
                    && r.Payment.OrderId == legacy.OrderId.Value
                    && r.Payment.CustomerId == legacy.CustomerId)
                .SumAsync(r => (decimal?)r.Amount, cancellationToken) ?? 0m;
            if (priorSettled > 0)
            {
                throw new ConflictException(
                    $"Ticket {legacy.RequestNumber} cannot be remediated automatically: a prior refund settlement of {priorSettled:0.00} is already recorded for order {order.OrderNumber}. Resolve it with staff support first.");
            }

            var now = timeProvider.Now;
            var remediationReason = request.Reason!.Trim();
            Mutate(() => legacy.MarkSuperseded(actor.UserId, now, remediationReason));
            // Persist the supersede first: the filtered unique index frees the
            // delivery before the linked refund row is inserted below.
            await SaveChangesWithConcurrencyGuardAsync(cancellationToken);

            // Informational only: order tickets never time out. The
            // TimestampOrder CHECK requires a deadline after submission, so a
            // staff-remediation ticket carries the configured window forward
            // from the remediation time instead of a long-closed window.
            var configuration = await configurationService.GetAsync(cancellationToken);
            DateTime? deadline = legacy.Source == RefundReplacementSource.MilkTestRejected
                ? null
                : now.AddHours(configuration.WindowHours);
            var refund = new RefundReplacementRequest(
                legacy.OrderId.Value,
                legacy.DeliveryId,
                legacy.CustomerId,
                legacy.BranchId,
                legacy.MilkTestId,
                RefundReplacementType.Refund,
                legacy.Source,
                $"Remediation for superseded ticket {legacy.RequestNumber}: refund-only remedy for a failed one-time order delivery.",
                $"Supersedes legacy ticket {legacy.RequestNumber}.",
                now,
                deadline);
            Mutate(() => refund.RecordRemediationLink(legacy.Id));
            var refundNumber = await numberSeriesService.GetNextNumberAsync(
                SeriesCode,
                actor.UserId,
                cancellationToken);
            Mutate(() => refund.AssignRequestNumber(refundNumber));
            dbContext.RefundReplacementRequests.Add(refund);

            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.REMEDIATE",
                legacy.PublicId,
                new { Status = RefundReplacementStatus.Approved.ToString() },
                new
                {
                    Status = legacy.Status.ToString(),
                    legacy.RemediatedAt,
                    RemediationReason = legacy.RemediationReason,
                    SupersedingTicket = refund.RequestNumber
                },
                remediationReason,
                now);
            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.SUBMIT",
                refund.PublicId,
                null,
                new
                {
                    refund.RequestNumber,
                    refund.OrderId,
                    refund.DeliveryId,
                    Type = refund.Type.ToString(),
                    Source = refund.Source.ToString(),
                    Status = refund.Status.ToString(),
                    Supersedes = legacy.RequestNumber,
                    refund.Deadline
                },
                refund.Reason,
                now);
            AddCustomerEvent(
                refund,
                NotificationEventTypes.LegacyReplacementSuperseded,
                $"refund-replacement:{legacy.PublicId:N}:superseded",
                $"Your replacement request {legacy.RequestNumber} is not supported for one-time orders and was superseded (closed without delivery; no replacement will be sent). A refund request {refund.RequestNumber} has been opened for the same delivery instead — Refund is the supported remedy and needs no further action from you.",
                now);

            await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
            result = new RemediateLegacyReplacementResult(
                ToResult(
                    legacy,
                    legacy.Order,
                    legacy.Delivery,
                    legacy.Customer,
                    legacy.Branch,
                    legacy.MilkTest,
                    legacy.DecidedByUser,
                    legacy.CompletedByUser),
                ToResult(
                    refund,
                    legacy.Order,
                    legacy.Delivery,
                    legacy.Customer,
                    legacy.Branch,
                    legacy.MilkTest,
                    null,
                    null));
        }, cancellationToken);

        return result!;
    }

    /// <summary>
    /// D1-D slice 5.2: executes the frozen delivery-fail refund for the
    /// narrow second-failure shape — an explicitly Refund-chosen, approved
    /// refund-only remedy linked to a failed replacement occurrence — being
    /// completed by staff. Returns null for every other ticket shape, leaving
    /// existing completion behavior (including first-failure and one-time
    /// refunds, which remain out-of-system) untouched.
    ///
    /// Uses the shared <see cref="DeliveryFailRefund"/> implementation, so
    /// the formula, snapshots, payment cap, wallet behavior, and the
    /// <c>delivery-fail:</c> idempotency key are identical to the automatic
    /// timeout path: whichever path runs first wins, the other reuses its
    /// records instead of duplicating them.
    /// </summary>
    private async Task<SecondFailureStaffRefund?> ExecuteSecondFailureRefundAsync(
        RefundReplacementRequest entity,
        long staffUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var occurrence = entity.SubscriptionDelivery;
        if (entity.Type != RefundReplacementType.Refund
            || entity.CustomerChoice != CustomerChoice.Refund
            || occurrence is null
            || !occurrence.IsReplacement
            || occurrence.RefundReplacementRequestId is null)
        {
            return null;
        }

        var subscription = await dbContext.Subscriptions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == occurrence.SubscriptionId, cancellationToken)
            ?? throw new ConflictException(
                $"The refund cannot be completed: the subscription for ticket {entity.RequestNumber} is missing. " +
                "Resolve it with staff support first.");
        // Stage 3: staff's recorded apply/waive decision wins when the ticket
        // went through approval; otherwise (automatic timeout path, tickets
        // approved before the decision existed) the frozen failure snapshot
        // applies, identical to before. Subscription test-failure tickets
        // (MilkTestRejected) always settle at 0% with no deduction.
        var deductionPercentage = DeliveryFailRefund.EffectiveDeductionPercentage(
            entity.Source,
            entity.SubscriptionDeliveryId,
            entity.AppliedDeductionPercentage,
            entity.DeliveryFailDeductionPercentageSnapshot);
        var payment = await DeliveryFailRefund.FindPaymentAsync(
                dbContext, subscription.Id, entity.CustomerId, cancellationToken)
            ?? throw new ConflictException(
                $"The refund cannot be completed: no successful payment covers ticket {entity.RequestNumber}. " +
                "Resolve it with staff support first.");
        var amounts = DeliveryFailRefund.Calculate(
            subscription.PayableAmount,
            subscription.TotalEntitlement,
            deductionPercentage,
            payment.Amount,
            payment.RefundedAmount);
        if (amounts.Remaining <= 0)
        {
            throw new ConflictException(
                $"The refund cannot be completed: the payment covering ticket {entity.RequestNumber} " +
                "is already fully refunded. Resolve it with staff support first.");
        }

        var deliveryNumber = entity.Delivery.DeliveryNumber ?? entity.Delivery.ReferenceNumber;
        var orderReference = subscription.SubscriptionNumber ?? deliveryNumber;
        var final = amounts.Final;
        if (final > 0)
        {
            var idempotencyKey = DeliveryFailRefund.IdempotencyKeyFor(entity.PublicId);
            await DeliveryFailRefund.CreditWalletAsync(
                dbContext,
                entity.CustomerId,
                subscription.Id,
                payment.Id,
                paymentOptions?.Value.Currency ?? payment.Currency,
                final,
                idempotencyKey,
                $"Delivery-fail staff refund {entity.RequestNumber} for subscription {orderReference}",
                now,
                cancellationToken);
            DeliveryFailRefund.RecordRefund(
                payment,
                final,
                idempotencyKey,
                "Delivery-fail staff-completed refund to wallet.",
                staffUserId,
                now);
        }

        return new SecondFailureStaffRefund(
            deliveryNumber,
            amounts.Gross,
            deductionPercentage,
            amounts.Deduction,
            final,
            subscription.PayableAmount,
            subscription.TotalEntitlement);
    }

    /// <summary>
    /// Audit remediation A: executes the frozen delivery-fail refund for an
    /// explicitly Refund-chosen first failure being completed by staff, and
    /// for a customer-raised post-delivery complaint (Refund type chosen at
    /// submit, no choice step) on a delivered occurrence. Returns null for
    /// every other ticket shape, leaving existing behavior untouched —
    /// notably order-backed one-time deliveries (no subscription occurrence
    /// to price a share from; reported gap, never invented math) and
    /// ReDelivery choices (owned by the delivery-completion hook).
    ///
    /// Uses the shared <see cref="DeliveryFailRefund"/> implementation with
    /// the same <c>delivery-fail:</c> per-ticket idempotency key as the D1-C
    /// timeout path: whichever path runs first wins, the other observes the
    /// committed records instead of duplicating them. Infeasible refunds
    /// (no snapshot, no covering payment, exhausted payment) throw before
    /// SaveChanges, so the ticket stays Approved for repair and retry — the
    /// remedy is never marked successful without a committed refund.
    /// </summary>
    private async Task<FirstFailureStaffRefund?> ExecuteFirstFailureRefundAsync(
        RefundReplacementRequest entity,
        long staffUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var occurrence = entity.SubscriptionDelivery;
        // Failed-flow tickets carry the choice; post-delivery complaints fix
        // the remedy at submit time (Refund type, never a choice step).
        var isRefundRemedy = entity.CustomerChoice == CustomerChoice.Refund
            || (entity.Source == RefundReplacementSource.CustomerPostDelivery
                && entity.CustomerChoice is null
                && entity.Type == RefundReplacementType.Refund);
        if (entity.SubscriptionDeliveryId is null
            || occurrence is null
            || occurrence.IsReplacement
            || entity.Source is not (RefundReplacementSource.DeliveryFailed
                or RefundReplacementSource.MilkTestRejected
                or RefundReplacementSource.CustomerPostDelivery)
            || entity.Type != RefundReplacementType.Refund
            || !isRefundRemedy)
        {
            return null;
        }

        var subscription = await dbContext.Subscriptions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == occurrence.SubscriptionId, cancellationToken)
            ?? throw new ConflictException(
                $"The refund cannot be completed: the subscription for ticket {entity.RequestNumber} is missing. " +
                "Resolve it with staff support first.");
        // Stage 3: staff's recorded apply/waive decision wins when the ticket
        // went through approval; otherwise (automatic timeout path, tickets
        // approved before the decision existed) the frozen failure snapshot
        // applies, identical to before. Subscription test-failure tickets
        // (MilkTestRejected) always settle at 0% with no deduction.
        var deductionPercentage = DeliveryFailRefund.EffectiveDeductionPercentage(
            entity.Source,
            entity.SubscriptionDeliveryId,
            entity.AppliedDeductionPercentage,
            entity.DeliveryFailDeductionPercentageSnapshot);
        // Shared lookup (latest Success/PartiallyRefunded payment), identical
        // to the timeout path; a fully refunded payment reads as no covering
        // payment, and the domain cap below stays exact.
        var payment = await DeliveryFailRefund.FindPaymentAsync(
                dbContext, subscription.Id, entity.CustomerId, cancellationToken)
            ?? throw new ConflictException(
                $"The refund cannot be completed: no successful payment covers ticket {entity.RequestNumber}. " +
                "Resolve it with staff support first.");
        var amounts = DeliveryFailRefund.Calculate(
            subscription.PayableAmount,
            subscription.TotalEntitlement,
            deductionPercentage,
            payment.Amount,
            payment.RefundedAmount);
        if (amounts.Remaining <= 0)
        {
            throw new ConflictException(
                $"The refund cannot be completed: the payment covering ticket {entity.RequestNumber} " +
                "is already fully refunded. Resolve it with staff support first.");
        }

        var deliveryNumber = entity.Delivery.DeliveryNumber ?? entity.Delivery.ReferenceNumber;
        var orderReference = subscription.SubscriptionNumber ?? deliveryNumber;
        var final = amounts.Final;
        if (final > 0)
        {
            var idempotencyKey = DeliveryFailRefund.IdempotencyKeyFor(entity.PublicId);
            await DeliveryFailRefund.CreditWalletAsync(
                dbContext,
                entity.CustomerId,
                subscription.Id,
                payment.Id,
                paymentOptions?.Value.Currency ?? payment.Currency,
                final,
                idempotencyKey,
                $"Delivery-fail staff refund {entity.RequestNumber} for subscription {orderReference}",
                now,
                cancellationToken);
            DeliveryFailRefund.RecordRefund(
                payment,
                final,
                idempotencyKey,
                "Delivery-fail staff-completed refund to wallet.",
                staffUserId,
                now);
        }

        return new FirstFailureStaffRefund(
            deliveryNumber,
            amounts.Gross,
            deductionPercentage,
            amounts.Deduction,
            final,
            subscription.PayableAmount,
            subscription.TotalEntitlement);
    }

    /// <summary>
    /// Whether the one-time order is already fully refunded from authoritative
    /// database state, on either of two prongs. First, a completed refund
    /// ticket for the order is terminal: the frozen policy maximum was paid
    /// out, including any customer-fault deduction remainder that is no
    /// longer refundable, so a second ticket could only over-refund. Second,
    /// with no completed ticket, verified (<see cref="RefundStatus.Succeeded"/>)
    /// settlement rows exist with nothing left outstanding across the
    /// Success/PartiallyRefunded payments (for example a cancellation-flow
    /// refund issued outside this workflow). Unpaid orders (nothing
    /// outstanding, no settlement) are not fully refunded: they stay
    /// submittable and completion still guards them. Partially refunded
    /// orders with a remaining balance and no completed ticket are not fully
    /// refunded: submission stays allowed and completion still caps the
    /// credit at the remainder.
    /// </summary>
    private async Task<bool> IsOneTimeOrderFullyRefundedAsync(
        long orderId,
        long customerId,
        CancellationToken cancellationToken)
    {
        if (await dbContext.RefundReplacementRequests.AsNoTracking().AnyAsync(
            x => x.OrderId == orderId
                && x.CustomerId == customerId
                && x.Type == RefundReplacementType.Refund
                && x.Status == RefundReplacementStatus.Completed,
            cancellationToken))
        {
            return true;
        }

        var payments = await OneTimeOrderRefund.FindEligiblePaymentsAsync(
            dbContext, orderId, customerId, cancellationToken);
        if (payments.Sum(x => x.Amount - x.RefundedAmount) > 0)
        {
            return false;
        }

        return await OneTimeOrderRefund.HasVerifiedSettlementAsync(
            dbContext, orderId, customerId, cancellationToken);
    }

    private static string AlreadyRefundedMessage(string? orderNumber) =>
        orderNumber is null
            ? "A refund has already been completed for this order. If you need further help, contact support."
            : $"A refund has already been completed for order {orderNumber}. If you need further help, contact support.";

    /// <summary>
    /// Stage 1: executes the frozen one-time-order refund for an approved
    /// order-backed Refund ticket being completed by staff. Uses only the
    /// ticket's frozen <c>AppliedDeductionPercentage</c> and the frozen
    /// <c>Order.PayableAmount</c>; aggregates every eligible payment and
    /// allocates deterministically. Credits the wallet and records
    /// per-payment local refunds (no gateway call) under deterministic
    /// ticket-scoped idempotency keys, so retries and concurrent completions
    /// reuse committed records instead of duplicating them.
    ///
    /// Returns the outcome, or null when the ticket was already made whole
    /// through an external Payments settlement (a Succeeded refund row with
    /// nothing left to credit) — completion then creates nothing. Throws
    /// before SaveChanges when no positive amount can be credited and no
    /// external settlement exists, so the ticket stays Approved for repair
    /// and retry.
    /// </summary>
    private async Task DesignateManualBankTransferAsync(
        RefundReplacementRequest entity,
        long staffUserId,
        decimal? appliedDeductionPercentage,
        CancellationToken cancellationToken)
    {
        var deduction = appliedDeductionPercentage
            ?? throw new ConflictException(
                $"The refund cannot be approved for manual bank transfer: ticket {entity.RequestNumber} has no recorded deduction decision.");

        var bankDetail = await dbContext.CustomerBankDetails
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == entity.CustomerId, cancellationToken);
        if (bankDetail is null || string.IsNullOrWhiteSpace(bankDetail.ProtectedAccountNumber))
        {
            throw new ValidationAppException(
                "The customer has no saved bank details for a manual bank transfer. Ask the customer to add them in Profile, then approve again.",
                "settlementMethod");
        }

        string fullAccountNumber;
        try
        {
            fullAccountNumber = _bankDetails.Unprotect(bankDetail.ProtectedAccountNumber);
        }
        catch (CryptographicException)
        {
            throw new ConflictException(
                $"The refund cannot be approved for manual bank transfer: the stored bank details for ticket {entity.RequestNumber} are currently unavailable. Resolve it with staff support first.");
        }

        decimal reservedTotal;
        string currency;
        List<(Payment Payment, decimal Amount)> allocations = new();
        if (entity.OrderId.HasValue)
        {
            var order = entity.Order
                ?? throw new ConflictException(
                    $"The refund cannot be approved for manual bank transfer: the order for ticket {entity.RequestNumber} is missing. Resolve it with staff support first.");
            var (_, net) = OneTimeOrderRefund.Calculate(order.PayableAmount, deduction);
            var payments = await OneTimeOrderRefund.FindEligiblePaymentsAsync(
                dbContext, order.Id, entity.CustomerId, cancellationToken);
            var eligiblePaid = payments.Sum(x => x.Amount - x.RefundedAmount);
            var final = net > eligiblePaid ? eligiblePaid : net;
            if (final <= 0)
            {
                if (await OneTimeOrderRefund.HasVerifiedSettlementAsync(dbContext, order.Id, entity.CustomerId, cancellationToken))
                {
                    throw new ConflictException(
                        $"The refund cannot be approved for manual bank transfer: order {order.OrderNumber} has already been settled through another refund path.");
                }

                throw new ConflictException(
                    eligiblePaid <= 0
                        ? $"The refund cannot be approved for manual bank transfer: no verified refund settlement exists for order {order.OrderNumber} and no eligible paid amount remains (unpaid/COD or fully refunded). Resolve it with staff support first."
                        : $"The refund cannot be approved for manual bank transfer: the {deduction:0.00}% customer-fault deduction covers the full order amount for order {order.OrderNumber}, so no positive amount can be transferred. Resolve it with staff support first.");
            }

            var plan = OneTimeOrderRefund.Allocate(final, payments);
            foreach (var allocation in plan)
            {
                allocations.Add((allocation.Payment, allocation.Amount));
            }

            currency = plan.Count > 0
                ? plan[0].Payment.Currency
                : paymentOptions?.Value.Currency ?? "INR";
            reservedTotal = final;
        }
        else
        {
            var occurrence = entity.SubscriptionDelivery
                ?? throw new ConflictException(
                    $"The refund cannot be approved for manual bank transfer: the subscription occurrence for ticket {entity.RequestNumber} is missing. Resolve it with staff support first.");
            var subscription = await dbContext.Subscriptions
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == occurrence.SubscriptionId, cancellationToken)
                ?? throw new ConflictException(
                    $"The refund cannot be approved for manual bank transfer: the subscription for ticket {entity.RequestNumber} is missing. Resolve it with staff support first.");
            var payment = await DeliveryFailRefund.FindPaymentAsync(
                    dbContext, subscription.Id, entity.CustomerId, cancellationToken)
                ?? throw new ConflictException(
                    $"The refund cannot be approved for manual bank transfer: no successful payment covers ticket {entity.RequestNumber}. Resolve it with staff support first.");
            var amounts = DeliveryFailRefund.Calculate(
                subscription.PayableAmount,
                subscription.TotalEntitlement,
                deduction,
                payment.Amount,
                payment.RefundedAmount);
            if (amounts.Remaining <= 0)
            {
                throw new ConflictException(
                    $"The refund cannot be approved for manual bank transfer: the payment covering ticket {entity.RequestNumber} is already fully refunded. Resolve it with staff support first.");
            }

            if (amounts.Final <= 0)
            {
                throw new ConflictException(
                    $"The refund cannot be approved for manual bank transfer: the customer-fault deduction covers the full share for ticket {entity.RequestNumber}, so no positive amount can be transferred. Resolve it with staff support first.");
            }

            allocations.Add((payment, amounts.Final));
            currency = payment.Currency;
            reservedTotal = amounts.Final;
        }

        foreach (var (payment, amount) in allocations)
        {
            ManualBankTransferReservation.Reserve(
                payment,
                amount,
                ManualBankTransferReservation.ReservationKeyFor(entity.PublicId, payment.Id),
                staffUserId);
        }

        string protectedSnapshot;
        try
        {
            protectedSnapshot = _beneficiary.Protect(fullAccountNumber);
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }

        Mutate(() => entity.DesignateManualBankSettlement(
            bankDetail.AccountHolderName,
            bankDetail.BankName,
            bankDetail.Ifsc,
            bankDetail.AccountLast4,
            protectedSnapshot,
            reservedTotal,
            currency));
    }

    private async Task<OneTimeOrderRefundOutcome?> ExecuteOneTimeOrderRefundAsync(
        RefundReplacementRequest entity,
        long staffUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var order = entity.Order
            ?? throw new ConflictException(
                $"The refund cannot be completed: the order for ticket {entity.RequestNumber} is missing. " +
                "Resolve it with staff support first.");
        var appliedPercentage = entity.AppliedDeductionPercentage
            ?? throw new ConflictException(
                $"The refund cannot be completed: ticket {entity.RequestNumber} has no recorded deduction decision. " +
                "Approve it again with an explicit apply/waive deduction decision first.");
        var (deduction, net) = OneTimeOrderRefund.Calculate(order.PayableAmount, appliedPercentage);
        var payments = await OneTimeOrderRefund.FindEligiblePaymentsAsync(
            dbContext, order.Id, entity.CustomerId, cancellationToken);
        var eligiblePaid = payments.Sum(x => x.Amount - x.RefundedAmount);
        var final = net > eligiblePaid ? eligiblePaid : net;
        if (final > 0)
        {
            var allocations = OneTimeOrderRefund.Allocate(final, payments);
            var walletKey = OneTimeOrderRefund.WalletIdempotencyKeyFor(entity.PublicId);
            var firstPaymentId = allocations.Count > 0 ? allocations[0].Payment.Id : (long?)null;
            var currency = allocations.Count > 0
                ? allocations[0].Payment.Currency
                : paymentOptions?.Value.Currency ?? "INR";
            await OneTimeOrderRefund.CreditWalletAsync(
                dbContext,
                entity.CustomerId,
                order.Id,
                firstPaymentId,
                currency,
                final,
                walletKey,
                $"One-time order refund {entity.RequestNumber} for order {order.OrderNumber}",
                now,
                cancellationToken);
            foreach (var allocation in allocations)
            {
                DeliveryFailRefund.RecordRefund(
                    allocation.Payment,
                    allocation.Amount,
                    OneTimeOrderRefund.RefundIdempotencyKeyFor(entity.PublicId, allocation.Payment.Id),
                    $"One-time order staff-completed refund to wallet for ticket {entity.RequestNumber}.",
                    staffUserId,
                    now);
            }

            return new OneTimeOrderRefundOutcome(
                order.OrderNumber,
                order.PayableAmount,
                appliedPercentage,
                deduction,
                eligiblePaid,
                final,
                allocations);
        }

        var externallySettled = await OneTimeOrderRefund.HasVerifiedSettlementAsync(
            dbContext, order.Id, entity.CustomerId, cancellationToken);
        if (externallySettled)
        {
            return null;
        }

        throw new ConflictException(
            eligiblePaid <= 0
                ? $"The refund cannot be completed: no verified refund settlement exists for order {order.OrderNumber} " +
                  "and no eligible paid amount remains (unpaid/COD or fully refunded). Resolve it with staff support first."
                : $"The refund cannot be completed: the {appliedPercentage:0.00}% customer-fault deduction covers " +
                  $"the full order amount for order {order.OrderNumber}, so no positive amount can be credited. " +
                  "Resolve it with staff support first.");
    }

    /// <summary>
    /// Stage 1: read-only, branch-scoped preview of an order-backed Refund
    /// ticket's proposed wallet settlement. Uses the same frozen-basis math
    /// as settlement; when <paramref name="applyDeduction"/> is true the
    /// current shared configuration percentage is shown (approval snapshots
    /// it), otherwise zero. No state is changed: no SaveChanges, no audit,
    /// no outbox event.
    /// </summary>
    public async Task<OrderRefundPreviewResult> PreviewOrderRefundAsync(
        RefundReplacementActor actor,
        Guid requestId,
        bool applyDeduction,
        CancellationToken cancellationToken)
    {
        var ticket = await BaseQuery(asNoTracking: true)
            .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
            ?? throw new NotFoundException("The refund/replacement request was not found.");
        EnsureStaffAccess(actor, ticket);
        if (!ticket.OrderId.HasValue || ticket.Type != RefundReplacementType.Refund)
        {
            throw new BusinessRuleException("A refund preview is only available for one-time order refund tickets.");
        }

        var order = ticket.Order
            ?? throw new ConflictException("The order for this ticket is missing.");
        var appliedPercentage = applyDeduction
            ? (await configurationService.GetAsync(cancellationToken)).DeliveryFailDeductionPercentage
            : 0m;
        var (deduction, net) = OneTimeOrderRefund.Calculate(order.PayableAmount, appliedPercentage);
        var payments = await OneTimeOrderRefund.FindEligiblePaymentsAsync(
            dbContext, order.Id, ticket.CustomerId, cancellationToken);
        var eligiblePaid = payments.Sum(x => x.Amount - x.RefundedAmount);
        var proposed = net > eligiblePaid ? eligiblePaid : net;
        if (proposed < 0)
        {
            proposed = 0;
        }

        var currency = payments.Count > 0
            ? payments[0].Currency
            : paymentOptions?.Value.Currency ?? "INR";
        return new OrderRefundPreviewResult(
            ticket.PublicId,
            ticket.RequestNumber,
            order.PublicId,
            order.OrderNumber,
            order.PayableAmount,
            appliedPercentage,
            deduction,
            eligiblePaid,
            proposed,
            currency);
    }

    private sealed record FirstFailureStaffRefund(
        string DeliveryNumber,
        decimal GrossRefund,
        decimal DeductionPercentage,
        decimal DeductionAmount,
        decimal WalletCredit,
        decimal SubscriptionPayable,
        int TotalEntitlement);

    private sealed record SecondFailureStaffRefund(
        string DeliveryNumber,
        decimal GrossRefund,
        decimal DeductionPercentage,
        decimal DeductionAmount,
        decimal WalletCredit,
        decimal SubscriptionPayable,
        int TotalEntitlement);

    public async Task<RefundReplacementImageResult> UploadImageAsync(
        RefundReplacementActor actor,
        Guid requestId,
        Stream content,
        string fileName,
        string contentType,
        long fileSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalizedContentType = ValidateImage(contentType, fileSize);
        var safeFileName = ValidateFileName(fileName);

        RefundReplacementImageResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            // Only the owning customer attaches the proof image for a normal request.
            var entity = await BaseQuery()
                .SingleOrDefaultAsync(
                    x => x.PublicId == requestId && x.CustomerId == actor.UserId,
                    cancellationToken) ?? throw new NotFoundException("The refund/replacement request was not found.");

            // Stage 2 conditional evidence: milk-test-rejection tickets stay
            // upload-closed (the test is the evidence). Failed-delivery
            // exemptions may still accept an optional photo — it is validated
            // and stored exactly like mandatory evidence.
            if (entity.Source == RefundReplacementSource.MilkTestRejected)
            {
                throw new BusinessRuleException(
                    "A proof image is not required for a request opened from a rejected doorstep milk test.");
            }

            if (entity.Status != RefundReplacementStatus.Pending)
            {
                throw new ConflictException("A proof image can only be added while the request is pending.");
            }

            var now = timeProvider.Now;
            var extension = ImageExtension(normalizedContentType);
            var storageKey =
                $"{now:yyyy/MM}/{entity.BranchId}/{entity.PublicId:N}/{Guid.NewGuid():N}{extension}";

            var stored = await mediaStorage.SaveAsync(
                storageKey,
                content,
                normalizedContentType,
                cancellationToken);

            try
            {
                if (stored.FileSize != fileSize)
                {
                    throw new InvalidOperationException(
                        "The stored media size does not match the uploaded image size.");
                }

                var image = new RefundReplacementImage(
                    entity.Id,
                    stored.StorageKey,
                    safeFileName,
                    normalizedContentType,
                    stored.FileSize,
                    actor.UserId,
                    now);

                Mutate(() => entity.AddImage(image));

                AddAudit(
                    actor.UserId,
                    "REFUND_REPLACEMENT.IMAGE_UPLOAD",
                    entity.PublicId,
                    null,
                    new { ImageId = image.PublicId, image.FileName, image.ContentType, image.FileSize },
                    null,
                    now);

                await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
                result = ToImageResult(image);
            }
            catch
            {
                // The database write failed — remove the orphaned blob.
                await mediaStorage.DeleteIfExistsAsync(stored.StorageKey, CancellationToken.None);
                throw;
            }
        }, cancellationToken);

        return result!;
    }

    public async Task<StoredMediaContent> OpenImageAsync(
        RefundReplacementActor actor,
        Guid requestId,
        Guid imageId,
        CancellationToken cancellationToken)
    {
        var request = await BaseQuery(asNoTracking: true)
            .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
            ?? throw new NotFoundException("The refund/replacement request was not found.");

        // The owning customer or branch-scoped staff may read the private file.
        if (request.CustomerId != actor.UserId)
        {
            EnsureStaffAccess(actor, request);
        }

        var image = request.Images.FirstOrDefault(x => x.PublicId == imageId)
            ?? throw new NotFoundException("The proof image was not found.");

        var stored = await mediaStorage.OpenReadAsync(image.StorageKey, cancellationToken);
        return new StoredMediaContent(stored.Content, image.ContentType, image.FileSize);
    }

    private async Task<RefundReplacementRequestResult> DecideAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideRefundReplacementRequest request,
        bool approve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Remarks, "remarks", MaximumRemarksLength, required: false);

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var entity = await BaseQuery()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
                ?? throw new NotFoundException("The refund/replacement request was not found.");

            EnsureStaffAccess(actor, entity);

            // Employee-origin policy: approve/reject decisions on an
            // employee-originated ticket require an Owner or System
            // Administrator actor (never the requester); customer tickets
            // keep the existing authorization rules.
            await EmployeeOriginGuard.RequireDecisionAuthorityAsync(
                dbContext, entity.CustomerId, actor.UserId, "approved or rejected", cancellationToken);

            // Stage 1: legacy order-backed Replacement tickets are preserved
            // but frozen. Approval would promise a re-delivery workflow that
            // does not exist for one-time orders, so it is refused; rejection
            // stays available so staff can close them truthfully. Completion
            // is refused separately in CompleteAsync.
            var isOrderTicket = entity.OrderId.HasValue;
            var isOrderReplacement = isOrderTicket && entity.Type == RefundReplacementType.Replacement;
            if (approve && entity.Status == RefundReplacementStatus.Pending && isOrderReplacement)
            {
                throw new BusinessRuleException(
                    $"Only refunds are supported for one-time orders. Replacement ticket {entity.RequestNumber} " +
                    "cannot be approved; resolve it manually with staff support (reject to close it without delivery).");
            }

            // Stage 1/3: a Refund approval records support's explicit
            // apply/waive deduction decision with a frozen snapshot. A
            // missing decision is rejected explicitly — an omitted boolean
            // must never default silently. Order-backed tickets decide from
            // Pending; subscription delivery-fail tickets decide once the
            // customer's Refund choice is recorded. Every other ticket shape
            // is untouched.
            decimal? appliedDeductionPercentage = null;
            var deductionWaived = false;
            string? deductionWaiverReason = null;
            var isSubscriptionFailRefund = !isOrderTicket
                && entity.SubscriptionDeliveryId.HasValue
                && entity.Type == RefundReplacementType.Refund
                && (entity.Source == RefundReplacementSource.DeliveryFailed
                    || entity.Source == RefundReplacementSource.MilkTestRejected)
                && entity.CustomerChoice == CustomerChoice.Refund
                && (entity.Status == RefundReplacementStatus.Pending
                    || entity.Status == RefundReplacementStatus.AwaitingCustomerChoice);
            // Lifecycle first: on a non-pending ticket the Approve/Reject
            // transition below reports the domain status conflict, never a
            // decision-validation error.
            if (approve
                && entity.Type == RefundReplacementType.Refund
                && request is ApproveRefundReplacementRequest approveDecision
                && ((isOrderTicket && entity.Status == RefundReplacementStatus.Pending)
                    || isSubscriptionFailRefund))
            {
                if (approveDecision.ApplyDeduction is null)
                {
                    throw new ValidationAppException(
                        isOrderTicket
                            ? "An explicit deduction decision is required to approve a one-time order refund. " +
                                "Choose whether to apply the customer-fault deduction or waive it."
                            : "An explicit deduction decision is required to approve this failed-delivery refund. " +
                                "Choose whether to apply the customer-fault deduction or waive it.",
                        "applyDeduction");
                }

                if (approveDecision.ApplyDeduction.Value)
                {
                    // Order tickets snapshot the current Admin Centre value;
                    // subscription tickets reuse the percentage frozen when the
                    // failure was recorded, identical to the automatic timeout
                    // path, so staff approval and timeout settle the same basis.
                    appliedDeductionPercentage = isOrderTicket
                        ? (await configurationService.GetAsync(cancellationToken)).DeliveryFailDeductionPercentage
                        : entity.DeliveryFailDeductionPercentageSnapshot
                            ?? throw new ConflictException(
                                $"The refund cannot be approved: ticket {entity.RequestNumber} has no frozen deduction. " +
                                "Resolve it with staff support first.");
                    Mutate(() => entity.RecordDeductionDecision(true, appliedDeductionPercentage.Value, null));
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(approveDecision.WaiverReason))
                    {
                        throw new ValidationAppException(
                            "A waiver reason is required when waiving the customer-fault deduction.",
                            "waiverReason");
                    }

                    deductionWaiverReason = approveDecision.WaiverReason.Trim();
                    Mutate(() => entity.RecordDeductionDecision(false, 0m, deductionWaiverReason));
                    appliedDeductionPercentage = 0m;
                    deductionWaived = true;
                }
            }

            // Stage 2: a Refund approval designates the settlement method
            // explicitly, in the same transaction as the deduction decision.
            // Wallet preserves today's completion behavior; ManualBankTransfer
            // freezes the beneficiary snapshot and reserves the authoritative
            // amount against the covering payments instead of moving money.
            // The method is exactly-once and never inferred from ledger
            // state; legacy tickets keep Unspecified and settle as before.
            // A missing method is rejected explicitly, never defaulted.
            RefundSettlementMethod? designatedMethod = null;
            if (approve
                && entity.Type == RefundReplacementType.Refund
                && request is ApproveRefundReplacementRequest approveSettlement
                && ((isOrderTicket && entity.Status == RefundReplacementStatus.Pending)
                    || isSubscriptionFailRefund))
            {
                designatedMethod = approveSettlement.SettlementMethod;
                if (designatedMethod is null || !Enum.IsDefined(designatedMethod.Value))
                {
                    throw new ValidationAppException(
                        "An explicit settlement method is required to approve a refund: choose wallet refund or manual bank transfer.",
                        "settlementMethod");
                }

                if (designatedMethod == RefundSettlementMethod.Wallet)
                {
                    Mutate(() => entity.DesignateWalletSettlement());
                }
                else
                {
                    await DesignateManualBankTransferAsync(
                        entity,
                        actor.UserId,
                        appliedDeductionPercentage,
                        cancellationToken);
                }
            }

            // Audit remediation B: a failed replacement cannot be replaced
            // again, so a ticket linked to a replacement occurrence is
            // refund-only. Rejecting it would strand the original approved
            // ReDelivery ticket (no re-replace, no timeout, no sweeper), so
            // rejection is refused here even for direct API callers; the
            // staff UI hides the action via IsRefundOnly.
            if (!approve && IsRefundOnlyRemedy(entity))
            {
                throw new BusinessRuleException(
                    "Only the refund remedy is available for a failed re-delivery replacement; " +
                    "it cannot be rejected. Approve and complete the refund instead.");
            }

            // Audit remediation B: approving a refund-only remedy without the
            // recorded Refund choice would create an Approved ticket the
            // timeout worker skips and completion cannot pay (a choice is
            // unrecordable after approval) — permanently stranded. Force the
            // choice-first order; customer abandonment still resolves through
            // the automatic timeout refund.
            if (approve && IsRefundOnlyRemedy(entity) && entity.CustomerChoice != CustomerChoice.Refund)
            {
                throw new ConflictException(
                    "A refund-only remedy can only be approved after the customer has chosen Refund.");
            }

            // Stage 2 conditional evidence (authoritative): a ticket that
            // requires a photo cannot be approved without one. The client
            // pre-check is advisory only; this gate closes the direct-API
            // bypass. It runs after the lifecycle-sensitive checks above so a
            // non-pending ticket still reports the domain status conflict, and
            // after the remedy-specific refusals so those errors keep their
            // precedence. Rejection never requires evidence. Completion needs
            // no separate gate: images are append-only (no removal endpoint),
            // so an approved photo-required ticket provably holds evidence,
            // while tickets approved before this rule complete unchanged.
            if (approve
                && (entity.Status == RefundReplacementStatus.Pending
                    || entity.Status == RefundReplacementStatus.AwaitingCustomerChoice)
                && entity.ProofImageRequired
                && !entity.Images.Any())
            {
                throw new BusinessRuleException(
                    "A proof image is required before this request can be approved. " +
                    "Ask the customer to attach a clear photo of the delivered product.");
            }

            var now = timeProvider.Now;
            var previousStatus = entity.Status;
            SubscriptionDelivery? createdReplacement = null;
            Mutate(() =>
            {
                if (approve)
                {
                    entity.Approve(actor.UserId, now, request.Remarks);
                }
                else
                {
                    entity.Reject(actor.UserId, now, request.Remarks);
                }
            });

            if (approve && request is ApproveRefundReplacementRequest approveRequest)
            {
                createdReplacement = await CreateReplacementForApprovalAsync(
                    entity,
                    previousStatus,
                    approveRequest.ReplacementDate,
                    approveRequest.Remarks,
                    now,
                    cancellationToken);
            }

            var decider = await dbContext.Users
                .AsNoTracking()
                .SingleAsync(x => x.Id == actor.UserId, cancellationToken);

            // Stage 1: the frozen deduction decision is part of the approval
            // audit so support's apply/waive choice, snapshot percentage and
            // waiver reason are traceable to the handling staff member.
            // Stage 2: the designated settlement method is audited too; for
            // manual bank transfers only the masked beneficiary destination
            // and frozen amount appear here — never the full account number.
            object? manualBankSettlement = entity.SettlementMethod == RefundSettlementMethod.ManualBankTransfer
                ? new
                {
                    MaskedDestination = $"{entity.ManualBankNameSnapshot} \u00b7\u00b7\u00b7\u00b7 {entity.ManualBankAccountLast4Snapshot}",
                    entity.ManualBankReservedAmount,
                    entity.ManualBankReservedCurrency
                }
                : null;
            object newValue = (createdReplacement, appliedDeductionPercentage) switch
            {
                (not null, _) => new
                {
                    Status = entity.Status.ToString(),
                    entity.DecidedAt,
                    ReplacementDate = createdReplacement.ScheduledDate,
                    ReplacementDeliveryId = createdReplacement.PublicId,
                    SettlementMethod = entity.SettlementMethod.ToString(),
                    ManualBankSettlement = manualBankSettlement
                },
                (null, not null) => new
                {
                    Status = entity.Status.ToString(),
                    entity.DecidedAt,
                    AppliedDeductionPercentage = appliedDeductionPercentage.Value,
                    DeductionWaived = deductionWaived,
                    DeductionWaiverReason = deductionWaiverReason,
                    SettlementMethod = entity.SettlementMethod.ToString(),
                    ManualBankSettlement = manualBankSettlement
                },
                _ => new
                {
                    Status = entity.Status.ToString(),
                    entity.DecidedAt,
                    SettlementMethod = entity.SettlementMethod.ToString(),
                    ManualBankSettlement = manualBankSettlement
                }
            };
            AddAudit(
                actor.UserId,
                approve ? "REFUND_REPLACEMENT.APPROVE" : "REFUND_REPLACEMENT.REJECT",
                entity.PublicId,
                new { Status = previousStatus.ToString() },
                newValue,
                request.Remarks,
                now);

            AddCustomerEvent(
                entity,
                NotificationEventTypes.RefundReplacementDecided,
                $"refund-replacement:{entity.PublicId:N}:decided:{entity.Status}",
                approve && createdReplacement is not null
                    ? $"Your replacement delivery has been approved and scheduled for {createdReplacement.ScheduledDate:yyyy-MM-dd}."
                    : approve && entity.SettlementMethod == RefundSettlementMethod.ManualBankTransfer
                        ? $"Your refund request has been approved for manual bank transfer of {entity.ManualBankReservedAmount:0.00} {entity.ManualBankReservedCurrency}. The amount will be transferred to your saved bank account."
                        : approve && appliedDeductionPercentage is not null && deductionWaived
                        ? "Your refund request has been approved with the customer-fault deduction waived."
                        : approve && appliedDeductionPercentage is not null
                            ? $"Your refund request has been approved with a customer-fault deduction of {appliedDeductionPercentage.Value:0.00}% applied."
                            : approve
                                ? "Your refund/replacement request has been approved."
                                : "Your refund/replacement request has been rejected.",
                now);

            try
            {
                await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
            }
            catch (DbUpdateException) when (createdReplacement is not null)
            {
                // Same-date unique-race backstop: a concurrent approval may have
                // committed an identical replacement before this transaction
                // saved. Re-read authoritatively; a now-present replacement
                // means this retry must lose as a conflict, never duplicate.
                var entityId = entity.Id;
                dbContext.ChangeTracker.Clear();
                var replacementExists = await dbContext.SubscriptionDeliveries
                    .AsNoTracking()
                    .AnyAsync(
                        x => x.IsReplacement && x.RefundReplacementRequestId == entityId,
                        cancellationToken);
                if (replacementExists)
                {
                    throw new ConflictException(
                        "The request was already approved by another operation. Refresh and try again.");
                }

                throw;
            }

            // D1-D slice 6.1: the just-created occurrence is reported through
            // the same summary shape as the persisted reads. No operational
            // delivery exists yet at approval time, so no number is exposed.
            ReplacementSummary? approvedReplacement = createdReplacement is null
                ? null
                : new ReplacementSummary(
                    createdReplacement.PublicId,
                    createdReplacement.ScheduledDate,
                    null);
            result = ToResult(
                entity,
                entity.Order,
                entity.Delivery,
                entity.Customer,
                entity.Branch,
                entity.MilkTest,
                decider,
                entity.CompletedByUser,
                approvedReplacement);
        }, cancellationToken);

        return result!;
    }

    /// <summary>
    /// D1-D: creates the approved re-delivery occurrence inside the approval
    /// transaction. Returns null for every non-re-delivery approval, leaving
    /// the existing one-time refund/replacement behavior unchanged.
    /// The replaced occurrence is always the ticket's own persisted source —
    /// client input supplies at most an optional date, never an identity.
    /// </summary>
    private async Task<SubscriptionDelivery?> CreateReplacementForApprovalAsync(
        RefundReplacementRequest entity,
        RefundReplacementStatus statusBeforeDecision,
        DateOnly? staffDate,
        string? remarks,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Replacements originate two ways: the failed-flow customer choice
        // (DeliveryFailed + recorded ReDelivery choice) or a customer-raised
        // post-delivery complaint (CustomerPostDelivery + Replacement type
        // chosen at submit, still Pending). Anything else creates nothing.
        var isFailedChoice = entity.Source == RefundReplacementSource.DeliveryFailed
            && entity.CustomerChoice == CustomerChoice.ReDelivery;
        var isPostDeliveryComplaint = entity.Source == RefundReplacementSource.CustomerPostDelivery
            && entity.CustomerChoice is null
            && entity.Type == RefundReplacementType.Replacement;
        if (entity.SubscriptionDeliveryId is null || (!isFailedChoice && !isPostDeliveryComplaint))
        {
            return null;
        }

        EnsureRedeliveryApprovable(entity, statusBeforeDecision);

        var subscription = await LoadSubscriptionForReplacementAsync(
            entity.SubscriptionDeliveryId.Value, asNoTracking: false, cancellationToken);
        var source = subscription.Deliveries.Single(x => x.Id == entity.SubscriptionDeliveryId.Value);
        var expectedOccurrenceStatus = isPostDeliveryComplaint
            ? SubscriptionDeliveryStatus.Delivered
            : SubscriptionDeliveryStatus.Failed;
        if (source.Status != expectedOccurrenceStatus)
        {
            throw new ConflictException(isPostDeliveryComplaint
                ? "Only a delivered occurrence can be replaced from a post-delivery complaint."
                : "Only a failed delivery can be replaced.");
        }
        // D1-D slice 4 (defense in depth): even a directly recorded
        // ReDelivery choice on a replacement failure can never mint another
        // replacement — the domain creator is the single choke point and it
        // rejects replacement sources as well.
        if (source.IsReplacement)
        {
            throw new ConflictException(
                "Only a refund is available for a failed re-delivery replacement; it cannot be replaced again.");
        }

        var cutoff = await GetReplacementCutoffAsync(cancellationToken);
        var date = staffDate.HasValue
            ? ValidateStaffReplacementDate(subscription, staffDate.Value, now, cutoff)
            : ProposeReplacementDate(subscription, now, cutoff);

        SubscriptionDelivery? created = null;
        Mutate(() => created = subscription.AddReplacementDelivery(
            source, date, entity.Id, ResolveReplacementReason(remarks, entity)));
        return created;
    }

    /// <summary>
    /// D1-D: re-delivery approvals require a subscription delivery-fail ticket
    /// still awaiting choice with a recorded ReDelivery choice, or a
    /// customer-raised post-delivery complaint (Pending, Replacement type)
    /// on a delivered occurrence. The status is evaluated as of before the
    /// approval transition (approval itself moves the ticket to Approved
    /// inside the same transaction).
    /// </summary>
    private static void EnsureRedeliveryApprovable(
        RefundReplacementRequest entity,
        RefundReplacementStatus statusBeforeDecision)
    {
        var isFailedChoice = entity.Source == RefundReplacementSource.DeliveryFailed
            && entity.SubscriptionDeliveryId is not null
            && entity.CustomerChoice == CustomerChoice.ReDelivery;
        var isPostDeliveryComplaint = entity.Source == RefundReplacementSource.CustomerPostDelivery
            && entity.SubscriptionDeliveryId is not null
            && entity.CustomerChoice is null
            && entity.Type == RefundReplacementType.Replacement;
        if (!isFailedChoice && !isPostDeliveryComplaint)
        {
            throw new ConflictException(
                "Only a subscription delivery-fail ticket with a recorded re-delivery choice can create a replacement.");
        }

        var requiredStatus = isPostDeliveryComplaint
            ? RefundReplacementStatus.Pending
            : RefundReplacementStatus.AwaitingCustomerChoice;
        if (statusBeforeDecision != requiredStatus)
        {
            throw new ConflictException(isPostDeliveryComplaint
                ? "A re-delivery can only be approved while the complaint is pending."
                : "A re-delivery can only be approved while the ticket awaits customer choice.");
        }
    }

    /// <summary>
    /// Audit remediation B: a ticket is refund-only when its failed
    /// occurrence is itself a re-delivery replacement. Read-only, derived
    /// strictly from the persisted ticket-to-occurrence relationship.
    /// </summary>
    private static bool IsRefundOnlyRemedy(RefundReplacementRequest entity) =>
        entity.SubscriptionDelivery?.IsReplacement == true;

    /// <summary>
    /// D1-D slice 4: identifies a failed replacement through the persisted
    /// occurrence flag. Read-only and transaction-safe: the occurrence row is
    /// never modified by choice recording.
    /// </summary>
    private async Task<bool> IsReplacementOccurrenceAsync(
        long subscriptionDeliveryId,
        CancellationToken cancellationToken) =>
        await dbContext.SubscriptionDeliveries.AsNoTracking()
            .AnyAsync(x => x.Id == subscriptionDeliveryId && x.IsReplacement, cancellationToken);

    private async Task<Subscription> LoadSubscriptionForReplacementAsync(
        long sourceDeliveryId,
        bool asNoTracking,
        CancellationToken cancellationToken)
    {
        IQueryable<Subscription> query = dbContext.Subscriptions
            .Include(x => x.Deliveries)
            .Include(x => x.Schedules)
            .Include(x => x.Branch);
        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(
                x => x.Deliveries.Any(d => d.Id == sourceDeliveryId),
                cancellationToken)
            ?? throw new ConflictException("The failed delivery no longer belongs to a subscription.");
    }

    /// <summary>
    /// D1-D: deterministic proposal — the first date after the approval day
    /// satisfying weekday schedule (when schedules exist), cutoff, active
    /// branch, and no date conflict. May fall after <c>EndDate</c> (the term
    /// itself is never extended). Bounded to one year, failing closed.
    /// D1-D slice 2.1: there is no vacation-range entity — persisted
    /// vacation/skip state is a <c>Skipped</c> occurrence row — so the date
    /// conflict exclusion is the enforceable vacation/skip rule here: a date
    /// already occupied (including by a vacation/skipped occurrence) is never
    /// proposed. The replacement itself is always created <c>Scheduled</c>,
    /// and ordinary skip/vacation of a replacement is rejected elsewhere,
    /// so it can never be silently skipped.
    /// </summary>
    private static DateOnly ProposeReplacementDate(
        Subscription subscription,
        DateTime now,
        TimeSpan cutoff)
    {
        const int horizonDays = 365;
        var weekdays = Weekdays(subscription);
        var start = DateOnly.FromDateTime(now).AddDays(1);
        for (var offset = 0; offset < horizonDays; offset++)
        {
            var candidate = start.AddDays(offset);
            if (IsEligibleReplacementDate(subscription, candidate, now, cutoff, weekdays))
            {
                return candidate;
            }
        }

        throw new ConflictException("No eligible replacement date exists within a year of approval.");
    }

    private static DateOnly ValidateStaffReplacementDate(
        Subscription subscription,
        DateOnly staffDate,
        DateTime now,
        TimeSpan cutoff)
    {
        // D1-D slice 2.1: evaluated inside the authoritative serializable
        // approval transaction, so a vacation/skip committed after the
        // advisory proposal is still caught here — never silently created.
        var occupant = subscription.Deliveries.FirstOrDefault(x => x.ScheduledDate == staffDate);
        if (occupant is not null)
        {
            if (occupant.Status == SubscriptionDeliveryStatus.Skipped)
            {
                throw new ValidationAppException(
                    "The replacement date falls on a vacation or skipped delivery. Request a new proposal and approve an alternative eligible date.",
                    "replacementDate");
            }

            throw new ValidationAppException(
                "The replacement date conflicts with an existing delivery.", "replacementDate");
        }

        var weekdays = Weekdays(subscription);
        if (weekdays.Count > 0 && !weekdays.Contains(staffDate.DayOfWeek))
        {
            throw new ValidationAppException(
                "The replacement date must fall on a scheduled delivery weekday.", "replacementDate");
        }

        if (!subscription.Branch.IsActive)
        {
            throw new ValidationAppException(
                "The branch is not active for the replacement date.", "replacementDate");
        }

        if (now > DeliveryStart(staffDate) - cutoff)
        {
            throw new ValidationAppException(
                "The replacement cutoff has passed for this date.", "replacementDate");
        }

        return staffDate;
    }

    private static bool IsEligibleReplacementDate(
        Subscription subscription,
        DateOnly candidate,
        DateTime now,
        TimeSpan cutoff,
        HashSet<DayOfWeek> weekdays) =>
        !subscription.Deliveries.Any(x => x.ScheduledDate == candidate)
        && (weekdays.Count == 0 || weekdays.Contains(candidate.DayOfWeek))
        && subscription.Branch.IsActive
        && now <= DeliveryStart(candidate) - cutoff;

    private static HashSet<DayOfWeek> Weekdays(Subscription subscription) =>
        subscription.Schedules.Select(x => x.DayOfWeek).ToHashSet();

    private static DateTime DeliveryStart(DateOnly date) =>
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);

    /// <summary>
    /// D1-D: reuses the existing skip/pause cutoff configuration
    /// (<c>Subscription.SkipPauseCutoffHours</c>, default 24h). No new
    /// per-day capacity model is introduced.
    /// </summary>
    private async Task<TimeSpan> GetReplacementCutoffAsync(CancellationToken cancellationToken)
    {
        var configuration = await dbContext.SystemConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == "Subscription.SkipPauseCutoffHours", cancellationToken);
        if (configuration is null
            || !double.TryParse(configuration.Value, out var hours)
            || hours < 0)
        {
            return TimeSpan.FromHours(24);
        }

        return TimeSpan.FromHours(hours);
    }

    private static string? ResolveReplacementReason(string? remarks, RefundReplacementRequest entity)
    {
        var trimmed = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
        if (trimmed is not null
            && trimmed.Length > SubscriptionDelivery.MaximumReplacementReasonLength)
        {
            throw new ValidationAppException(
                $"Remarks cannot exceed {SubscriptionDelivery.MaximumReplacementReasonLength} characters when approving a re-delivery.",
                "remarks");
        }

        return trimmed ?? entity.FailureReasonSnapshot;
    }

    private IQueryable<RefundReplacementRequest> BaseQuery(bool asNoTracking = false)
    {
        IQueryable<RefundReplacementRequest> query = dbContext.RefundReplacementRequests
            .Include(x => x.Order)
            .Include(x => x.SubscriptionDelivery)
            .Include(x => x.Delivery)
            .Include(x => x.Customer)
            .Include(x => x.Branch)
            .Include(x => x.MilkTest)
            .Include(x => x.DecidedByUser)
            .Include(x => x.CompletedByUser)
            .Include(x => x.Images)
            .Include(x => x.SupersedesRequest)
            .Include(x => x.RemediationRefunds);

        return asNoTracking ? query.AsNoTracking() : query;
    }

    private static void EnsureStaffAccess(RefundReplacementActor actor, RefundReplacementRequest request)
    {
        if (!actor.HasGlobalAccess && !actor.BranchIds.Contains(request.BranchId))
        {
            // Out-of-scope requests are reported as missing, never as forbidden.
            throw new NotFoundException("The refund/replacement request was not found.");
        }
    }

    private void AddCustomerEvent(
        RefundReplacementRequest request,
        string eventType,
        string eventKey,
        string message,
        DateTime occurredAt)
    {
        var variables = new Dictionary<string, string>
        {
            ["requestId"] = request.PublicId.ToString(),
            ["requestNumber"] = request.RequestNumber,
            ["type"] = request.Type.ToString(),
            ["status"] = request.Status.ToString(),
            ["message"] = message
        };

        notificationEventWriter.Add(new NotificationEventRequest(
            request.CustomerId,
            eventType,
            eventKey,
            variables,
            $"/refund-replacements/{request.PublicId}",
            occurredAt));
    }

    private void AddAudit(
        long userId,
        string action,
        Guid requestId,
        object? oldValue,
        object? newValue,
        string? reason,
        DateTime createdAt) =>
        dbContext.AddAuditLog(new AuditLog(
            userId,
            action,
            EntityType,
            requestId.ToString(),
            oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            newValue is null ? null : JsonSerializer.Serialize(newValue),
            null,
            null,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            createdAt));

    private async Task ExecuteSerializableAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await operation();
            return;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static void Mutate(Action operation)
    {
        try
        {
            operation();
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }
        catch (InvalidOperationException exception)
        {
            throw new ConflictException(exception.Message);
        }
    }

    private static string ValidateImage(string contentType, long fileSize) =>
        RefundReplacementProofImages.ValidateContent(contentType, fileSize);

    private static string ValidateFileName(string fileName) =>
        RefundReplacementProofImages.ValidateFileName(fileName);

    private static string ImageExtension(string contentType) =>
        RefundReplacementProofImages.Extension(contentType);

    private static void ValidateText(string? value, string field, int maximumLength, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationAppException($"{field} is required.", field);
        }

        if (value?.Trim().Length > maximumLength)
        {
            throw new ValidationAppException(
                $"{field} cannot exceed {maximumLength} characters.",
                field);
        }
    }

    /// <summary>
    /// D1-B: fans the recorded choice out to branch Admin / Dairy Manager /
    /// Support staff (global assignments included). One deterministic event per
    /// recipient keeps retries free of duplicate notifications.
    /// </summary>
    private async Task AddStaffChoiceEventsAsync(
        RefundReplacementRequest request,
        CustomerChoice choice,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        var staffRoleCodes = new[]
        {
            AuthorizationCodes.Owner,
            AuthorizationCodes.SystemAdmin,
            AuthorizationCodes.DairyManager,
            AuthorizationCodes.CustomerSupport
        };
        var recipientIds = await dbContext.UserRoles
            .AsNoTracking()
            .Where(x => (x.BranchId == request.BranchId || x.BranchId == null)
                && staffRoleCodes.Contains(x.Role.Code)
                && x.Role.IsActive
                && x.User.IsActive)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var deliveryNumber = request.Delivery.DeliveryNumber ?? request.Delivery.ReferenceNumber;
        foreach (var userId in recipientIds)
        {
            notificationEventWriter.Add(new NotificationEventRequest(
                userId,
                NotificationEventTypes.RefundReplacementChoiceRecorded,
                $"refund-replacement:{request.PublicId:N}:choice:{choice}:staff:{userId}",
                new Dictionary<string, string>
                {
                    ["requestId"] = request.PublicId.ToString(),
                    ["requestNumber"] = request.RequestNumber,
                    ["deliveryNumber"] = deliveryNumber,
                    ["choice"] = choice.ToString(),
                    ["customerName"] = request.Customer.DisplayName ?? request.Delivery.CustomerNameSnapshot,
                    ["message"] = $"Customer chose {choice} for delivery {deliveryNumber} ({request.RequestNumber})."
                },
                $"/staff/refund-replacements/{request.PublicId}",
                occurredAt));
        }
    }

    private async Task SaveChangesWithConcurrencyGuardAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConflictException(
                $"The request was changed by another operation. Refresh and try again. ({exception.GetType().Name})");
        }
    }

    /// <summary>
    /// D1-D slice 6.1: the persisted replacement routed from an approved
    /// re-delivery ticket, resolved through the stored
    /// <c>SubscriptionDelivery.RefundReplacementRequestId</c> link — never
    /// from client input and never inferred from the original failed
    /// occurrence. <c>DeliveryNumber</c> is the operational delivery's public
    /// DELIVERY-series reference when the replacement has materialized, else
    /// null (never invented).
    /// </summary>
    private sealed record ReplacementSummary(
        Guid OccurrencePublicId,
        DateOnly ScheduledDate,
        string? DeliveryNumber);

    /// <summary>
    /// D1-D slice 6.1: batch-resolves persisted replacements for the given
    /// tickets in one read-only round trip (occurrence rows plus their
    /// operational delivery numbers via the unique
    /// <c>Delivery.SubscriptionDeliveryId</c> link). Tickets without an
    /// approved re-delivery simply have no entry.
    /// </summary>
    private async Task<Dictionary<long, ReplacementSummary>> LoadPersistedReplacementsAsync(
        IReadOnlyCollection<long> ticketIds,
        CancellationToken cancellationToken)
    {
        if (ticketIds.Count == 0)
        {
            return new Dictionary<long, ReplacementSummary>();
        }

        var rows = await dbContext.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.IsReplacement
                && x.RefundReplacementRequestId != null
                && ticketIds.Contains(x.RefundReplacementRequestId.Value))
            .Select(x => new
            {
                TicketId = x.RefundReplacementRequestId!.Value,
                x.PublicId,
                x.ScheduledDate,
                DeliveryNumber = dbContext.Deliveries
                    .Where(d => d.SubscriptionDeliveryId == x.Id)
                    .Select(d => d.DeliveryNumber)
                    .SingleOrDefault()
            })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(
            x => x.TicketId,
            x => new ReplacementSummary(x.PublicId, x.ScheduledDate, x.DeliveryNumber));
    }

    private static RefundReplacementRequestResult ToResult(
        RefundReplacementRequest request,
        Order? order,
        Delivery delivery,
        User customer,
        Branch branch,
        MilkTest? milkTest,
        User? decidedBy,
        User? completedBy,
        ReplacementSummary? replacement = null) => new(
        request.PublicId,
        request.RequestNumber,
        request.OrderId,
        order?.PublicId,
        order?.OrderNumber,
        request.SubscriptionDelivery?.PublicId,
        request.FailedAt,
        request.DeliveryFailWindowHoursSnapshot,
        request.DeliveryFailDeductionPercentageSnapshot,
        request.FailureReasonSnapshot,
        delivery.PublicId,
        delivery.DeliveryNumber,
        request.CustomerId,
        string.IsNullOrWhiteSpace(customer.DisplayName)
            ? delivery.CustomerNameSnapshot
            : customer.DisplayName!,
        string.IsNullOrWhiteSpace(customer.Mobile)
            ? delivery.CustomerMobileSnapshot
            : customer.Mobile!,
        request.BranchId,
        branch.Code,
        branch.Name,
        request.MilkTestId,
        milkTest?.PublicId,
        request.Type,
        request.Source,
        request.Status,
        request.CustomerChoice,
        request.CustomerChoiceAt,
        request.Reason,
        request.Remarks,
        request.SubmittedAt,
        request.Deadline,
        request.DecidedByUserId,
        decidedBy?.DisplayName,
        request.DecidedAt,
        request.DecisionRemarks,
        request.CompletedByUserId,
        completedBy?.DisplayName,
        request.CompletedAt,
        request.CompletionRemarks,
        request.ProofImageRequired,
        request.Images
            .OrderBy(x => x.UploadedAt)
            .Select(ToImageResult)
            .ToArray(),
        replacement?.OccurrencePublicId,
        replacement?.ScheduledDate,
        replacement?.DeliveryNumber,
        request.SubscriptionDelivery?.IsReplacement == true,
        request.AppliedDeductionPercentage,
        request.DeductionWaived,
        request.DeductionWaiverReason,
        request.SupersedesRequest?.RequestNumber,
        request.SupersedesRequest?.PublicId,
        request.RemediationRefunds
            .OrderBy(x => x.Id)
            .Select(x => x.RequestNumber)
            .FirstOrDefault(),
        delivery.Status,
        request.SettlementMethod,
        request.ManualBankReservedAmount,
        request.ManualBankReservedCurrency,
        request.ManualBankHolderNameSnapshot,
        request.ManualBankNameSnapshot,
        request.ManualBankIfscSnapshot,
        request.ManualBankAccountLast4Snapshot,
        EmployeeOriginGuard.IsEmployeeOriginated(customer.UserType));

    private static RefundReplacementListItem ToListItem(
        RefundReplacementRequest request,
        DateOnly? replacementDate = null) => new(
        request.PublicId,
        request.RequestNumber,
        request.OrderId,
        request.Order?.PublicId,
        request.Order?.OrderNumber,
        request.SubscriptionDelivery?.PublicId,
        request.FailedAt,
        request.DeliveryFailWindowHoursSnapshot,
        request.DeliveryFailDeductionPercentageSnapshot,
        request.FailureReasonSnapshot,
        request.Delivery.PublicId,
        request.Delivery.DeliveryNumber,
        request.CustomerId,
        string.IsNullOrWhiteSpace(request.Customer.DisplayName)
            ? request.Delivery.CustomerNameSnapshot
            : request.Customer.DisplayName!,
        string.IsNullOrWhiteSpace(request.Customer.Mobile)
            ? request.Delivery.CustomerMobileSnapshot
            : request.Customer.Mobile!,
        request.BranchId,
        request.Branch.Code,
        request.Branch.Name,
        request.MilkTestId,
        request.MilkTest?.PublicId,
        request.Type,
        request.Source,
        request.Status,
        request.CustomerChoice,
        request.CustomerChoiceAt,
        request.Reason,
        request.Remarks,
        request.SubmittedAt,
        request.Deadline,
        request.ProofImageRequired,
        request.Images.Count,
        replacementDate,
        request.SubscriptionDelivery?.IsReplacement == true,
        request.AppliedDeductionPercentage,
        request.DeductionWaived,
        request.SupersedesRequest?.RequestNumber,
        request.Delivery.Status,
        request.SettlementMethod,
        EmployeeOriginGuard.IsEmployeeOriginated(request.Customer.UserType));

    private static RefundReplacementImageResult ToImageResult(RefundReplacementImage image) => new(
        image.PublicId,
        image.FileName,
        image.ContentType,
        image.FileSize,
        image.UploadedAt);
}
