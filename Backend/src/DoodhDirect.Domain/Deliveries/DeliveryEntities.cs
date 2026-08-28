using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Subscriptions;

namespace DoodhDirect.Domain.Deliveries;

public enum DeliverySourceType
{
    OneTimeOrder,
    SubscriptionOccurrence
}

public enum DeliveryStatus
{
    ReadyForAssignment,
    Assigned,
    PickedUp,
    OutForDelivery,
    Arrived,
    Delivered,
    Failed
}

public static class DeliveryFailureReasons
{
    public const string CustomerNotAvailable = "Customer not available";
    public const string AddressNotFound = "Address not found";
    public const string VehicleIssue = "Vehicle issue";
    public const string ProductDamaged = "Product damaged";
    public const string Other = "Other";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CustomerNotAvailable,
        AddressNotFound,
        VehicleIssue,
        ProductDamaged,
        Other
    };
}

public sealed class Delivery : AuditableEntity
{
    private Delivery() { }

    private Delivery(
        DeliverySourceType sourceType,
        long? orderId,
        long? subscriptionDeliveryId,
        long customerId,
        long branchId,
        DateOnly scheduledDate,
        string referenceNumber,
        string customerName,
        string customerMobile,
        string destinationAddress,
        string? deliveryInstructions,
        decimal destinationLatitude,
        decimal destinationLongitude)
    {
        if ((orderId.HasValue ? 1 : 0) + (subscriptionDeliveryId.HasValue ? 1 : 0) != 1)
        {
            throw new ArgumentException("A delivery must reference exactly one source.");
        }
        if (orderId is <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));
        if (subscriptionDeliveryId is <= 0) throw new ArgumentOutOfRangeException(nameof(subscriptionDeliveryId));
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (branchId <= 0) throw new ArgumentOutOfRangeException(nameof(branchId));
        ValidateCoordinates(destinationLatitude, destinationLongitude);

        SourceType = sourceType;
        OrderId = orderId;
        SubscriptionDeliveryId = subscriptionDeliveryId;
        CustomerId = customerId;
        BranchId = branchId;
        ScheduledDate = scheduledDate;
        ReferenceNumber = Required(referenceNumber, nameof(referenceNumber));
        CustomerNameSnapshot = Required(customerName, nameof(customerName));
        CustomerMobileSnapshot = Required(customerMobile, nameof(customerMobile));
        DestinationAddressSnapshot = Required(destinationAddress, nameof(destinationAddress));
        DeliveryInstructionsSnapshot = Optional(deliveryInstructions);
        DestinationLatitude = destinationLatitude;
        DestinationLongitude = destinationLongitude;
        Status = DeliveryStatus.ReadyForAssignment;
    }

    public static Delivery ForOrder(
        long orderId,
        long customerId,
        long branchId,
        DateOnly scheduledDate,
        string referenceNumber,
        string customerName,
        string customerMobile,
        string destinationAddress,
        string? deliveryInstructions,
        decimal destinationLatitude,
        decimal destinationLongitude) =>
        new(
            DeliverySourceType.OneTimeOrder,
            orderId,
            null,
            customerId,
            branchId,
            scheduledDate,
            referenceNumber,
            customerName,
            customerMobile,
            destinationAddress,
            deliveryInstructions,
            destinationLatitude,
            destinationLongitude);

    public static Delivery ForSubscriptionOccurrence(
        long subscriptionDeliveryId,
        long customerId,
        long branchId,
        DateOnly scheduledDate,
        string referenceNumber,
        string customerName,
        string customerMobile,
        string destinationAddress,
        string? deliveryInstructions,
        decimal destinationLatitude,
        decimal destinationLongitude) =>
        new(
            DeliverySourceType.SubscriptionOccurrence,
            null,
            subscriptionDeliveryId,
            customerId,
            branchId,
            scheduledDate,
            referenceNumber,
            customerName,
            customerMobile,
            destinationAddress,
            deliveryInstructions,
            destinationLatitude,
            destinationLongitude);

    public DeliverySourceType SourceType { get; private set; }
    public long? OrderId { get; private set; }
    public long? SubscriptionDeliveryId { get; private set; }
    public long CustomerId { get; private set; }
    public long BranchId { get; private set; }
    public long? AssignedEmployeeId { get; private set; }
    public DateOnly ScheduledDate { get; private set; }
    public string ReferenceNumber { get; private set; } = string.Empty;
    public string? DeliveryNumber { get; private set; }
    public DeliveryStatus Status { get; private set; }
    public string CustomerNameSnapshot { get; private set; } = string.Empty;
    public string CustomerMobileSnapshot { get; private set; } = string.Empty;
    public string DestinationAddressSnapshot { get; private set; } = string.Empty;
    public string? DeliveryInstructionsSnapshot { get; private set; }
    public decimal DestinationLatitude { get; private set; }
    public decimal DestinationLongitude { get; private set; }
    public DateTime? AssignedAt { get; private set; }
    public DateTime? PickedUpAt { get; private set; }
    public DateTime? OutForDeliveryAt { get; private set; }
    public DateTime? ArrivedAt { get; private set; }
    public DateTime? OtpVerifiedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? FailedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public string? Remarks { get; private set; }
    public string? OperationalNotes { get; private set; }
    public decimal? FailureLatitude { get; private set; }
    public decimal? FailureLongitude { get; private set; }
    public bool IsTrackingActive => Status is DeliveryStatus.OutForDelivery or DeliveryStatus.Arrived;

    public Order? Order { get; private set; }
    public SubscriptionDelivery? SubscriptionDelivery { get; private set; }
    public Branch Branch { get; private set; } = null!;
    public User Customer { get; private set; } = null!;
    public User? AssignedEmployee { get; private set; }
    public ICollection<DeliveryAssignment> Assignments { get; private set; } = [];
    public ICollection<DeliveryOtp> Otps { get; private set; } = [];
    public ICollection<DeliveryLocation> Locations { get; private set; } = [];

    public void AssignDeliveryNumber(string deliveryNumber)
    {
        if (string.IsNullOrWhiteSpace(deliveryNumber))
        {
            throw new ArgumentException("A delivery number is required.", nameof(deliveryNumber));
        }

        if (DeliveryNumber is not null)
        {
            throw new InvalidOperationException("The delivery number has already been assigned.");
        }

        DeliveryNumber = deliveryNumber.Trim();
    }

    public DeliveryAssignment Assign(long employeeId, long assignedByUserId, DateTime assignedAt, string? reason)
    {
        if (employeeId <= 0) throw new ArgumentOutOfRangeException(nameof(employeeId));
        if (assignedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(assignedByUserId));
        EnsureIndiaLocal(assignedAt, nameof(assignedAt));
        if (Status is not (DeliveryStatus.ReadyForAssignment or DeliveryStatus.Assigned))
        {
            throw InvalidTransition("assigned or reassigned");
        }
        if (AssignedEmployeeId == employeeId)
        {
            throw new InvalidOperationException("The delivery is already assigned to this employee.");
        }

        var assignment = new DeliveryAssignment(AssignedEmployeeId, employeeId, assignedByUserId, assignedAt, reason);
        AssignedEmployeeId = employeeId;
        AssignedAt = assignedAt;
        Status = DeliveryStatus.Assigned;
        Assignments.Add(assignment);
        return assignment;
    }

    public void PickUp(long employeeId, DateTime pickedUpAt, string? operationalNotes)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureStatus(DeliveryStatus.Assigned, "picked up");
        EnsureIndiaLocal(pickedUpAt, nameof(pickedUpAt));
        Status = DeliveryStatus.PickedUp;
        PickedUpAt = pickedUpAt;
        OperationalNotes = Optional(operationalNotes);
    }

    public void Start(long employeeId, DateTime startedAt)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureStatus(DeliveryStatus.PickedUp, "started");
        EnsureIndiaLocal(startedAt, nameof(startedAt));
        Status = DeliveryStatus.OutForDelivery;
        OutForDeliveryAt = startedAt;
    }

    public void Arrive(long employeeId, DateTime arrivedAt)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureStatus(DeliveryStatus.OutForDelivery, "marked as arrived");
        EnsureIndiaLocal(arrivedAt, nameof(arrivedAt));
        Status = DeliveryStatus.Arrived;
        ArrivedAt = arrivedAt;
    }

    public void RecordOtpVerified(long employeeId, DateTime verifiedAt)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureStatus(DeliveryStatus.Arrived, "OTP verified");
        EnsureIndiaLocal(verifiedAt, nameof(verifiedAt));
        OtpVerifiedAt = verifiedAt;
    }

    public void Complete(long employeeId, DateTime completedAt, string? remarks)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureStatus(DeliveryStatus.Arrived, "completed");
        EnsureIndiaLocal(completedAt, nameof(completedAt));
        if (!OtpVerifiedAt.HasValue)
        {
            throw new InvalidOperationException("Delivery OTP verification is required before completion.");
        }

        Status = DeliveryStatus.Delivered;
        CompletedAt = completedAt;
        Remarks = Optional(remarks);
    }

    public void Fail(
        long employeeId,
        DateTime failedAt,
        string reason,
        string? remarks,
        decimal? latitude,
        decimal? longitude)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureIndiaLocal(failedAt, nameof(failedAt));
        if (Status is DeliveryStatus.Delivered or DeliveryStatus.Failed or DeliveryStatus.ReadyForAssignment)
        {
            throw InvalidTransition("failed");
        }
        var normalizedReason = reason?.Trim() ?? string.Empty;
        if (!DeliveryFailureReasons.Allowed.Contains(normalizedReason))
        {
            throw new ArgumentException("The failure reason is not supported.", nameof(reason));
        }
        if (latitude.HasValue != longitude.HasValue)
        {
            throw new ArgumentException("Failure latitude and longitude must be supplied together.");
        }
        if (latitude.HasValue)
        {
            ValidateCoordinates(latitude.Value, longitude!.Value);
        }

        Status = DeliveryStatus.Failed;
        FailedAt = failedAt;
        FailureReason = DeliveryFailureReasons.Allowed.Single(x => x.Equals(normalizedReason, StringComparison.OrdinalIgnoreCase));
        Remarks = Optional(remarks);
        FailureLatitude = latitude;
        FailureLongitude = longitude;
    }

    public void EnsureCanRecordLocation(long employeeId)
    {
        EnsureAssignedEmployee(employeeId);
        if (!IsTrackingActive)
        {
            throw new InvalidOperationException("Location can only be recorded while delivery tracking is active.");
        }
    }

    private void EnsureAssignedEmployee(long employeeId)
    {
        if (AssignedEmployeeId != employeeId)
        {
            throw new InvalidOperationException("Only the currently assigned employee can operate this delivery.");
        }
    }

    private void EnsureStatus(DeliveryStatus expected, string operation)
    {
        if (Status != expected)
        {
            throw InvalidTransition(operation);
        }
    }

    private InvalidOperationException InvalidTransition(string operation) =>
        new($"A delivery in status '{Status}' cannot be {operation}.");

    private static void ValidateCoordinates(decimal latitude, decimal longitude)
    {
        if (latitude is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(longitude));
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

public sealed class DeliveryAssignment : Entity
{
    private DeliveryAssignment() { }

    internal DeliveryAssignment(
        long? previousEmployeeId,
        long employeeId,
        long assignedByUserId,
        DateTime assignedAt,
        string? reason)
    {
        if (assignedAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                nameof(assignedAt));
        }

        PreviousEmployeeId = previousEmployeeId;
        EmployeeId = employeeId;
        AssignedByUserId = assignedByUserId;
        AssignedAt = assignedAt;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    public long DeliveryId { get; private set; }
    public long? PreviousEmployeeId { get; private set; }
    public long EmployeeId { get; private set; }
    public long AssignedByUserId { get; private set; }
    public DateTime AssignedAt { get; private set; }
    public string? Reason { get; private set; }

    public Delivery Delivery { get; private set; } = null!;
    public User? PreviousEmployee { get; private set; }
    public User Employee { get; private set; } = null!;
    public User AssignedByUser { get; private set; } = null!;
}

public sealed class DeliveryOtp : PublicEntity
{
    private DeliveryOtp() { }

    public DeliveryOtp(
        long deliveryId,
        string codeHash,
        DateTime expiresAt,
        int maximumAttempts,
        DateTime createdAt,
        string? protectedCode = null)
    {
        if (deliveryId <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryId));
        if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (expiresAt.Kind != DateTimeKind.Unspecified || createdAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("OTP timestamps must be India-local with an unspecified DateTime kind.");
        }
        if (expiresAt <= createdAt) throw new ArgumentOutOfRangeException(nameof(expiresAt));

        DeliveryId = deliveryId;
        CodeHash = string.IsNullOrWhiteSpace(codeHash)
            ? throw new ArgumentException("A code hash is required.", nameof(codeHash))
            : codeHash;
        ProtectedCode = string.IsNullOrWhiteSpace(protectedCode) ? null : protectedCode.Trim();
        ExpiresAt = expiresAt;
        MaximumAttempts = maximumAttempts;
        CreatedAt = createdAt;
    }

    public long DeliveryId { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public string? ProtectedCode { get; private set; }
    public DateTime? SentAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public int AttemptCount { get; private set; }
    public int MaximumAttempts { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ConsumedAt { get; private set; }
    public Delivery Delivery { get; private set; } = null!;

    public void EnsureVerifiable(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        if (ConsumedAt.HasValue) throw new InvalidOperationException("The delivery OTP has already been consumed.");
        if (AttemptCount >= MaximumAttempts) throw new InvalidOperationException("The delivery OTP attempt limit has been reached.");
    }

    public void RecordFailedAttempt(DateTime indiaLocalNow)
    {
        EnsureVerifiable(indiaLocalNow);
        AttemptCount++;
    }

    public void MarkSent(DateTime indiaLocalAt)
    {
        if (indiaLocalAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                nameof(indiaLocalAt));
        }
        SentAt ??= indiaLocalAt;
    }

    public void Consume(DateTime indiaLocalNow)
    {
        EnsureVerifiable(indiaLocalNow);
        ConsumedAt = indiaLocalNow;
        ProtectedCode = null;
    }

    public void Invalidate(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        ConsumedAt ??= indiaLocalNow;
        ProtectedCode = null;
    }

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

public sealed class DeliveryLocation : Entity
{
    private DeliveryLocation() { }

    public DeliveryLocation(
        long deliveryId,
        long employeeId,
        decimal latitude,
        decimal longitude,
        decimal? accuracyMetres,
        DateTime recordedAt)
    {
        if (deliveryId <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryId));
        if (employeeId <= 0) throw new ArgumentOutOfRangeException(nameof(employeeId));
        if (latitude is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(longitude));
        if (accuracyMetres is < 0) throw new ArgumentOutOfRangeException(nameof(accuracyMetres));
        if (recordedAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                nameof(recordedAt));
        }

        DeliveryId = deliveryId;
        EmployeeId = employeeId;
        Latitude = latitude;
        Longitude = longitude;
        AccuracyMetres = accuracyMetres;
        RecordedAt = recordedAt;
    }

    public long DeliveryId { get; private set; }
    public long EmployeeId { get; private set; }
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public decimal? AccuracyMetres { get; private set; }
    public DateTime RecordedAt { get; private set; }
    public Delivery Delivery { get; private set; } = null!;
    public User Employee { get; private set; } = null!;
}
