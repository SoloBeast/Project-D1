using DoodhDirect.Domain.Subscriptions;

namespace DoodhDirect.Domain.Tests;

/// <summary>
/// D1-D slice 1: replacement-occurrence domain support. Covers valid creation
/// (new Scheduled row, frozen copy, linkage, post-EndDate allowed), invariant
/// enforcement (at most one replacement, failed-source-only, date uniqueness),
/// and preservation of entitlement, financials, and existing lifecycle rules.
/// </summary>
public sealed class SubscriptionReplacementDeliveryTests
{
    private static readonly DateTime IndiaNow =
        new(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified);

    [Fact]
    public void AddReplacementDelivery_CreatesScheduledRowPreservingSourceAndTerm()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var failed = Assert.Single(subscription.Deliveries);
        subscription.MarkFailed(failed, IndiaNow);
        var endDate = subscription.EndDate;
        var payable = subscription.PayableAmount;
        var chargesTotal = subscription.ChargesTotal;

        var replacement = subscription.AddReplacementDelivery(
            failed,
            new DateOnly(2026, 9, 5),
            refundReplacementRequestId: 7,
            reason: "  Approved re-delivery  ");

        Assert.Equal(2, subscription.Deliveries.Count);
        Assert.Equal(SubscriptionDeliveryStatus.Failed, failed.Status);
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
        Assert.True(replacement.IsReplacement);
        Assert.False(failed.IsReplacement);
        Assert.Same(failed, replacement.ReplacesSubscriptionDelivery);
        Assert.Equal(7, replacement.RefundReplacementRequestId);
        Assert.Equal("Approved re-delivery", replacement.ReplacementReason);
        Assert.Null(failed.ReplacesSubscriptionDeliveryId);
        Assert.Null(failed.RefundReplacementRequestId);
        Assert.Null(failed.ReplacementReason);

        // Rule 4: frozen copy from the failed occurrence.
        Assert.Equal(failed.Slot, replacement.Slot);
        Assert.Equal(failed.BranchId, replacement.BranchId);
        Assert.Equal(failed.Quantity, replacement.Quantity);
        Assert.Equal(failed.BranchCodeSnapshot, replacement.BranchCodeSnapshot);
        Assert.Equal(failed.BranchNameSnapshot, replacement.BranchNameSnapshot);
        Assert.Equal(failed.AddressSnapshot, replacement.AddressSnapshot);
        Assert.Equal(new DateOnly(2026, 9, 5), replacement.ScheduledDate);

        // Rule 2: replacement beyond EndDate without extending the term.
        Assert.True(replacement.ScheduledDate > endDate);
        Assert.Equal(endDate, subscription.EndDate);

        // Rule 3 + Rule 9: creation consumes nothing and reprices nothing.
        Assert.Equal(0, subscription.UsedEntitlement);
        Assert.Equal(subscription.TotalEntitlement, subscription.RemainingEntitlement);
        Assert.Equal(payable, subscription.PayableAmount);
        Assert.Equal(chargesTotal, subscription.ChargesTotal);
        Assert.Empty(subscription.Charges);
    }

    [Fact]
    public void AddReplacementDelivery_NullReasonStaysNull()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var failed = Assert.Single(subscription.Deliveries);
        subscription.MarkFailed(failed, IndiaNow);

        var replacement = subscription.AddReplacementDelivery(
            failed, new DateOnly(2026, 9, 5), 7, "   ");

        Assert.Null(replacement.ReplacementReason);
    }

    [Fact]
    public void AddReplacementDelivery_DeliveringReplacementConsumesOutstandingEntitlement()
    {
        var subscription = CreateActiveSubscription(totalEntitlement: 2);
        subscription.AddDelivery(new DateOnly(2026, 8, 17));
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var deliveries = subscription.Deliveries.OrderBy(x => x.ScheduledDate).ToArray();
        subscription.MarkDelivered(deliveries[0], IndiaNow);
        subscription.MarkFailed(deliveries[1], IndiaNow);

        var replacement = subscription.AddReplacementDelivery(
            deliveries[1], new DateOnly(2026, 9, 5), 9, "re-drive");
        subscription.MarkDelivered(replacement, IndiaNow.AddDays(1));

        Assert.Equal(2, subscription.UsedEntitlement);
        Assert.Equal(0, subscription.RemainingEntitlement);
        Assert.Equal(SubscriptionStatus.Completed, subscription.Status);
    }

    [Fact]
    public void AddReplacementDelivery_RejectsNonFailedSource()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var scheduled = Assert.Single(subscription.Deliveries);

        Assert.Throws<InvalidOperationException>(() =>
            subscription.AddReplacementDelivery(scheduled, new DateOnly(2026, 9, 5), 7, null));
        Assert.Single(subscription.Deliveries);
    }

    [Fact]
    public void AddReplacementDelivery_RejectsForeignSource()
    {
        var subscription = CreateActiveSubscription();
        var other = CreateActiveSubscription();
        other.AddDelivery(new DateOnly(2026, 8, 18));
        var foreign = Assert.Single(other.Deliveries);
        other.MarkFailed(foreign, IndiaNow);

        Assert.Throws<InvalidOperationException>(() =>
            subscription.AddReplacementDelivery(foreign, new DateOnly(2026, 9, 5), 7, null));
        Assert.Empty(subscription.Deliveries);
    }

    [Fact]
    public void AddReplacementDelivery_RejectsSecondReplacementForSameFailure()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var failed = Assert.Single(subscription.Deliveries);
        subscription.MarkFailed(failed, IndiaNow);
        subscription.AddReplacementDelivery(failed, new DateOnly(2026, 9, 5), 7, null);

        Assert.Throws<InvalidOperationException>(() =>
            subscription.AddReplacementDelivery(failed, new DateOnly(2026, 9, 6), 8, null));
        Assert.Equal(2, subscription.Deliveries.Count);
    }

    [Fact]
    public void AddReplacementDelivery_RejectsReplacingAReplacement()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var failed = Assert.Single(subscription.Deliveries);
        subscription.MarkFailed(failed, IndiaNow);
        var replacement = subscription.AddReplacementDelivery(
            failed, new DateOnly(2026, 9, 5), 7, null);
        subscription.MarkFailed(replacement, IndiaNow.AddDays(1));

        Assert.Throws<InvalidOperationException>(() =>
            subscription.AddReplacementDelivery(replacement, new DateOnly(2026, 9, 6), 7, null));
        Assert.Equal(2, subscription.Deliveries.Count);
    }

    [Fact]
    public void AddReplacementDelivery_RejectsConflictingDate()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        subscription.AddDelivery(new DateOnly(2026, 8, 19));
        var deliveries = subscription.Deliveries.OrderBy(x => x.ScheduledDate).ToArray();
        subscription.MarkFailed(deliveries[0], IndiaNow);

        Assert.Throws<InvalidOperationException>(() =>
            subscription.AddReplacementDelivery(
                deliveries[0], new DateOnly(2026, 8, 19), 7, null));
        Assert.Equal(2, subscription.Deliveries.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void AddReplacementDelivery_RejectsInvalidRequestId(long requestId)
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var failed = Assert.Single(subscription.Deliveries);
        subscription.MarkFailed(failed, IndiaNow);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            subscription.AddReplacementDelivery(failed, new DateOnly(2026, 9, 5), requestId, null));
        Assert.Single(subscription.Deliveries);
    }

    [Fact]
    public void AddReplacementDelivery_RejectsNullSource()
    {
        var subscription = CreateActiveSubscription();

        Assert.Throws<ArgumentNullException>(() =>
            subscription.AddReplacementDelivery(null!, new DateOnly(2026, 9, 5), 7, null));
    }

    [Fact]
    public void AddReplacementDelivery_RejectsOverlongReason()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var failed = Assert.Single(subscription.Deliveries);
        subscription.MarkFailed(failed, IndiaNow);

        Assert.Throws<ArgumentException>(() =>
            subscription.AddReplacementDelivery(
                failed, new DateOnly(2026, 9, 5), 7, new string('r', 201)));
        Assert.Single(subscription.Deliveries);
    }

    [Fact]
    public void OrdinaryAddDelivery_StillRejectsPostEndDate()
    {
        var subscription = CreateActiveSubscription();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            subscription.AddDelivery(new DateOnly(2026, 9, 5)));
    }

    [Fact]
    public void Skip_RejectsReplacementDeliverySoVacationCannotSilentlyConsumeIt()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var failed = Assert.Single(subscription.Deliveries);
        subscription.MarkFailed(failed, IndiaNow);
        var replacement = subscription.AddReplacementDelivery(
            failed, new DateOnly(2026, 9, 5), 7, null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            subscription.Skip(replacement, IndiaNow.AddDays(1), TimeSpan.FromHours(24)));
        Assert.Contains("explicit operational decision", exception.Message);
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
    }

    [Fact]
    public void Skip_StillSkipsOrdinaryScheduledDelivery()
    {
        var subscription = CreateActiveSubscription();
        subscription.AddDelivery(new DateOnly(2026, 8, 18));
        var scheduled = Assert.Single(subscription.Deliveries);

        subscription.Skip(scheduled, IndiaNow, TimeSpan.FromHours(24));

        Assert.Equal(SubscriptionDeliveryStatus.Skipped, scheduled.Status);
    }

    private static Subscription CreateActiveSubscription(int totalEntitlement = 4)
    {
        var subscription = new Subscription(
            customerId: 1,
            productId: 2,
            customerAddressId: 3,
            branchId: 4,
            idempotencyKey: "subscription-001",
            startDate: new DateOnly(2026, 8, 17),
            endDate: new DateOnly(2026, 8, 31),
            quantity: 1m,
            unitPrice: 80m,
            totalEntitlement,
            productSku: "MILK-001",
            productName: "Fresh Milk",
            unitOfMeasure: "LITRE",
            branchCode: "MAIN",
            branchName: "Main Branch",
            addressSnapshot: "Home, 1 Main Road, Central, Bengaluru");
        subscription.Activate(IndiaNow);
        return subscription;
    }
}
