using DoodhDirect.Domain.RefundReplacement;

namespace DoodhDirect.Domain.Tests;

/// <summary>
/// Legacy remediation: an approved, order-backed Replacement ticket moves to
/// the explicit terminal <c>Superseded</c> status (no fulfillment or refund
/// implied), and a replacement Refund ticket links back to it. History —
/// type, source, approval identity/timestamp, evidence — is preserved.
/// </summary>
public sealed class RefundReplacementRemediationTests
{
    private static readonly DateTime SubmittedAt =
        new(2026, 8, 17, 9, 0, 0, DateTimeKind.Unspecified);

    private static readonly DateTime DecidedAt =
        new(2026, 8, 17, 11, 0, 0, DateTimeKind.Unspecified);

    private static readonly DateTime RemediatedAt =
        new(2026, 10, 16, 9, 0, 0, DateTimeKind.Unspecified);

    private static RefundReplacementRequest ApprovedOrderReplacement() =>
        ApprovedTicket(
            5,
            null,
            RefundReplacementType.Replacement,
            RefundReplacementSource.CustomerPostDelivery);

    private static RefundReplacementRequest ApprovedTicket(
        long? orderId,
        long? subscriptionDeliveryId,
        RefundReplacementType type,
        RefundReplacementSource source) =>
        Ticket(orderId, subscriptionDeliveryId, type, source, approve: true);

    private static RefundReplacementRequest Ticket(
        long? orderId,
        long? subscriptionDeliveryId,
        RefundReplacementType type,
        RefundReplacementSource source,
        bool approve)
    {
        var ticket = new RefundReplacementRequest(
            orderId,
            7,
            9,
            11,
            null,
            type,
            source,
            "Customer missed the delivery call.",
            null,
            SubmittedAt,
            SubmittedAt.AddHours(48),
            subscriptionDeliveryId,
            subscriptionDeliveryId.HasValue ? SubmittedAt : null,
            subscriptionDeliveryId.HasValue ? 24 : null,
            subscriptionDeliveryId.HasValue ? 10m : null);
        if (approve)
        {
            ticket.Approve(21, DecidedAt, "Legacy approval.");
        }

        return ticket;
    }

    [Fact]
    public void MarkSuperseded_ApprovedOrderReplacement_TransitionsWithMetadata()
    {
        var ticket = ApprovedOrderReplacement();

        ticket.MarkSuperseded(31, RemediatedAt, "  Replacement unsupported for one-time orders  ");

        Assert.Equal(RefundReplacementStatus.Superseded, ticket.Status);
        Assert.Equal(31, ticket.RemediatedByUserId);
        Assert.Equal(RemediatedAt, ticket.RemediatedAt);
        Assert.Equal("Replacement unsupported for one-time orders", ticket.RemediationReason);
        // History preserved: type, source, approval identity/timestamp kept;
        // completion stays null so no fulfillment or refund is implied.
        Assert.Equal(RefundReplacementType.Replacement, ticket.Type);
        Assert.Equal(RefundReplacementSource.CustomerPostDelivery, ticket.Source);
        Assert.Equal(21, ticket.DecidedByUserId);
        Assert.Equal(DecidedAt, ticket.DecidedAt);
        Assert.Null(ticket.CompletedByUserId);
        Assert.Null(ticket.CompletedAt);
    }

    [Fact]
    public void MarkSuperseded_Pending_Throws()
    {
        var ticket = Ticket(5, null, RefundReplacementType.Replacement, RefundReplacementSource.CustomerPostDelivery, approve: false);

        Assert.Throws<InvalidOperationException>(() => ticket.MarkSuperseded(31, RemediatedAt, "Reason."));
        Assert.Equal(RefundReplacementStatus.Pending, ticket.Status);
    }

    [Fact]
    public void MarkSuperseded_OrderRefund_Throws()
    {
        var ticket = ApprovedTicket(5, null, RefundReplacementType.Refund, RefundReplacementSource.CustomerPostDelivery);

        var error = Assert.Throws<InvalidOperationException>(
            () => ticket.MarkSuperseded(31, RemediatedAt, "Reason."));
        Assert.Contains("replacement", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RefundReplacementStatus.Approved, ticket.Status);
    }

    [Fact]
    public void MarkSuperseded_SubscriptionTicket_Throws()
    {
        var ticket = ApprovedTicket(null, 13, RefundReplacementType.Replacement, RefundReplacementSource.DeliveryFailed);

        Assert.Throws<InvalidOperationException>(() => ticket.MarkSuperseded(31, RemediatedAt, "Reason."));
        Assert.Equal(RefundReplacementStatus.Approved, ticket.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MarkSuperseded_BlankReason_Throws(string? reason)
    {
        var ticket = ApprovedOrderReplacement();

        Assert.Throws<ArgumentException>(() => ticket.MarkSuperseded(31, RemediatedAt, reason!));
        Assert.Equal(RefundReplacementStatus.Approved, ticket.Status);
        Assert.Null(ticket.RemediationReason);
    }

    [Fact]
    public void MarkSuperseded_LongReason_Throws()
    {
        var ticket = ApprovedOrderReplacement();

        Assert.Throws<ArgumentException>(
            () => ticket.MarkSuperseded(31, RemediatedAt, new string('x', 1001)));
        Assert.Equal(RefundReplacementStatus.Approved, ticket.Status);
    }

    [Fact]
    public void MarkSuperseded_InvalidActor_Throws()
    {
        var ticket = ApprovedOrderReplacement();

        Assert.Throws<ArgumentOutOfRangeException>(() => ticket.MarkSuperseded(0, RemediatedAt, "Reason."));
        Assert.Equal(RefundReplacementStatus.Approved, ticket.Status);
    }

    [Fact]
    public void MarkSuperseded_Twice_Throws()
    {
        var ticket = ApprovedOrderReplacement();
        ticket.MarkSuperseded(31, RemediatedAt, "Reason.");

        Assert.Throws<InvalidOperationException>(() => ticket.MarkSuperseded(31, RemediatedAt, "Again."));
        Assert.Equal(RefundReplacementStatus.Superseded, ticket.Status);
    }

    [Fact]
    public void RecordRemediationLink_PendingOrderRefund_Links()
    {
        var ticket = Ticket(5, null, RefundReplacementType.Refund, RefundReplacementSource.CustomerPostDelivery, approve: false);

        ticket.RecordRemediationLink(77);

        Assert.Equal(77, ticket.SupersedesRequestId);
        Assert.Equal(RefundReplacementStatus.Pending, ticket.Status);
    }

    [Fact]
    public void RecordRemediationLink_Replacement_Throws()
    {
        var ticket = Ticket(5, null, RefundReplacementType.Replacement, RefundReplacementSource.CustomerPostDelivery, approve: false);

        Assert.Throws<InvalidOperationException>(() => ticket.RecordRemediationLink(77));
        Assert.Null(ticket.SupersedesRequestId);
    }

    [Fact]
    public void RecordRemediationLink_NonPending_Throws()
    {
        var ticket = ApprovedTicket(5, null, RefundReplacementType.Refund, RefundReplacementSource.CustomerPostDelivery);

        Assert.Throws<InvalidOperationException>(() => ticket.RecordRemediationLink(77));
        Assert.Null(ticket.SupersedesRequestId);
    }

    [Fact]
    public void RecordRemediationLink_SubscriptionTicket_Throws()
    {
        var ticket = Ticket(null, 13, RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed, approve: false);

        Assert.Throws<InvalidOperationException>(() => ticket.RecordRemediationLink(77));
        Assert.Null(ticket.SupersedesRequestId);
    }

    [Fact]
    public void RecordRemediationLink_Twice_Throws()
    {
        var ticket = Ticket(5, null, RefundReplacementType.Refund, RefundReplacementSource.CustomerPostDelivery, approve: false);
        ticket.RecordRemediationLink(77);

        Assert.Throws<InvalidOperationException>(() => ticket.RecordRemediationLink(78));
        Assert.Equal(77, ticket.SupersedesRequestId);
    }
}
