using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Subscriptions;

namespace DoodhDirect.Application.Deliveries;

public sealed record DeliveryActor(
    long UserId,
    IReadOnlyCollection<long> BranchIds,
    bool HasGlobalAccess = false);

public sealed record AssignDeliveryRequest(Guid EmployeeId, string? Reason);
public sealed record BulkAssignDeliveriesRequest(
    IReadOnlyCollection<Guid> DeliveryIds,
    Guid EmployeeId,
    string? Reason);
public sealed record BulkAssignDeliveriesResult(
    IReadOnlyList<DeliveryResult> Deliveries);
public sealed record DeliveryOrderSummary(
    string OrderNumber,
    decimal TotalQuantity,
    decimal TotalAmount,
    IReadOnlyCollection<string> Items);
public sealed record DeliveryNotesRequest(string? Remarks);
public sealed record FailDeliveryRequest(string Reason, string? Remarks, decimal? Latitude, decimal? Longitude);
public sealed record VerifyDeliveryOtpRequest(string Code);
public sealed record DeliveryLocationRequest(decimal Latitude, decimal Longitude, decimal? AccuracyMetres, DateTime RecordedAt);

public sealed record DeliveryLocationResult(
    decimal Latitude,
    decimal Longitude,
    decimal? AccuracyMetres,
    DateTime RecordedAt);

public sealed record DeliveryAssignmentResult(
    Guid EmployeeId,
    string? EmployeeName,
    Guid AssignedByUserId,
    DateTime AssignedAt,
    string? Reason);

public sealed record DeliveryResult(
    Guid DeliveryId,
    DeliverySourceType SourceType,
    string ReferenceNumber,
    DeliveryStatus Status,
    DateOnly ScheduledDate,
    long BranchId,
    Guid CustomerId,
    string CustomerName,
    string CustomerMobile,
    string DestinationAddress,
    string? DeliveryInstructions,
    decimal DestinationLatitude,
    decimal DestinationLongitude,
    Guid? AssignedEmployeeId,
    string? AssignedEmployeeName,
    DateTime? AssignedAt,
    DateTime? PickedUpAt,
    DateTime? OutForDeliveryAt,
    DateTime? ArrivedAt,
    DateTime? OtpVerifiedAt,
    DateTime? CompletedAt,
    DateTime? FailedAt,
    string? FailureReason,
    string? Remarks,
    string? OperationalNotes,
    bool IsTrackingActive,
    DeliveryLocationResult? LatestLocation,
    IReadOnlyCollection<DeliveryAssignmentResult> Assignments,
    SubscriptionDeliverySlot? SubscriptionSlot = null,
    decimal? Quantity = null,
    DeliveryOrderSummary? OrderSummary = null);

public sealed record CustomerDeliveryResult(
    Guid DeliveryId,
    DeliverySourceType SourceType,
    string ReferenceNumber,
    DeliveryStatus Status,
    DateOnly ScheduledDate,
    string DestinationAddress,
    Guid? AssignedEmployeeId,
    string? AssignedEmployeeName,
    bool IsTrackingActive,
    DeliveryLocationResult? LatestLocation,
    DateTime? CompletedAt,
    DateTime? FailedAt,
    string? FailureReason,
    string? ActiveOtp = null);

public sealed record DeliveryEmployeeResult(
    Guid EmployeeId,
    string DisplayName,
    long BranchId);

public sealed record DeliveryMaterializationResult(int OrdersCreated, int SubscriptionOccurrencesCreated);

public interface IOneTimeDeliveryCreator
{
    void AddIfMissing(Order order, DateOnly scheduledDate);
    Task IssuePendingOtpsAsync(CancellationToken cancellationToken);
}

public interface IDeliveryRealtimePublisher
{
    Task DeliveryChangedAsync(DeliveryResult delivery, CancellationToken cancellationToken);
    Task LocationChangedAsync(Guid deliveryId, DeliveryLocationResult location, CancellationToken cancellationToken);
}

public interface IDeliveryService : IOneTimeDeliveryCreator
{
    Task<DeliveryMaterializationResult> MaterializeEligibleAsync(
        DeliveryActor actor,
        DateOnly throughDate,
        CancellationToken cancellationToken);

    Task<DeliveryMaterializationResult> FetchSubscriptionDeliveriesAsync(
        DeliveryActor actor,
        DateOnly throughDate,
        SubscriptionDeliverySlot? slot,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DeliveryResult>> GetTodayForStaffAsync(
        DeliveryActor actor,
        DateOnly date,
        DeliveryStatus? status,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DeliveryResult>> GetForBranchAsync(
        DeliveryActor actor,
        long branchId,
        DateOnly? date,
        DeliveryStatus? status,
        DeliverySourceType? sourceType,
        SubscriptionDeliverySlot? slot,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DeliveryEmployeeResult>> GetEmployeesAsync(
        DeliveryActor actor,
        long branchId,
        CancellationToken cancellationToken);

    Task<DeliveryResult> GetForOperationsAsync(
        DeliveryActor actor,
        Guid deliveryId,
        bool requireAssignment,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerDeliveryResult>> GetForCustomerAsync(
        long customerId,
        CancellationToken cancellationToken);

    Task<CustomerDeliveryResult> GetForCustomerAsync(
        long customerId,
        Guid deliveryId,
        CancellationToken cancellationToken);

    Task<DeliveryResult> AssignAsync(
        DeliveryActor actor,
        Guid deliveryId,
        AssignDeliveryRequest request,
        CancellationToken cancellationToken);

    Task<BulkAssignDeliveriesResult> BulkAssignAsync(
        DeliveryActor actor,
        BulkAssignDeliveriesRequest request,
        CancellationToken cancellationToken);

    Task<DeliveryResult> PickUpAsync(
        DeliveryActor actor,
        Guid deliveryId,
        DeliveryNotesRequest request,
        CancellationToken cancellationToken);

    Task<DeliveryResult> StartAsync(DeliveryActor actor, Guid deliveryId, CancellationToken cancellationToken);
    Task<DeliveryResult> ArriveAsync(DeliveryActor actor, Guid deliveryId, CancellationToken cancellationToken);

    Task<DeliveryResult> VerifyOtpAsync(
        DeliveryActor actor,
        Guid deliveryId,
        VerifyDeliveryOtpRequest request,
        CancellationToken cancellationToken);

    Task<DeliveryResult> CompleteAsync(
        DeliveryActor actor,
        Guid deliveryId,
        DeliveryNotesRequest request,
        CancellationToken cancellationToken);

    Task<DeliveryResult> FailAsync(
        DeliveryActor actor,
        Guid deliveryId,
        FailDeliveryRequest request,
        CancellationToken cancellationToken);

    Task<DeliveryLocationResult> RecordLocationAsync(
        DeliveryActor actor,
        Guid deliveryId,
        DeliveryLocationRequest request,
        CancellationToken cancellationToken);
}

public sealed class NullDeliveryRealtimePublisher : IDeliveryRealtimePublisher
{
    public Task DeliveryChangedAsync(DeliveryResult delivery, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task LocationChangedAsync(Guid deliveryId, DeliveryLocationResult location, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
