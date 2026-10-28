using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// D1-A shared proof-image rules for delivery-fail tickets. Mirrors the
/// existing refund/replacement proof conventions (JPEG/PNG/WebP, 10 MB):
/// storage keys stay server-side and never appear in API or audit payloads.
/// </summary>
internal static class RefundReplacementProofImages
{
    public const long MaximumImageBytes = 10L * 1024L * 1024L;
    private const int MaximumFileNameLength = 255;

    public static string ValidateContent(string contentType, long fileSize)
    {
        var normalized = contentType.Trim().ToLowerInvariant();
        if (normalized is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            throw new ValidationAppException(
                "The proof image must be a JPEG, PNG or WebP image.",
                "contentType");
        }

        if (fileSize <= 0)
        {
            throw new ValidationAppException("The proof image is empty.", "fileSize");
        }

        if (fileSize > MaximumImageBytes)
        {
            throw new ValidationAppException(
                $"The proof image cannot exceed {MaximumImageBytes / (1024 * 1024)} MB.",
                "fileSize");
        }

        return normalized;
    }

    public static string ValidateFileName(string fileName)
    {
        // Strip any directory component so a hostile client cannot influence the
        // stored key via the original file name.
        var safe = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safe))
        {
            throw new ValidationAppException("A file name is required.", "fileName");
        }

        if (safe.Length > MaximumFileNameLength)
        {
            throw new ValidationAppException(
                $"The file name cannot exceed {MaximumFileNameLength} characters.",
                "fileName");
        }

        return safe;
    }

    public static string Extension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => throw new ValidationAppException("The proof image type is unsupported.", "image")
    };
}

/// <summary>
/// D1-A result of the idempotent delivery-fail ticket step.
/// <see cref="StoredStorageKey"/> is set only when a new proof blob was stored
/// in this call, so the caller can compensating-delete it when the surrounding
/// transaction rolls back. It must never leave this boundary (no API/audit).
/// </summary>
public sealed record DeliveryFailTicketResult(
    RefundReplacementRequest Ticket,
    string? StoredStorageKey,
    bool Created);

public interface IDeliveryFailTicketCreator
{
    /// <summary>
    /// Creates (or reuses, on retry/race) the remedy ticket for an already-failed
    /// subscription delivery. Runs inside the caller's serializable transaction:
    /// it only buffers entities/events/audits — the caller owns SaveChanges and
    /// deletes <see cref="DeliveryFailTicketResult.StoredStorageKey"/> on failure.
    /// Never touches wallet, refund or gateway systems.
    /// <paramref name="noServiceProvided"/> marks a manager-confirmed miss of an
    /// occurrence that was never operationalized: no proof image exists (there
    /// is no doorstep to photograph), the deduction snapshot freezes at zero,
    /// and the ticket records the persisted no-service distinction. Attempted
    /// visits keep the ordinary proof and deduction behavior.
    /// </summary>
    Task<DeliveryFailTicketResult> CreateOrReuseAsync(
        Delivery delivery,
        long? milkTestId,
        RefundReplacementSource source,
        FailDeliveryProof? proof,
        long actorUserId,
        DateTime now,
        CancellationToken cancellationToken,
        bool noServiceProvided = false);
}

public sealed class DeliveryFailTicketCreator(
    DoodhDirectDbContext dbContext,
    IRefundReplacementConfigurationService configurationService,
    INumberSeriesService numberSeriesService,
    IMediaStorage mediaStorage,
    INotificationEventWriter notificationEventWriter) : IDeliveryFailTicketCreator
{
    /// <summary>Numbering series for the human-readable request number.</summary>
    public const string SeriesCode = RefundReplacementService.SeriesCode;

    private const string EntityType = "RefundReplacementRequest";
    private const string TicketCreatedAction = "REFUND_REPLACEMENT.DELIVERY_FAIL_TICKET_CREATED";
    private const string MilkTestLinkedAction = "REFUND_REPLACEMENT.MILK_TEST_LINKED";

    public async Task<DeliveryFailTicketResult> CreateOrReuseAsync(
        Delivery delivery,
        long? milkTestId,
        RefundReplacementSource source,
        FailDeliveryProof? proof,
        long actorUserId,
        DateTime now,
        CancellationToken cancellationToken,
        bool noServiceProvided = false)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.SourceType != DeliverySourceType.SubscriptionOccurrence
            || delivery.SubscriptionDeliveryId is null)
        {
            throw new BusinessRuleException(
                "Delivery-fail tickets are only available for subscription deliveries.");
        }

        if (source is not (RefundReplacementSource.DeliveryFailed or RefundReplacementSource.MilkTestRejected))
        {
            throw new BusinessRuleException("The ticket source is not supported for a delivery failure.");
        }

        if (source == RefundReplacementSource.DeliveryFailed && proof is null && !noServiceProvided)
        {
            throw new ValidationAppException("A proof image is required to fail a subscription delivery.", "proof");
        }

        if (noServiceProvided && source != RefundReplacementSource.DeliveryFailed)
        {
            throw new BusinessRuleException("The no-service distinction applies only to failed-delivery tickets.");
        }

        if (source == RefundReplacementSource.DeliveryFailed && proof is not null && proof.FileSize <= 0)
        {
            throw new ValidationAppException("The proof image is empty.", "proof");
        }

        var existing = await dbContext.RefundReplacementRequests
            .Include(x => x.Images)
            .SingleOrDefaultAsync(
                x => x.DeliveryId == delivery.Id
                    && (x.Status == RefundReplacementStatus.Pending
                        || x.Status == RefundReplacementStatus.Approved
                        || x.Status == RefundReplacementStatus.AwaitingCustomerChoice),
                cancellationToken);
        if (existing is not null)
        {
            // Race guard (staff fail vs. milk-test rejection): exactly one ticket
            // per failed delivery. Link the rejecting test when the surviving
            // ticket is a milk-test ticket still missing it.
            if (milkTestId.HasValue
                && !existing.MilkTestId.HasValue
                && existing.Source == RefundReplacementSource.MilkTestRejected)
            {
                Mutate(() => existing.AttachMilkTest(milkTestId.Value));
                dbContext.AddAuditLog(new AuditLog(
                    actorUserId,
                    MilkTestLinkedAction,
                    EntityType,
                    existing.PublicId.ToString(),
                    null,
                    JsonSerializer.Serialize(new { existing.RequestNumber }),
                    null,
                    null,
                    "Doorstep milk-test rejection linked to the delivery-fail ticket.",
                    now));
            }

            return new DeliveryFailTicketResult(existing, null, false);
        }

        var configuration = await configurationService.GetAsync(cancellationToken);
        var failedAt = delivery.FailedAt ?? now;
        var deadline = failedAt.AddHours(configuration.DeliveryFailWindowHours);
        var failureReason = string.IsNullOrWhiteSpace(delivery.FailureReason)
            ? "Delivery failed"
            : delivery.FailureReason.Trim();
        var deliveryNumber = delivery.DeliveryNumber ?? delivery.ReferenceNumber;

        var entity = new RefundReplacementRequest(
            null,
            delivery.Id,
            delivery.CustomerId,
            delivery.BranchId,
            milkTestId,
            // Placeholder until the customer chooses (a later D1 stage records the
            // Refund / Re-Delivery choice). Status carries the pending choice.
            RefundReplacementType.Refund,
            source,
            failureReason,
            delivery.Remarks,
            now,
            deadline,
            delivery.SubscriptionDeliveryId,
            failedAt,
            configuration.DeliveryFailWindowHours,
            noServiceProvided ? 0m : configuration.DeliveryFailDeductionPercentage,
            failureReason,
            RefundReplacementStatus.AwaitingCustomerChoice,
            noServiceProvided: noServiceProvided);

        var requestNumber = await numberSeriesService.GetNextNumberAsync(
            SeriesCode,
            actorUserId,
            cancellationToken);
        Mutate(() => entity.AssignRequestNumber(requestNumber));
        dbContext.RefundReplacementRequests.Add(entity);
        // Flush inside the caller's transaction (no commit) so the proof image
        // can reference the assigned ticket id. A later failure rolls everything
        // back and the caller compensating-deletes the stored blob.
        await dbContext.SaveChangesAsync(cancellationToken);

        string? storedStorageKey = null;
        if (proof is not null)
        {
            var normalizedContentType = RefundReplacementProofImages.ValidateContent(
                proof.ContentType, proof.FileSize);
            var safeFileName = RefundReplacementProofImages.ValidateFileName(proof.FileName);
            var extension = RefundReplacementProofImages.Extension(normalizedContentType);
            var storageKey =
                $"{now:yyyy/MM}/{entity.BranchId}/{entity.PublicId:N}/{Guid.NewGuid():N}{extension}";

            var stored = await mediaStorage.SaveAsync(
                storageKey,
                proof.Content,
                normalizedContentType,
                cancellationToken);
            if (stored.FileSize != proof.FileSize)
            {
                await mediaStorage.DeleteIfExistsAsync(stored.StorageKey, CancellationToken.None);
                throw new InvalidOperationException(
                    "The stored media size does not match the uploaded image size.");
            }

            storedStorageKey = stored.StorageKey;
            Mutate(() => entity.AddImage(new RefundReplacementImage(
                entity.Id,
                stored.StorageKey,
                safeFileName,
                normalizedContentType,
                stored.FileSize,
                actorUserId,
                now)));
        }

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            TicketCreatedAction,
            EntityType,
            entity.PublicId.ToString(),
            null,
            JsonSerializer.Serialize(new
            {
                entity.RequestNumber,
                DeliveryNumber = deliveryNumber,
                Source = entity.Source.ToString(),
                Status = entity.Status.ToString(),
                entity.Deadline,
                entity.DeliveryFailWindowHoursSnapshot,
                entity.DeliveryFailDeductionPercentageSnapshot,
                entity.FailureReasonSnapshot,
                entity.NoServiceProvided
            }),
            null,
            null,
            failureReason,
            now));

        notificationEventWriter.Add(new NotificationEventRequest(
            delivery.CustomerId,
            NotificationEventTypes.RefundReplacementChoiceRequired,
            $"refund-replacement:{entity.PublicId:N}:choice-required",
            new Dictionary<string, string>
            {
                ["requestId"] = entity.PublicId.ToString(),
                ["requestNumber"] = entity.RequestNumber,
                ["deliveryNumber"] = deliveryNumber,
                ["deadline"] = deadline.ToString("yyyy-MM-dd HH:mm"),
                ["message"] = $"Delivery {deliveryNumber} could not be completed. Choose Refund or Re-Delivery before {deadline:yyyy-MM-dd HH:mm}."
            },
            $"/refund-replacements/{entity.PublicId}",
            now));

        return new DeliveryFailTicketResult(entity, storedStorageKey, true);
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
}
