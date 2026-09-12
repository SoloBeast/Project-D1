using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Orders;

namespace DoodhDirect.Domain.RefundReplacement;

public enum RefundReplacementType
{
    Refund,
    Replacement
}

public enum RefundReplacementStatus
{
    Pending,
    Approved,
    Rejected,
    Completed
}

/// <summary>
/// Origin of a refund/replacement request.
/// <see cref="MilkTestRejected"/> is the special flow opened immediately when a
/// customer rejects a performed milk test during delivery; it is exempt from the
/// normal post-delivery request window and does not require a proof image.
/// </summary>
public enum RefundReplacementSource
{
    CustomerPostDelivery,
    MilkTestRejected
}

public sealed class RefundReplacementRequest : AuditableEntity
{
    private RefundReplacementRequest() { }

    public RefundReplacementRequest(
        long orderId,
        long deliveryId,
        long customerId,
        long branchId,
        long? milkTestId,
        RefundReplacementType type,
        RefundReplacementSource source,
        string reason,
        string? remarks,
        DateTime submittedAt,
        DateTime? deadline)
    {
        if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));
        if (deliveryId <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryId));
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (branchId <= 0) throw new ArgumentOutOfRangeException(nameof(branchId));
        if (milkTestId is <= 0) throw new ArgumentOutOfRangeException(nameof(milkTestId));
        if (source == RefundReplacementSource.MilkTestRejected && milkTestId is null)
        {
            throw new ArgumentException(
                "A milk-test reference is required for requests originating from a milk-test rejection.",
                nameof(milkTestId));
        }
        if (source != RefundReplacementSource.MilkTestRejected && deadline is null)
        {
            throw new ArgumentException(
                "A deadline is required for post-delivery requests.",
                nameof(deadline));
        }
        EnsureIndiaLocal(submittedAt, nameof(submittedAt));
        if (deadline is not null) EnsureIndiaLocal(deadline.Value, nameof(deadline));

        OrderId = orderId;
        DeliveryId = deliveryId;
        CustomerId = customerId;
        BranchId = branchId;
        MilkTestId = milkTestId;
        Type = type;
        Source = source;
        Reason = Required(reason, nameof(reason));
        Remarks = Optional(remarks);
        SubmittedAt = submittedAt;
        Deadline = deadline;
        Status = RefundReplacementStatus.Pending;
    }

    public string RequestNumber { get; private set; } = string.Empty;
    public long OrderId { get; private set; }
    public long DeliveryId { get; private set; }
    public long CustomerId { get; private set; }
    public long BranchId { get; private set; }
    public long? MilkTestId { get; private set; }
    public RefundReplacementType Type { get; private set; }
    public RefundReplacementSource Source { get; private set; }
    public RefundReplacementStatus Status { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string? Remarks { get; private set; }
    public DateTime SubmittedAt { get; private set; }
    public DateTime? Deadline { get; private set; }
    public long? DecidedByUserId { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public string? DecisionRemarks { get; private set; }
    public long? CompletedByUserId { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? CompletionRemarks { get; private set; }

    /// <summary>
    /// Requests opened from a milk-test rejection are exempt from the proof
    /// image requirement (the milk test itself is the evidence).
    /// </summary>
    public bool ProofImageRequired => Source != RefundReplacementSource.MilkTestRejected;

    public Order Order { get; private set; } = null!;
    public Delivery Delivery { get; private set; } = null!;
    public MilkTest? MilkTest { get; private set; }
    public User Customer { get; private set; } = null!;
    public Branch Branch { get; private set; } = null!;
    public User? DecidedByUser { get; private set; }
    public User? CompletedByUser { get; private set; }
    public ICollection<RefundReplacementImage> Images { get; private set; } = [];

    public void AssignRequestNumber(string requestNumber)
    {
        if (string.IsNullOrWhiteSpace(requestNumber))
        {
            throw new ArgumentException("A request number is required.", nameof(requestNumber));
        }

        if (!string.IsNullOrEmpty(RequestNumber))
        {
            throw new InvalidOperationException("The request number has already been assigned.");
        }

        RequestNumber = requestNumber.Trim();
    }

    public void AddImage(RefundReplacementImage image)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        if (image.RequestId != Id)
        {
            throw new InvalidOperationException("The image belongs to a different request.");
        }

        Images.Add(image);
    }

    public void RemoveImage(Guid publicId)
    {
        var image = Images.FirstOrDefault(x => x.PublicId == publicId);
        if (image is null)
        {
            throw new InvalidOperationException("The image does not belong to this request.");
        }

        Images.Remove(image);
    }

    public void Approve(long decidedByUserId, DateTime decidedAt, string? remarks)
    {
        if (decidedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(decidedByUserId));
        EnsureIndiaLocal(decidedAt, nameof(decidedAt));
        EnsurePending("approved");

        DecidedByUserId = decidedByUserId;
        DecidedAt = decidedAt;
        DecisionRemarks = Optional(remarks);
        Status = RefundReplacementStatus.Approved;
    }

    public void Reject(long decidedByUserId, DateTime decidedAt, string? remarks)
    {
        if (decidedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(decidedByUserId));
        EnsureIndiaLocal(decidedAt, nameof(decidedAt));
        EnsurePending("rejected");

        DecidedByUserId = decidedByUserId;
        DecidedAt = decidedAt;
        DecisionRemarks = Optional(remarks);
        Status = RefundReplacementStatus.Rejected;
    }

    public void Complete(long completedByUserId, DateTime completedAt, string? remarks)
    {
        if (completedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(completedByUserId));
        EnsureIndiaLocal(completedAt, nameof(completedAt));
        if (Status != RefundReplacementStatus.Approved)
        {
            throw new InvalidOperationException("Only an approved request can be completed.");
        }

        CompletedByUserId = completedByUserId;
        CompletedAt = completedAt;
        CompletionRemarks = Optional(remarks);
        Status = RefundReplacementStatus.Completed;
    }

    private void EnsurePending(string operation)
    {
        if (Status != RefundReplacementStatus.Pending)
        {
            throw new InvalidOperationException($"Only a pending request can be {operation}.");
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                parameterName);
        }
    }
}

public sealed class RefundReplacementImage : PublicEntity
{
    private RefundReplacementImage() { }

    public RefundReplacementImage(
        long requestId,
        string storageKey,
        string fileName,
        string contentType,
        long fileSize,
        long uploadedByUserId,
        DateTime uploadedAt)
    {
        if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        if (fileSize <= 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
        if (uploadedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(uploadedByUserId));
        EnsureIndiaLocal(uploadedAt, nameof(uploadedAt));

        RequestId = requestId;
        StorageKey = Required(storageKey, nameof(storageKey));
        FileName = Required(fileName, nameof(fileName));
        ContentType = Required(contentType, nameof(contentType)).ToLowerInvariant();
        FileSize = fileSize;
        UploadedByUserId = uploadedByUserId;
        UploadedAt = uploadedAt;
    }

    public long RequestId { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long FileSize { get; private set; }
    public long UploadedByUserId { get; private set; }
    public DateTime UploadedAt { get; private set; }

    public RefundReplacementRequest Request { get; private set; } = null!;
    public User UploadedByUser { get; private set; } = null!;

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                parameterName);
        }
    }
}
