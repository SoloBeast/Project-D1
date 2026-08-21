using DoodhDirect.Domain.Subscriptions;

namespace DoodhDirect.Domain.Tests;

public sealed class SubscriptionDomainTests
{
    private static readonly DateTime IndiaNow =
        new(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified);

    [Fact]
    public void Constructor_CreatesPaymentPendingSubscriptionWithPrepaidTotalsAndSnapshots()
    {
        var subscription = CreateSubscription(totalEntitlement: 12, quantity: 1.5m, unitPrice: 80m);

        Assert.Equal(SubscriptionStatus.PaymentPending, subscription.Status);
        Assert.Equal(1440m, subscription.PayableAmount);
        Assert.Equal(12, subscription.TotalEntitlement);
        Assert.Equal(0, subscription.UsedEntitlement);
        Assert.Equal(12, subscription.RemainingEntitlement);
        Assert.Equal("MILK-001", subscription.ProductSkuSnapshot);
        Assert.Equal("Fresh Milk", subscription.ProductNameSnapshot);
        Assert.Equal("litre", subscription.UnitOfMeasureSnapshot);
        Assert.Equal("MAIN", subscription.BranchCodeSnapshot);
        Assert.Equal("Home, 1 Main Road, Central, Bengaluru", subscription.AddressSnapshot);
    }

    [Fact]
    public void ScheduleAndDelivery_RejectDuplicatesAndOutOfTermDates()
    {
        var subscription = CreateSubscription();
        subscription.AddSchedule(DayOfWeek.Monday);
        subscription.AddDelivery(new DateOnly(2026, 8, 17));

        Assert.Throws<InvalidOperationException>(() => subscription.AddSchedule(DayOfWeek.Monday));
        Assert.Throws<InvalidOperationException>(() => subscription.AddDelivery(new DateOnly(2026, 8, 17)));
        Assert.Throws<ArgumentOutOfRangeException>(() => subscription.AddDelivery(new DateOnly(2026, 9, 1)));
    }

    [Fact]
    public void ActivatePauseResume_AreIdempotentAndRequireValidState()
    {
        var subscription = CreateSubscription();

        subscription.Activate(IndiaNow);
        subscription.Activate(IndiaNow.AddMinutes(1));
        subscription.Pause(IndiaNow.AddHours(1));
        subscription.Pause(IndiaNow.AddHours(2));
        subscription.Resume();
        subscription.Resume();

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(IndiaNow, subscription.ActivatedAt);
        Assert.Null(subscription.PausedAt);
        Assert.Throws<InvalidOperationException>(() => subscription.FailPayment());
    }

    [Fact]
    public void RetryPayment_AfterTerminalFailure_ReturnsToPaymentPending()
    {
        var subscription = CreateSubscription();
        subscription.FailPayment();

        subscription.RetryPayment();

        Assert.Equal(SubscriptionStatus.PaymentPending, subscription.Status);
    }

    [Theory]
    [InlineData(SubscriptionStatus.PaymentPending)]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Paused)]
    [InlineData(SubscriptionStatus.Cancelled)]
    public void RetryPayment_OutsidePaymentFailed_IsRejected(SubscriptionStatus status)
    {
        var subscription = CreateSubscription();
        if (status == SubscriptionStatus.Active || status == SubscriptionStatus.Paused || status == SubscriptionStatus.Cancelled)
        {
            subscription.Activate(IndiaNow);
        }
        if (status == SubscriptionStatus.Paused || status == SubscriptionStatus.Cancelled)
        {
            subscription.Pause(IndiaNow.AddMinutes(1));
        }
        if (status == SubscriptionStatus.Cancelled)
        {
            subscription.Cancel(IndiaNow.AddMinutes(2));
        }

        Assert.Throws<InvalidOperationException>(() => subscription.RetryPayment());
        Assert.Equal(status, subscription.Status);
    }

    [Fact]
    public void Skip_BeforeCutoffIsIdempotentAndDoesNotConsumeEntitlement()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var delivery = Assert.Single(subscription.Deliveries);
        var beforeCutoff = new DateTime(2026, 8, 16, 23, 59, 0, DateTimeKind.Unspecified);

        subscription.Skip(delivery, beforeCutoff, TimeSpan.FromHours(24));
        subscription.Skip(delivery, beforeCutoff.AddMinutes(1), TimeSpan.FromHours(24));

        Assert.Equal(SubscriptionDeliveryStatus.Skipped, delivery.Status);
        Assert.Equal(0, subscription.UsedEntitlement);
        Assert.Equal(subscription.TotalEntitlement, subscription.RemainingEntitlement);
    }

    [Fact]
    public void Skip_AfterCutoffIsRejectedWithoutChangingOccurrence()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var delivery = Assert.Single(subscription.Deliveries);
        var afterCutoff = new DateTime(2026, 8, 17, 0, 0, 1, DateTimeKind.Unspecified);

        Assert.Throws<InvalidOperationException>(() =>
            subscription.Skip(delivery, afterCutoff, TimeSpan.FromHours(24)));
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, delivery.Status);
    }

    [Fact]
    public void MarkDelivered_ConsumesOnlyOnceAndCompletesAtFiniteEntitlement()
    {
        var subscription = CreateActiveSubscription(totalEntitlement: 2);
        subscription.AddDelivery(new DateOnly(2026, 8, 17));
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var deliveries = subscription.Deliveries.OrderBy(x => x.ScheduledDate).ToArray();

        subscription.MarkDelivered(deliveries[0], IndiaNow);
        subscription.MarkDelivered(deliveries[0], IndiaNow.AddMinutes(1));
        subscription.MarkDelivered(deliveries[1], IndiaNow.AddDays(1));

        Assert.Equal(2, subscription.UsedEntitlement);
        Assert.Equal(0, subscription.RemainingEntitlement);
        Assert.Equal(SubscriptionStatus.Completed, subscription.Status);
        Assert.Equal(IndiaNow.AddDays(1), subscription.CompletedAt);
    }

    [Fact]
    public void FailedDelivery_IsTerminalAndDoesNotConsumeEntitlement()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 17));
        var delivery = Assert.Single(subscription.Deliveries);

        subscription.MarkFailed(delivery, IndiaNow);
        subscription.MarkFailed(delivery, IndiaNow.AddMinutes(1));

        Assert.Equal(SubscriptionDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(0, subscription.UsedEntitlement);
        Assert.Throws<InvalidOperationException>(() =>
            subscription.MarkDelivered(delivery, IndiaNow.AddMinutes(2)));
    }

    [Fact]
    public void Cancel_PreservesOccurrencesAndCancelsOnlyScheduledOnes()
    {
        var subscription = CreateActiveSubscription(totalEntitlement: 3);
        subscription.AddDelivery(new DateOnly(2026, 8, 17));
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        subscription.AddDelivery(new DateOnly(2026, 8, 19));
        var deliveries = subscription.Deliveries.OrderBy(x => x.ScheduledDate).ToArray();
        subscription.MarkDelivered(deliveries[0], IndiaNow);
        subscription.MarkFailed(deliveries[1], IndiaNow);

        subscription.Cancel(IndiaNow.AddHours(1));
        subscription.Cancel(IndiaNow.AddHours(2));

        Assert.Equal(SubscriptionStatus.Cancelled, subscription.Status);
        Assert.Equal(3, subscription.Deliveries.Count);
        Assert.Equal(SubscriptionDeliveryStatus.Delivered, deliveries[0].Status);
        Assert.Equal(SubscriptionDeliveryStatus.Failed, deliveries[1].Status);
        Assert.Equal(SubscriptionDeliveryStatus.Cancelled, deliveries[2].Status);
        Assert.Equal(1, subscription.UsedEntitlement);
    }

    [Fact]
    public void StateTransitions_RejectNonIndiaLocalTimestamps()
    {
        var subscription = CreateSubscription();

        Assert.Throws<ArgumentException>(() => subscription.Activate(new DateTime(2026, 8, 16, 7, 30, 0, DateTimeKind.Utc)));
    }

    private static Subscription CreateActiveSubscription(int totalEntitlement = 4)
    {
        var subscription = CreateSubscription(totalEntitlement);
        subscription.Activate(IndiaNow);
        return subscription;
    }

    private static Subscription CreateSubscription(
        int totalEntitlement = 4,
        decimal quantity = 1m,
        decimal unitPrice = 80m) =>
        new(
            customerId: 1,
            productId: 2,
            customerAddressId: 3,
            branchId: 4,
            idempotencyKey: "subscription-001",
            startDate: new DateOnly(2026, 8, 17),
            endDate: new DateOnly(2026, 8, 31),
            quantity,
            unitPrice,
            totalEntitlement,
            productSku: "MILK-001",
            productName: "Fresh Milk",
            unitOfMeasure: "LITRE",
            branchCode: "MAIN",
            branchName: "Main Branch",
            addressSnapshot: "Home, 1 Main Road, Central, Bengaluru");
}
