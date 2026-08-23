using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Domain.Orders;

public enum OrderType
{
    OneTime
}

public enum OrderStatus
{
    PendingPayment,
    Confirmed,
    Assigned,
    OutForDelivery,
    Delivered,
    Failed,
    Cancelled,
    PaymentFailed,
    RejectedByCustomer
}

public sealed class Order : AuditableEntity
{
    private Order() { }

    public Order(
        long customerId,
        long customerAddressId,
        long branchId,
        string idempotencyKey,
        string orderNumber,
        decimal subtotal,
        decimal discountAmount,
        string branchCode,
        string branchName,
        string addressLabel,
        string addressLine1,
        string? addressLine2,
        string locality,
        string city,
        string state,
        string pinCode,
        string? landmark,
        string? deliveryInstructions,
        string contactName,
        string contactMobile,
        decimal latitude,
        decimal longitude)
    {
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (customerAddressId <= 0) throw new ArgumentOutOfRangeException(nameof(customerAddressId));
        if (branchId <= 0) throw new ArgumentOutOfRangeException(nameof(branchId));
        if (subtotal < 0) throw new ArgumentOutOfRangeException(nameof(subtotal));
        if (discountAmount < 0 || discountAmount > subtotal) throw new ArgumentOutOfRangeException(nameof(discountAmount));

        CustomerId = customerId;
        CustomerAddressId = customerAddressId;
        BranchId = branchId;
        IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey));
        OrderNumber = Required(orderNumber, nameof(orderNumber));
        Type = OrderType.OneTime;
        Status = OrderStatus.PendingPayment;
        Subtotal = subtotal;
        DiscountAmount = discountAmount;
        PayableAmount = subtotal - discountAmount;
        BranchCodeSnapshot = Required(branchCode, nameof(branchCode));
        BranchNameSnapshot = Required(branchName, nameof(branchName));
        AddressLabelSnapshot = Required(addressLabel, nameof(addressLabel));
        AddressLine1Snapshot = Required(addressLine1, nameof(addressLine1));
        AddressLine2Snapshot = Optional(addressLine2);
        LocalitySnapshot = Required(locality, nameof(locality));
        CitySnapshot = Required(city, nameof(city));
        StateSnapshot = Required(state, nameof(state));
        PinCodeSnapshot = Required(pinCode, nameof(pinCode));
        LandmarkSnapshot = Optional(landmark);
        DeliveryInstructionsSnapshot = Optional(deliveryInstructions);
        ContactNameSnapshot = Required(contactName, nameof(contactName));
        ContactMobileSnapshot = Required(contactMobile, nameof(contactMobile));
        LatitudeSnapshot = latitude;
        LongitudeSnapshot = longitude;
    }

    public long CustomerId { get; private set; }
    public long CustomerAddressId { get; private set; }
    public long BranchId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string OrderNumber { get; private set; } = string.Empty;
    public OrderType Type { get; private set; }
    public OrderStatus Status { get; private set; }
    public decimal Subtotal { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public decimal PayableAmount { get; private set; }
    public string BranchCodeSnapshot { get; private set; } = string.Empty;
    public string BranchNameSnapshot { get; private set; } = string.Empty;
    public string AddressLabelSnapshot { get; private set; } = string.Empty;
    public string AddressLine1Snapshot { get; private set; } = string.Empty;
    public string? AddressLine2Snapshot { get; private set; }
    public string LocalitySnapshot { get; private set; } = string.Empty;
    public string CitySnapshot { get; private set; } = string.Empty;
    public string StateSnapshot { get; private set; } = string.Empty;
    public string PinCodeSnapshot { get; private set; } = string.Empty;
    public string? LandmarkSnapshot { get; private set; }
    public string? DeliveryInstructionsSnapshot { get; private set; }
    public string ContactNameSnapshot { get; private set; } = string.Empty;
    public string ContactMobileSnapshot { get; private set; } = string.Empty;
    public decimal LatitudeSnapshot { get; private set; }
    public decimal LongitudeSnapshot { get; private set; }
    public DateTime? CancelledAt { get; private set; }

    public User Customer { get; private set; } = null!;
    public CustomerAddress CustomerAddress { get; private set; } = null!;
    public Branch Branch { get; private set; } = null!;
    public ICollection<OrderItem> Items { get; private set; } = [];

    public void AddItem(OrderItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Items.Add(item);
    }

    public void ConfirmPayment()
    {
        if (Status == OrderStatus.Confirmed)
        {
            return;
        }

        if (Status != OrderStatus.PendingPayment)
        {
            throw new InvalidOperationException(
                $"An order in status '{Status}' cannot be confirmed by payment.");
        }

        Status = OrderStatus.Confirmed;
    }

    public void RecoverCapturedPayment()
    {
        if (Status == OrderStatus.Confirmed)
        {
            return;
        }

        if (Status != OrderStatus.PaymentFailed)
        {
            throw new InvalidOperationException(
                $"An order in status '{Status}' cannot recover a captured payment.");
        }

        Status = OrderStatus.Confirmed;
    }

    public void FailPayment()
    {
        if (Status == OrderStatus.PaymentFailed)
        {
            return;
        }

        if (Status != OrderStatus.PendingPayment)
        {
            throw new InvalidOperationException(
                $"An order in status '{Status}' cannot be marked as payment failed.");
        }

        Status = OrderStatus.PaymentFailed;
    }

    public void Cancel(DateTime now)
    {
        if (Status != OrderStatus.Confirmed)
        {
            throw new InvalidOperationException($"An order in status '{Status}' cannot be cancelled.");
        }

        EnsureIndiaLocal(now, nameof(now));
        Status = OrderStatus.Cancelled;
        CancelledAt = now;
    }

    public void AssignForDelivery()
    {
        if (Status == OrderStatus.Assigned) return;
        EnsureStatus(OrderStatus.Confirmed, "assigned for delivery");
        Status = OrderStatus.Assigned;
    }

    public void StartDelivery()
    {
        if (Status == OrderStatus.OutForDelivery) return;
        EnsureStatus(OrderStatus.Assigned, "started for delivery");
        Status = OrderStatus.OutForDelivery;
    }

    public void MarkDelivered()
    {
        if (Status == OrderStatus.Delivered) return;
        if (Status is not (OrderStatus.Assigned or OrderStatus.OutForDelivery))
        {
            throw new InvalidOperationException($"An order in status '{Status}' cannot be marked delivered.");
        }

        Status = OrderStatus.Delivered;
    }

    public void MarkDeliveryFailed()
    {
        if (Status == OrderStatus.Failed) return;
        if (Status is not (OrderStatus.Assigned or OrderStatus.OutForDelivery))
        {
            throw new InvalidOperationException($"An order in status '{Status}' cannot be marked as delivery failed.");
        }

        Status = OrderStatus.Failed;
    }

    private void EnsureStatus(OrderStatus expected, string operation)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"An order in status '{Status}' cannot be {operation}.");
        }
    }

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("Timestamp must be India-local.", parameterName);
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class OrderItem : Entity
{
    private OrderItem() { }

    public OrderItem(
        long productId,
        decimal quantity,
        decimal unitPrice,
        string sku,
        string productName,
        string unitOfMeasure)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (decimal.Round(quantity, 3) != quantity)
        {
            throw new ArgumentException("Quantity supports at most three decimal places.", nameof(quantity));
        }
        if (unitPrice < 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));

        ProductId = productId;
        Quantity = quantity;
        UnitPrice = unitPrice;
        LineTotal = decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
        SkuSnapshot = Required(sku, nameof(sku));
        ProductNameSnapshot = Required(productName, nameof(productName));
        UnitOfMeasureSnapshot = Required(unitOfMeasure, nameof(unitOfMeasure)).ToLowerInvariant();
    }

    public long OrderId { get; private set; }
    public long ProductId { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal LineTotal { get; private set; }
    public string SkuSnapshot { get; private set; } = string.Empty;
    public string ProductNameSnapshot { get; private set; } = string.Empty;
    public string UnitOfMeasureSnapshot { get; private set; } = string.Empty;

    public Order Order { get; private set; } = null!;
    public Product Product { get; private set; } = null!;

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
}
