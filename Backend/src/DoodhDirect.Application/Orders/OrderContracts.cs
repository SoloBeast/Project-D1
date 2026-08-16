using DoodhDirect.Domain.Orders;

namespace DoodhDirect.Application.Orders;

public sealed record OrderItemRequest(Guid ProductId, decimal Quantity);

public sealed record CheckoutRequest(Guid AddressId, IReadOnlyCollection<OrderItemRequest> Items);

public sealed record CheckoutLineResult(
    Guid ProductId,
    string Sku,
    string ProductName,
    string UnitOfMeasure,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record CheckoutResult(
    Guid AddressId,
    string AddressLabel,
    string AddressLine1,
    string? AddressLine2,
    string Locality,
    string City,
    string State,
    string PinCode,
    string ContactName,
    string ContactMobile,
    Guid BranchId,
    string BranchCode,
    string BranchName,
    decimal DistanceKm,
    IReadOnlyCollection<CheckoutLineResult> Items,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal PayableAmount);

public sealed record OrderResult(
    Guid PublicId,
    string OrderNumber,
    OrderType Type,
    OrderStatus Status,
    DateTime CreatedAtUtc,
    Guid BranchId,
    string BranchCode,
    string BranchName,
    Guid AddressId,
    string AddressLabel,
    string AddressLine1,
    string? AddressLine2,
    string Locality,
    string City,
    string State,
    string PinCode,
    string? Landmark,
    string? DeliveryInstructions,
    string ContactName,
    string ContactMobile,
    decimal Latitude,
    decimal Longitude,
    IReadOnlyCollection<OrderItemResult> Items,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal PayableAmount,
    DateTime? CancelledAtUtc);

public sealed record OrderItemResult(
    Guid ProductId,
    string Sku,
    string ProductName,
    string UnitOfMeasure,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record BranchAllocationResult(long BranchId, Guid BranchPublicId, string BranchCode, string BranchName, decimal DistanceKm);

public interface IBranchAllocationService
{
    Task<BranchAllocationResult> AllocateAsync(
        decimal latitude,
        decimal longitude,
        IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
        CancellationToken cancellationToken);
}

public interface IOrderService
{
    Task<CheckoutResult> PreviewAsync(long customerId, CheckoutRequest request, CancellationToken cancellationToken);

    Task<OrderResult> CreateAsync(
        long customerId,
        CheckoutRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderResult>> GetForCustomerAsync(long customerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderResult>> GetForAdministrationAsync(CancellationToken cancellationToken);

    Task<OrderResult> GetAsync(long customerId, Guid orderId, bool bypassOwnership, CancellationToken cancellationToken);

    Task<OrderResult> CancelAsync(long customerId, Guid orderId, CancellationToken cancellationToken);
}

public static class OrderMappings
{
    public static OrderItemResult ToResult(this OrderItem item) => new(
        item.Product.PublicId,
        item.SkuSnapshot,
        item.ProductNameSnapshot,
        item.UnitOfMeasureSnapshot,
        item.Quantity,
        item.UnitPrice,
        item.LineTotal);

    public static OrderResult ToResult(this Order order) => new(
        order.PublicId,
        order.OrderNumber,
        order.Type,
        order.Status,
        order.CreatedAtUtc,
        order.Branch.PublicId,
        order.BranchCodeSnapshot,
        order.BranchNameSnapshot,
        order.CustomerAddress.PublicId,
        order.AddressLabelSnapshot,
        order.AddressLine1Snapshot,
        order.AddressLine2Snapshot,
        order.LocalitySnapshot,
        order.CitySnapshot,
        order.StateSnapshot,
        order.PinCodeSnapshot,
        order.LandmarkSnapshot,
        order.DeliveryInstructionsSnapshot,
        order.ContactNameSnapshot,
        order.ContactMobileSnapshot,
        order.LatitudeSnapshot,
        order.LongitudeSnapshot,
        order.Items.Select(item => item.ToResult()).ToArray(),
        order.Subtotal,
        order.DiscountAmount,
        order.PayableAmount,
        order.CancelledAtUtc);
}

public static class OrderValidation
{
    public static void ValidateItems(IReadOnlyCollection<OrderItemRequest>? items)
    {
        if (items is null || items.Count == 0)
        {
            throw new ArgumentException("At least one order item is required.", nameof(items));
        }

        if (items.Any(item => item.ProductId == Guid.Empty))
        {
            throw new ArgumentException("Each order item must include a product.", nameof(items));
        }

        if (items.Any(item => item.Quantity <= 0 || decimal.Round(item.Quantity, 3) != item.Quantity))
        {
            throw new ArgumentException("Quantities must be positive and use at most three decimal places.", nameof(items));
        }

        if (items.Select(item => item.ProductId).Distinct().Count() != items.Count)
        {
            throw new ArgumentException("A product may appear only once in an order.", nameof(items));
        }
    }
}
