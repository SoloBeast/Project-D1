using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Common;

namespace DoodhDirect.Domain.MilkTesting;

public enum MilkTestStatus
{
    Requested = 1,
    Completed = 2
}

public enum MilkTestCustomerDecision
{
    Pending = 1,
    Confirmed = 2,
    Rejected = 3
}

public sealed class MilkTest : AuditableEntity
{
    private MilkTest() { }

    public MilkTest(
        long deliveryId,
        long customerId,
        long branchId,
        long requestedByUserId,
        DateTime requestedAt)
    {
        if (deliveryId <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryId));
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (branchId <= 0) throw new ArgumentOutOfRangeException(nameof(branchId));
        if (requestedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(requestedByUserId));
        EnsureIndiaLocal(requestedAt, nameof(requestedAt));

        DeliveryId = deliveryId;
        CustomerId = customerId;
        BranchId = branchId;
        RequestedByUserId = requestedByUserId;
        RequestedAt = requestedAt;
        Status = MilkTestStatus.Requested;
        CustomerDecision = MilkTestCustomerDecision.Pending;
    }

    public long DeliveryId { get; private set; }
    public long CustomerId { get; private set; }
    public long BranchId { get; private set; }
    public long RequestedByUserId { get; private set; }
    public DateTime RequestedAt { get; private set; }
    public MilkTestStatus Status { get; private set; }
    public long? CompletedByUserId { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? StaffRemarks { get; private set; }
    public MilkTestCustomerDecision CustomerDecision { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? RejectedAt { get; private set; }
    public string? CustomerRemarks { get; private set; }

    public Delivery Delivery { get; private set; } = null!;
    public User Customer { get; private set; } = null!;
    public User RequestedByUser { get; private set; } = null!;
    public User? CompletedByUser { get; private set; }
    public ICollection<MilkTestParameter> Parameters { get; private set; } = [];
    public ICollection<MilkTestImage> Images { get; private set; } = [];

    public void AddParameter(string code, string name, decimal value, string unit)
    {
        EnsureRequested();
        var normalizedCode = Required(code, nameof(code)).ToUpperInvariant();
        if (Parameters.Any(x => x.Code == normalizedCode))
        {
            throw new InvalidOperationException($"A reading with code '{normalizedCode}' already exists.");
        }

        Parameters.Add(new MilkTestParameter(Id, normalizedCode, name, value, unit));
    }

    public void AddImage(MilkTestImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        EnsureRequested();
        Images.Add(image);
    }

    public void RemoveImage(Guid publicId)
    {
        EnsureRequested();
        RemoveImageCore(publicId);
    }

    public void AddImageDuringReview(MilkTestImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        EnsureCompletedForDecision();
        Images.Add(image);
    }

    public void RemoveImageDuringReview(Guid publicId)
    {
        EnsureCompletedForDecision();
        RemoveImageCore(publicId);
    }

    private void RemoveImageCore(Guid publicId)
    {
        var image = Images.FirstOrDefault(x => x.PublicId == publicId)
            ?? throw new InvalidOperationException("The test image was not found.");
        Images.Remove(image);
    }

    public void Complete(long completedByUserId, DateTime completedAt, string? remarks)
    {
        EnsureRequested();
        if (completedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(completedByUserId));
        EnsureIndiaLocal(completedAt, nameof(completedAt));
        if (completedAt < RequestedAt)
        {
            throw new ArgumentException("Completion cannot precede the test request.", nameof(completedAt));
        }
        if (Parameters.Count == 0)
        {
            throw new InvalidOperationException("At least one valid reading is required before completing the test.");
        }
        if (Images.Count == 0)
        {
            throw new InvalidOperationException("At least one valid image is required before completing the test.");
        }

        Status = MilkTestStatus.Completed;
        CompletedByUserId = completedByUserId;
        CompletedAt = completedAt;
        StaffRemarks = Optional(remarks);
    }

    public void Confirm(DateTime confirmedAt, string? remarks)
    {
        EnsureCompletedForDecision();
        EnsureIndiaLocal(confirmedAt, nameof(confirmedAt));
        if (confirmedAt < CompletedAt)
        {
            throw new ArgumentException("Confirmation cannot precede test completion.", nameof(confirmedAt));
        }

        CustomerDecision = MilkTestCustomerDecision.Confirmed;
        ConfirmedAt = confirmedAt;
        CustomerRemarks = Optional(remarks);
    }

    public void Reject(DateTime rejectedAt, string? remarks)
    {
        EnsureCompletedForDecision();
        EnsureIndiaLocal(rejectedAt, nameof(rejectedAt));
        if (rejectedAt < CompletedAt)
        {
            throw new ArgumentException("Rejection cannot precede test completion.", nameof(rejectedAt));
        }

        CustomerDecision = MilkTestCustomerDecision.Rejected;
        RejectedAt = rejectedAt;
        CustomerRemarks = Optional(remarks);
    }

    private void EnsureRequested()
    {
        if (Status != MilkTestStatus.Requested)
        {
            throw new InvalidOperationException($"A test in status '{Status}' cannot be changed.");
        }
    }

    private void EnsureCompletedForDecision()
    {
        if (Status != MilkTestStatus.Completed)
        {
            throw new InvalidOperationException("The customer can decide only after the test is completed.");
        }
        if (CustomerDecision != MilkTestCustomerDecision.Pending)
        {
            throw new InvalidOperationException("The customer decision has already been recorded.");
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

public sealed class MilkTestParameter : Entity
{
    private MilkTestParameter() { }

    public MilkTestParameter(long milkTestId, string code, string name, decimal value, string unit)
    {
        if (milkTestId < 0) throw new ArgumentOutOfRangeException(nameof(milkTestId));
        MilkTestId = milkTestId;
        Code = Required(code, nameof(code)).ToUpperInvariant();
        Name = Required(name, nameof(name));
        Value = value;
        Unit = Required(unit, nameof(unit));
    }

    public long MilkTestId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public decimal Value { get; private set; }
    public string Unit { get; private set; } = string.Empty;

    public MilkTest MilkTest { get; private set; } = null!;

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
}

public sealed class MilkTestImage : PublicEntity
{
    private MilkTestImage() { }

    public MilkTestImage(
        long milkTestId,
        string storageKey,
        string fileName,
        string contentType,
        long fileSize,
        long uploadedByUserId,
        DateTime uploadedAt)
    {
        if (milkTestId < 0) throw new ArgumentOutOfRangeException(nameof(milkTestId));
        if (fileSize <= 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
        if (uploadedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(uploadedByUserId));
        if (uploadedAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                nameof(uploadedAt));
        }

        MilkTestId = milkTestId;
        StorageKey = Required(storageKey, nameof(storageKey));
        FileName = Required(fileName, nameof(fileName));
        ContentType = Required(contentType, nameof(contentType)).ToLowerInvariant();
        FileSize = fileSize;
        UploadedByUserId = uploadedByUserId;
        UploadedAt = uploadedAt;
    }

    public long MilkTestId { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long FileSize { get; private set; }
    public long UploadedByUserId { get; private set; }
    public DateTime UploadedAt { get; private set; }

    public MilkTest MilkTest { get; private set; } = null!;
    public User UploadedByUser { get; private set; } = null!;

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
}
