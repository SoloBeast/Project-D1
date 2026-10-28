using DoodhDirect.Domain.RefundReplacement;

namespace DoodhDirect.Domain.Tests;

/// <summary>
/// Stage 1/3: support's explicit apply/waive deduction decision on
/// order-backed and subscription-delivery Refund tickets, plus the frozen
/// one-time-order refund math. The decision is never inferred — it is
/// recorded explicitly with a frozen snapshot.
/// </summary>
public sealed class OneTimeOrderRefundDecisionTests
{
    private static RefundReplacementRequest OrderRefundTicket() =>
        new(
            5,
            7,
            9,
            11,
            null,
            RefundReplacementType.Refund,
            RefundReplacementSource.CustomerPostDelivery,
            "Customer missed the delivery call.",
            null,
            new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 19, 9, 0, 0, DateTimeKind.Unspecified));

    [Fact]
    public void RecordDeductionDecision_Apply_SnapshotsConfiguredPercentage()
    {
        var ticket = OrderRefundTicket();

        ticket.RecordDeductionDecision(true, 12.5m, null);

        Assert.Equal(12.5m, ticket.AppliedDeductionPercentage);
        Assert.False(ticket.DeductionWaived);
        Assert.Null(ticket.DeductionWaiverReason);
    }

    [Fact]
    public void RecordDeductionDecision_Waive_SnapshotsZeroWithReason()
    {
        var ticket = OrderRefundTicket();

        ticket.RecordDeductionDecision(false, 0m, "  Goodwill gesture  ");

        Assert.Equal(0m, ticket.AppliedDeductionPercentage);
        Assert.True(ticket.DeductionWaived);
        Assert.Equal("Goodwill gesture", ticket.DeductionWaiverReason);
    }

    [Fact]
    public void RecordDeductionDecision_Waive_RequiresReason()
    {
        var ticket = OrderRefundTicket();

        Assert.Throws<ArgumentException>(() => ticket.RecordDeductionDecision(false, 0m, null));
        Assert.Throws<ArgumentException>(() => ticket.RecordDeductionDecision(false, 0m, "   "));
        Assert.Null(ticket.AppliedDeductionPercentage);
        Assert.False(ticket.DeductionWaived);
    }

    [Fact]
    public void RecordDeductionDecision_Twice_Throws()
    {
        var ticket = OrderRefundTicket();
        ticket.RecordDeductionDecision(true, 10m, null);

        Assert.Throws<InvalidOperationException>(() => ticket.RecordDeductionDecision(false, 0m, "Changed mind"));
        Assert.Equal(10m, ticket.AppliedDeductionPercentage);
    }

    private static RefundReplacementRequest SubscriptionFailTicket(
        RefundReplacementStatus initialStatus = RefundReplacementStatus.Pending) =>
        new(
            null,
            7,
            9,
            11,
            null,
            RefundReplacementType.Refund,
            RefundReplacementSource.DeliveryFailed,
            "No response at the door.",
            null,
            new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Unspecified),
            13,
            new DateTime(2026, 8, 17, 8, 0, 0, DateTimeKind.Unspecified),
            24,
            10m,
            "CustomerNotAvailable",
            initialStatus);

    [Fact]
    public void RecordDeductionDecision_SubscriptionTicket_RecordsDecision()
    {
        // Stage 3: subscription delivery-fail tickets record the same
        // explicit apply/waive decision as order-backed tickets.
        var ticket = SubscriptionFailTicket();

        ticket.RecordDeductionDecision(true, 10m, null);

        Assert.Equal(10m, ticket.AppliedDeductionPercentage);
        Assert.False(ticket.DeductionWaived);
        Assert.Null(ticket.DeductionWaiverReason);
    }

    [Fact]
    public void RecordDeductionDecision_SubscriptionAwaitingChoice_RecordsDecision()
    {
        // The subscription decision is recorded while the customer's Refund
        // choice is on the ticket (AwaitingCustomerChoice), before approval.
        var ticket = SubscriptionFailTicket(RefundReplacementStatus.AwaitingCustomerChoice);

        ticket.RecordDeductionDecision(false, 0m, "Dairy fault confirmed");

        Assert.Equal(0m, ticket.AppliedDeductionPercentage);
        Assert.True(ticket.DeductionWaived);
        Assert.Equal("Dairy fault confirmed", ticket.DeductionWaiverReason);
    }

    [Fact]
    public void RecordDeductionDecision_ReplacementTicket_Throws()
    {
        var ticket = new RefundReplacementRequest(
            5,
            7,
            9,
            11,
            null,
            RefundReplacementType.Replacement,
            RefundReplacementSource.CustomerPostDelivery,
            "Wants a fresh delivery.",
            null,
            new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 19, 9, 0, 0, DateTimeKind.Unspecified));

        Assert.Throws<InvalidOperationException>(() => ticket.RecordDeductionDecision(true, 10m, null));
    }

    [Fact]
    public void RecordDeductionDecision_AfterApproval_Throws()
    {
        var ticket = OrderRefundTicket();
        ticket.Approve(
            19,
            new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Unspecified),
            "Approved.");

        Assert.Throws<InvalidOperationException>(() => ticket.RecordDeductionDecision(true, 10m, null));
    }
}
