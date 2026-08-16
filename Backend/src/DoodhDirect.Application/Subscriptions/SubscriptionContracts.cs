using DoodhDirect.Application.Payments;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;

namespace DoodhDirect.Application.Subscriptions;

public sealed record CreateSubscriptionRequest(
    Guid ProductId,
    Guid AddressId,
    decimal Quantity,
    DateOnly StartDate,
    IReadOnlyCollection<DayOfWeek> DeliveryDays,
    int TotalEntitlement,
    PaymentMethod PaymentMethod);

public sealed record UpdateSubscriptionRequest(
    decimal? Quantity,
    Guid? AddressId,
    IReadOnlyCollection<DayOfWeek>? DeliveryDays);

public sealed record SkipSubscriptionDeliveryRequest(Guid DeliveryId);

public sealed record SubscriptionScheduleResult(DayOfWeek DayOfWeek);

public sealed record SubscriptionDeliveryResult(
    Guid PublicId,
    DateOnly ScheduledDate,
    decimal Quantity,
    SubscriptionDeliveryStatus Status,
    Guid BranchId,
    string BranchCode,
    string BranchName,
    string Address,
    DateTime? StatusChangedAtUtc);

public sealed record SubscriptionResult(
    Guid PublicId,
    SubscriptionStatus Status,
    Guid ProductId,
    string ProductSku,
    string ProductName,
    string UnitOfMeasure,
    decimal Quantity,
    decimal UnitPrice,
    decimal PayableAmount,
    DateOnly StartDate,
    DateOnly EndDate,
    int TotalEntitlement,
    int UsedEntitlement,
    int RemainingEntitlement,
    Guid AddressId,
    string Address,
    Guid BranchId,
    string BranchCode,
    string BranchName,
    IReadOnlyCollection<SubscriptionScheduleResult> Schedules,
    DateTime? ActivatedAtUtc,
    DateTime? PausedAtUtc,
    DateTime? CancelledAtUtc,
    DateTime? CompletedAtUtc,
    DateTime CreatedAtUtc);

public sealed record CreatedSubscriptionResult(
    SubscriptionResult Subscription,
    PaymentResult Payment);

public interface ISubscriptionService
{
    Task<CreatedSubscriptionResult> CreateAsync(
        long customerId,
        CreateSubscriptionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SubscriptionResult>> GetForCustomerAsync(
        long customerId,
        CancellationToken cancellationToken);

    Task<SubscriptionResult> GetAsync(
        long customerId,
        Guid subscriptionId,
        CancellationToken cancellationToken);

    Task<SubscriptionResult> UpdateAsync(
        long customerId,
        Guid subscriptionId,
        UpdateSubscriptionRequest request,
        CancellationToken cancellationToken);

    Task<SubscriptionResult> PauseAsync(
        long customerId,
        Guid subscriptionId,
        CancellationToken cancellationToken);

    Task<SubscriptionResult> ResumeAsync(
        long customerId,
        Guid subscriptionId,
        CancellationToken cancellationToken);

    Task<SubscriptionResult> CancelAsync(
        long customerId,
        Guid subscriptionId,
        CancellationToken cancellationToken);

    Task<SubscriptionDeliveryResult> SkipAsync(
        long customerId,
        Guid subscriptionId,
        SkipSubscriptionDeliveryRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SubscriptionDeliveryResult>> GetCalendarAsync(
        long customerId,
        Guid subscriptionId,
        CancellationToken cancellationToken);

    Task MarkDeliveryFailedAsync(long deliveryId, CancellationToken cancellationToken);

    Task MarkDeliveryDeliveredAsync(long deliveryId, CancellationToken cancellationToken);
}

public static class SubscriptionMappings
{
    public static SubscriptionResult ToResult(this Subscription subscription) => new(
        subscription.PublicId,
        subscription.Status,
        subscription.Product.PublicId,
        subscription.ProductSkuSnapshot,
        subscription.ProductNameSnapshot,
        subscription.UnitOfMeasureSnapshot,
        subscription.Quantity,
        subscription.UnitPrice,
        subscription.PayableAmount,
        subscription.StartDate,
        subscription.EndDate,
        subscription.TotalEntitlement,
        subscription.UsedEntitlement,
        subscription.RemainingEntitlement,
        subscription.CustomerAddress.PublicId,
        subscription.AddressSnapshot,
        subscription.Branch.PublicId,
        subscription.BranchCodeSnapshot,
        subscription.BranchNameSnapshot,
        subscription.Schedules
            .OrderBy(schedule => schedule.DayOfWeek)
            .Select(schedule => new SubscriptionScheduleResult(schedule.DayOfWeek))
            .ToArray(),
        subscription.ActivatedAtUtc,
        subscription.PausedAtUtc,
        subscription.CancelledAtUtc,
        subscription.CompletedAtUtc,
        subscription.CreatedAtUtc);

    public static SubscriptionDeliveryResult ToResult(this SubscriptionDelivery delivery) => new(
        delivery.PublicId,
        delivery.ScheduledDate,
        delivery.Quantity,
        delivery.Status,
        delivery.Branch.PublicId,
        delivery.BranchCodeSnapshot,
        delivery.BranchNameSnapshot,
        delivery.AddressSnapshot,
        delivery.StatusChangedAtUtc);
}
