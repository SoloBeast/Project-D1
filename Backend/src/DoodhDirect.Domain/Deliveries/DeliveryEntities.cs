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
    public DeliveryStatus Status { get; private set; }
    public string CustomerNameSnapshot { get; private set; } = string.Empty;
    public string CustomerMobileSnapshot { get; private set; } = string.Empty;
    public string DestinationAddressSnapshot { get; private set; } = string.Empty;
    public string? DeliveryInstructionsSnapshot { get; private set; }
    public decimal DestinationLatitude { get; private set; }
    public decimal DestinationLongitude { get; private set; }
    public DateTime? AssignedAtUtc { get; private set; }
    public DateTime? PickedUpAtUtc { get; private set; }
    public DateTime? OutForDeliveryAtUtc { get; private set; }
    public DateTime? ArrivedAtUtc { get; private set; }
    public DateTime? OtpVerifiedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime? FailedAtUtc { get; private set; }
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

    public DeliveryAssignment Assign(long employeeId, long assignedByUserId, DateTime utcNow, string? reason)
    {
        if (employeeId <= 0) throw new ArgumentOutOfRangeException(nameof(employeeId));
        if (assignedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(assignedByUserId));
        EnsureUtc(utcNow, nameof(utcNow));
        if (Status is not (DeliveryStatus.ReadyForAssignment or DeliveryStatus.Assigned))
        {
            throw InvalidTransition("assigned or reassigned");
        }
        if (AssignedEmployeeId == employeeId)
        {
            throw new InvalidOperationException("The delivery is already assigned to this employee.");
        }

        var assignment = new DeliveryAssignment(AssignedEmployeeId, employeeId, assignedByUserId, utcNow, reason);
        AssignedEmployeeId = employeeId;
        AssignedAtUtc = utcNow;
        Status = DeliveryStatus.Assigned;
        Assignments.Add(assignment);
        return assignment;
    }

    public void PickUp(long employeeId, DateTime utcNow, string? operationalNotes)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureStatus(DeliveryStatus.Assigned, "picked up");
        EnsureUtc(utcNow, nameof(utcNow));
        Status = DeliveryStatus.PickedUp;
        PickedUpAtUtc = utcNow;
        OperationalNotes = Optional(operationalNotes);
    }

    public void Start(long employeeId, DateTime utcNow)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureStatus(DeliveryStatus.PickedUp, "started");
        EnsureUtc(utcNow, nameof(utcNow));
        Status = DeliveryStatus.OutForDelivery;
        OutForDeliveryAtUtc = utcNow;
    }

    public void Arrive(long employeeId, DateTime utcNow)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureStatus(DeliveryStatus.OutForDelivery, "marked as arrived");
        EnsureUtc(utcNow, nameof(utcNow));
        Status = DeliveryStatus.Arrived;
        ArrivedAtUtc = utcNow;
    }

    public void RecordOtpVerified(long employeeId, DateTime utcNow)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureStatus(DeliveryStatus.Arrived, "OTP verified");
        EnsureUtc(utcNow, nameof(utcNow));
        OtpVerifiedAtUtc = utcNow;
    }

    public void Complete(long employeeId, DateTime utcNow, string? remarks)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureStatus(DeliveryStatus.Arrived, "completed");
        EnsureUtc(utcNow, nameof(utcNow));
        if (!OtpVerifiedAtUtc.HasValue)
        {
            throw new InvalidOperationException("Delivery OTP verification is required before completion.");
        }

        Status = DeliveryStatus.Delivered;
        CompletedAtUtc = utcNow;
        Remarks = Optional(remarks);
    }

    public void Fail(
        long employeeId,
        DateTime utcNow,
        string reason,
        string? remarks,
        decimal? latitude,
        decimal? longitude)
    {
        EnsureAssignedEmployee(employeeId);
        EnsureUtc(utcNow, nameof(utcNow));
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
        FailedAtUtc = utcNow;
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

    private static void EnsureUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
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
        DateTime assignedAtUtc,
        string? reason)
    {
        PreviousEmployeeId = previousEmployeeId;
        EmployeeId = employeeId;
        AssignedByUserId = assignedByUserId;
        AssignedAtUtc = assignedAtUtc;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    public long DeliveryId { get; private set; }
    public long? PreviousEmployeeId { get; private set; }
    public long EmployeeId { get; private set; }
    public long AssignedByUserId { get; private set; }
    public DateTime AssignedAtUtc { get; private set; }
    public string? Reason { get; private set; }

    public Delivery Delivery { get; private set; } = null!;
    public User? PreviousEmployee { get; private set; }
    public User Employee { get; private set; } = null!;
    public User AssignedByUser { get; private set; } = null!;
}

public sealed class DeliveryOtp : PublicEntity
{
    private DeliveryOtp() { }

    public DeliveryOtp(long deliveryId, string codeHash, DateTime expiresAtUtc, int maximumAttempts, DateTime createdAtUtc)
    {
        if (deliveryId <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryId));
        if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (expiresAtUtc.Kind != DateTimeKind.Utc || createdAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("OTP timestamps must be UTC.");
        }
        if (expiresAtUtc <= createdAtUtc) throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));

        DeliveryId = deliveryId;
        CodeHash = string.IsNullOrWhiteSpace(codeHash)
            ? throw new ArgumentException("A code hash is required.", nameof(codeHash))
            : codeHash;
        ExpiresAtUtc = expiresAtUtc;
        MaximumAttempts = maximumAttempts;
        CreatedAtUtc = createdAtUtc;
    }

    public long DeliveryId { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public int AttemptCount { get; private set; }
    public int MaximumAttempts { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }
    public Delivery Delivery { get; private set; } = null!;

    public void EnsureVerifiable(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("Timestamp must be UTC.", nameof(utcNow));
        if (ConsumedAtUtc.HasValue) throw new InvalidOperationException("The delivery OTP has already been consumed.");
        if (utcNow >= ExpiresAtUtc) throw new InvalidOperationException("The delivery OTP has expired.");
        if (AttemptCount >= MaximumAttempts) throw new InvalidOperationException("The delivery OTP attempt limit has been reached.");
    }

    public void RecordFailedAttempt(DateTime utcNow)
    {
        EnsureVerifiable(utcNow);
        AttemptCount++;
    }

    public void Consume(DateTime utcNow)
    {
        EnsureVerifiable(utcNow);
        ConsumedAtUtc = utcNow;
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
        DateTime recordedAtUtc)
    {
        if (deliveryId <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryId));
        if (employeeId <= 0) throw new ArgumentOutOfRangeException(nameof(employeeId));
        if (latitude is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(longitude));
        if (accuracyMetres is < 0) throw new ArgumentOutOfRangeException(nameof(accuracyMetres));
        if (recordedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Timestamp must be UTC.", nameof(recordedAtUtc));

        DeliveryId = deliveryId;
        EmployeeId = employeeId;
        Latitude = latitude;
        Longitude = longitude;
        AccuracyMetres = accuracyMetres;
        RecordedAtUtc = recordedAtUtc;
    }

    public long DeliveryId { get; private set; }
    public long EmployeeId { get; private set; }
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public decimal? AccuracyMetres { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }
    public Delivery Delivery { get; private set; } = null!;
    public User Employee { get; private set; } = null!;
}
