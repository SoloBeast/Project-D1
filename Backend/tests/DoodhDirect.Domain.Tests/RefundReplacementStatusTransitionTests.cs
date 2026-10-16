using DoodhDirect.Domain.RefundReplacement;

namespace DoodhDirect.Domain.Tests;

/// <summary>
/// D1-D slice 7.1: the subscription-cancellation terminal outcome is
/// distinct from both successful fulfillment (<see
/// cref="RefundReplacementStatus.Completed"/>) and the replacement-failure
/// refund-only path (<see cref="RefundReplacementStatus.Failed"/>).
/// </summary>
public sealed class RefundReplacementStatusTransitionTests
{
    private static RefundReplacementRequest ApprovedTicket()
    {
        var ticket = new RefundReplacementRequest(
            null,
            7,
            9,
            11,
            null,
            RefundReplacementType.Replacement,
            RefundReplacementSource.DeliveryFailed,
            "No response at the door.",
            null,
            new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Unspecified),
            13,
            new DateTime(2026, 8, 17, 8, 0, 0, DateTimeKind.Unspecified),
            24,
            10m,
            CustomerNotAvailableFallback);
        ticket.Approve(
            19,
            new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Unspecified),
            "Approved for re-delivery.");
        return ticket;
    }

    private const string CustomerNotAvailableFallback = "CustomerNotAvailable";

    [Fact]
    public void MarkSubscriptionCancelled_FromApproved_ResolvesToCancelled()
    {
        var ticket = ApprovedTicket();

        ticket.MarkSubscriptionCancelled(
            21,
            new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Unspecified),
            "Subscription cancelled; pending replacement voided and refunded.");

        Assert.Equal(RefundReplacementStatus.Cancelled, ticket.Status);
        Assert.Equal(21, ticket.CompletedByUserId);
        Assert.NotNull(ticket.CompletedAt);
        Assert.Contains("cancelled", ticket.CompletionRemarks ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MarkSubscriptionCancelled_FromPending_Throws()
    {
        var ticket = new RefundReplacementRequest(
            null,
            7,
            9,
            11,
            null,
            RefundReplacementType.Replacement,
            RefundReplacementSource.DeliveryFailed,
            "No response at the door.",
            null,
            new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Unspecified),
            13,
            new DateTime(2026, 8, 17, 8, 0, 0, DateTimeKind.Unspecified),
            24,
            10m,
            CustomerNotAvailableFallback);

        Assert.Throws<InvalidOperationException>(() => ticket.MarkSubscriptionCancelled(
            21,
            new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Unspecified),
            null));
    }

    [Fact]
    public void TerminalOutcomes_AreMutuallyExclusive()
    {
        var cancelled = ApprovedTicket();
        cancelled.MarkSubscriptionCancelled(
            21,
            new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Unspecified),
            null);

        // A cancelled replacement can never be presented as delivered, nor
        // re-resolved through the failure path.
        Assert.Throws<InvalidOperationException>(() => cancelled.Complete(
            21,
            new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Unspecified),
            null));
        Assert.Throws<InvalidOperationException>(() => cancelled.MarkRemedyFailed(
            21,
            new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Unspecified),
            null));

        var completed = ApprovedTicket();
        completed.Complete(
            21,
            new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Unspecified),
            "Replacement delivery delivered.");

        // Successful fulfillment stays Completed and cannot be recast.
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.Throws<InvalidOperationException>(() => completed.MarkSubscriptionCancelled(
            21,
            new DateTime(2026, 8, 22, 10, 0, 0, DateTimeKind.Unspecified),
            null));
    }
}
