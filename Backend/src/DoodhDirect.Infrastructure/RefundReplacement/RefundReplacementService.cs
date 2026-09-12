using System.Data;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
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
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Customer refund/replacement request workflow.
///
/// The backend is the single source of truth for eligibility and for the
/// post-delivery window: the deadline is computed as
/// <c>Delivery.CompletedAt + configured window hours</c> using the
/// server-authoritative completion timestamp and the India-local clock. The
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
    INotificationEventWriter notificationEventWriter) : IRefundReplacementService
{
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
        DateTime? deadline = delivery.CompletedAt?.AddHours(windowHours);

        string? ineligibleReason = null;
        if (delivery.SourceType != DeliverySourceType.OneTimeOrder || delivery.OrderId is null)
        {
            ineligibleReason =
                "Refund or replacement requests are only available for one-time order deliveries.";
        }
        else if (isMilkTestRejectedFlow is false &&
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

        return new RefundReplacementEligibilityResult(
            IsEligible: ineligibleReason is null,
            IneligibleReason: ineligibleReason,
            WindowHours: windowHours,
            Deadline: deadline,
            HasActiveRequest: hasActiveRequest,
            ProofImageRequired: !isMilkTestRejectedFlow,
            IsMilkTestRejectedFlow: isMilkTestRejectedFlow);
    }

    public async Task<RefundReplacementRequestResult> SubmitAsync(
        RefundReplacementActor actor,
        SubmitRefundReplacementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Reason, "reason", MaximumReasonLength, required: true);
        ValidateText(request.Remarks, "remarks", MaximumRemarksLength, required: false);

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var delivery = await dbContext.Deliveries
                .SingleOrDefaultAsync(
                    x => x.PublicId == request.DeliveryId && x.CustomerId == actor.UserId,
                    cancellationToken) ?? throw new NotFoundException("The delivery was not found.");

            if (delivery.SourceType != DeliverySourceType.OneTimeOrder || delivery.OrderId is null)
            {
                throw new BusinessRuleException(
                    "Refund or replacement requests are only available for one-time order deliveries.");
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
            else if (delivery.Status != DeliveryStatus.Delivered || delivery.CompletedAt is null)
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

            var configuration = await configurationService.GetAsync(cancellationToken);
            var now = timeProvider.Now;

            DateTime? deadline = null;
            if (source == RefundReplacementSource.CustomerPostDelivery)
            {
                // Authoritative completion timestamp + configured window. The window is
                // exclusive: at the deadline the request is refused, which also keeps the
                // stored deadline strictly greater than the submission timestamp.
                var computed = delivery.CompletedAt!.Value.AddHours(configuration.WindowHours);
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

    public async Task<IReadOnlyList<RefundReplacementRequestResult>> ListForCustomerAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken)
    {
        var requests = await BaseQuery(asNoTracking: true)
            .Where(x => x.CustomerId == actor.UserId)
            .OrderByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return requests
            .Select(x => ToResult(
                x,
                x.Order,
                x.Delivery,
                x.Customer,
                x.Branch,
                x.MilkTest,
                x.DecidedByUser,
                x.CompletedByUser))
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

        return request is null
            ? null
            : ToResult(
                request,
                request.Order,
                request.Delivery,
                request.Customer,
                request.Branch,
                request.MilkTest,
                request.DecidedByUser,
                request.CompletedByUser);
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
            .Include(x => x.Delivery)
            .Include(x => x.Customer)
            .Include(x => x.Branch)
            .Include(x => x.MilkTest)
            .Include(x => x.Images)
            .OrderByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new RefundReplacementPageResult(
            items.Select(ToListItem).ToArray(),
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

        return ToResult(
            request,
            request.Order,
            request.Delivery,
            request.Customer,
            request.Branch,
            request.MilkTest,
            request.DecidedByUser,
            request.CompletedByUser);
    }

    public Task<RefundReplacementRequestResult> ApproveAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        DecideAsync(actor, requestId, request, approve: true, cancellationToken);

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
                ?? throw new NotFoundException("The refund/replacement request was not found.");

            EnsureStaffAccess(actor, entity);

            var now = timeProvider.Now;
            var previousStatus = entity.Status;
            Mutate(() => entity.Complete(actor.UserId, now, request.Remarks));

            var completer = await dbContext.Users
                .AsNoTracking()
                .SingleAsync(x => x.Id == actor.UserId, cancellationToken);
            var decider = entity.DecidedByUserId is null
                ? null
                : await dbContext.Users
                    .AsNoTracking()
                    .SingleAsync(x => x.Id == entity.DecidedByUserId.Value, cancellationToken);

            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.COMPLETE",
                entity.PublicId,
                new { Status = previousStatus.ToString() },
                new { Status = entity.Status.ToString(), entity.CompletedAt },
                request.Remarks,
                now);

            AddCustomerEvent(
                entity,
                NotificationEventTypes.RefundReplacementCompleted,
                $"refund-replacement:{entity.PublicId:N}:completed",
                "Your refund/replacement request has been completed.",
                now);

            await dbContext.SaveChangesAsync(cancellationToken);
            result = ToResult(
                entity,
                entity.Order,
                entity.Delivery,
                entity.Customer,
                entity.Branch,
                entity.MilkTest,
                decider,
                completer);
        }, cancellationToken);

        return result!;
    }

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

            if (!entity.ProofImageRequired)
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

                await dbContext.SaveChangesAsync(cancellationToken);
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

            var now = timeProvider.Now;
            var previousStatus = entity.Status;
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

            var decider = await dbContext.Users
                .AsNoTracking()
                .SingleAsync(x => x.Id == actor.UserId, cancellationToken);

            AddAudit(
                actor.UserId,
                approve ? "REFUND_REPLACEMENT.APPROVE" : "REFUND_REPLACEMENT.REJECT",
                entity.PublicId,
                new { Status = previousStatus.ToString() },
                new { Status = entity.Status.ToString(), entity.DecidedAt },
                request.Remarks,
                now);

            AddCustomerEvent(
                entity,
                NotificationEventTypes.RefundReplacementDecided,
                $"refund-replacement:{entity.PublicId:N}:decided:{entity.Status}",
                approve
                    ? "Your refund/replacement request has been approved."
                    : "Your refund/replacement request has been rejected.",
                now);

            await dbContext.SaveChangesAsync(cancellationToken);
            result = ToResult(
                entity,
                entity.Order,
                entity.Delivery,
                entity.Customer,
                entity.Branch,
                entity.MilkTest,
                decider,
                entity.CompletedByUser);
        }, cancellationToken);

        return result!;
    }

    private IQueryable<RefundReplacementRequest> BaseQuery(bool asNoTracking = false)
    {
        IQueryable<RefundReplacementRequest> query = dbContext.RefundReplacementRequests
            .Include(x => x.Order)
            .Include(x => x.Delivery)
            .Include(x => x.Customer)
            .Include(x => x.Branch)
            .Include(x => x.MilkTest)
            .Include(x => x.DecidedByUser)
            .Include(x => x.CompletedByUser)
            .Include(x => x.Images);

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

    private static string ValidateImage(string contentType, long fileSize)
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

    private static string ValidateFileName(string fileName)
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

    private static string ImageExtension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => throw new ValidationAppException("The proof image type is unsupported.", "image")
    };

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

    private static RefundReplacementRequestResult ToResult(
        RefundReplacementRequest request,
        Order order,
        Delivery delivery,
        User customer,
        Branch branch,
        MilkTest? milkTest,
        User? decidedBy,
        User? completedBy) => new(
        request.PublicId,
        request.RequestNumber,
        request.OrderId,
        order.PublicId,
        order.OrderNumber,
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
            .ToArray());

    private static RefundReplacementListItem ToListItem(RefundReplacementRequest request) => new(
        request.PublicId,
        request.RequestNumber,
        request.OrderId,
        request.Order.PublicId,
        request.Order.OrderNumber,
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
        request.Reason,
        request.Remarks,
        request.SubmittedAt,
        request.Deadline,
        request.ProofImageRequired,
        request.Images.Count);

    private static RefundReplacementImageResult ToImageResult(RefundReplacementImage image) => new(
        image.PublicId,
        image.FileName,
        image.ContentType,
        image.FileSize,
        image.UploadedAt);
}
