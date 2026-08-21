using DoodhDirect.Domain.Orders;

namespace DoodhDirect.Domain.Tests;

public sealed class OrderDomainTests
{
    [Fact]
    public void Constructor_CreatesPendingPaymentOneTimeOrderWithHistoricalSnapshots()
    {
        var order = CreateOrder(subtotal: 200m, discountAmount: 25m);

        Assert.Equal(OrderType.OneTime, order.Type);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(200m, order.Subtotal);
        Assert.Equal(25m, order.DiscountAmount);
        Assert.Equal(175m, order.PayableAmount);
        Assert.Equal("MAIN", order.BranchCodeSnapshot);
        Assert.Equal("Home", order.AddressLabelSnapshot);
        Assert.Equal("Customer Name", order.ContactNameSnapshot);
        Assert.Null(order.CancelledAt);
    }

    [Fact]
    public void OrderItem_RoundsLineTotalAwayFromZeroAndNormalizesSnapshots()
    {
        var item = new OrderItem(10, 1.005m, 10m, " milk-001 ", " Fresh Milk ", " LITRE ");

        Assert.Equal(10.05m, item.LineTotal);
        Assert.Equal("milk-001", item.SkuSnapshot);
        Assert.Equal("Fresh Milk", item.ProductNameSnapshot);
        Assert.Equal("litre", item.UnitOfMeasureSnapshot);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void OrderItem_RejectsNonPositiveQuantity(decimal quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrderItem(10, quantity, 80m, "MILK-001", "Milk", "litre"));
    }

    [Fact]
    public void OrderItem_RejectsMoreThanThreeFractionalDigits()
    {
        Assert.Throws<ArgumentException>(() =>
            new OrderItem(10, 1.0001m, 80m, "MILK-001", "Milk", "litre"));
    }

    [Fact]
    public void Cancel_TransitionsConfirmedOrderAndRecordsIndiaLocalTimestamp()
    {
        var order = CreateOrder();
        order.ConfirmPayment();
        var cancelledAt = new DateTime(2026, 8, 16, 1, 0, 0, DateTimeKind.Unspecified);

        order.Cancel(cancelledAt);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(cancelledAt, order.CancelledAt);
    }

    [Fact]
    public void Cancel_RejectsRepeatedCancellation()
    {
        var order = CreateOrder();
        order.ConfirmPayment();
        order.Cancel(new DateTime(2026, 8, 16, 1, 0, 0, DateTimeKind.Unspecified));

        Assert.Throws<InvalidOperationException>(() =>
            order.Cancel(new DateTime(2026, 8, 16, 1, 1, 0, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void Cancel_RejectsNonIndiaLocalTimestamp()
    {
        var order = CreateOrder();
        order.ConfirmPayment();

        Assert.Throws<ArgumentException>(() =>
            order.Cancel(new DateTime(2026, 8, 16, 1, 0, 0, DateTimeKind.Utc)));
    }

    private static Order CreateOrder(decimal subtotal = 160m, decimal discountAmount = 0m) =>
        new(
            customerId: 1,
            customerAddressId: 2,
            branchId: 3,
            idempotencyKey: "checkout-001",
            orderNumber: "DD-20260816010000-ABCDEF",
            subtotal,
            discountAmount,
            branchCode: "MAIN",
            branchName: "Main Branch",
            addressLabel: "Home",
            addressLine1: "1 Main Road",
            addressLine2: null,
            locality: "Central",
            city: "Bengaluru",
            state: "Karnataka",
            pinCode: "560001",
            landmark: null,
            deliveryInstructions: "Leave at reception",
            contactName: "Customer Name",
            contactMobile: "9999999999",
            latitude: 12.9716m,
            longitude: 77.5946m);
}
