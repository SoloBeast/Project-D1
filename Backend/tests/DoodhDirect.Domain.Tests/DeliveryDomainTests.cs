using DoodhDirect.Domain.Deliveries;

namespace DoodhDirect.Domain.Tests;

public sealed class DeliveryDomainTests
{
    private const long EmployeeId = 20;
    private const long OtherEmployeeId = 21;
    private static readonly DateTime UtcNow = new(2026, 8, 16, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Factories_CreateReadyDeliveriesWithExactlyOneSourceAndNormalizedSnapshots()
    {
        var orderDelivery = CreateOrderDelivery();
        var subscriptionDelivery = CreateSubscriptionDelivery();

        Assert.Equal(DeliverySourceType.OneTimeOrder, orderDelivery.SourceType);
        Assert.Equal(DeliveryStatus.ReadyForAssignment, orderDelivery.Status);
        Assert.Equal(10, orderDelivery.OrderId);
        Assert.Null(orderDelivery.SubscriptionDeliveryId);
        Assert.Equal("DD-ORDER-001", orderDelivery.ReferenceNumber);
        Assert.Equal("Customer Name", orderDelivery.CustomerNameSnapshot);
        Assert.Equal("Leave at reception", orderDelivery.DeliveryInstructionsSnapshot);

        Assert.Equal(DeliverySourceType.SubscriptionOccurrence, subscriptionDelivery.SourceType);
        Assert.Null(subscriptionDelivery.OrderId);
        Assert.Equal(11, subscriptionDelivery.SubscriptionDeliveryId);
    }

    [Fact]
    public void Factory_RejectsInvalidIdentityCoordinatesAndRequiredSnapshots()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOrderDelivery(orderId: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOrderDelivery(customerId: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOrderDelivery(branchId: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOrderDelivery(latitude: 90.0001m));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateOrderDelivery(longitude: -180.0001m));
        Assert.Throws<ArgumentException>(() => CreateOrderDelivery(referenceNumber: " "));
    }

    [Fact]
    public void Assign_AllowsReassignmentBeforePickupAndRecordsHistory()
    {
        var delivery = CreateOrderDelivery();

        var initial = delivery.Assign(EmployeeId, assignedByUserId: 30, UtcNow, " Morning route ");
        var reassignedAt = UtcNow.AddMinutes(2);
        var reassignment = delivery.Assign(OtherEmployeeId, assignedByUserId: 31, reassignedAt, "Coverage");

        Assert.Equal(DeliveryStatus.Assigned, delivery.Status);
        Assert.Equal(OtherEmployeeId, delivery.AssignedEmployeeId);
        Assert.Equal(reassignedAt, delivery.AssignedAtUtc);
        Assert.Null(initial.PreviousEmployeeId);
        Assert.Equal("Morning route", initial.Reason);
        Assert.Equal(EmployeeId, reassignment.PreviousEmployeeId);
        Assert.Equal(2, delivery.Assignments.Count);
        Assert.Throws<InvalidOperationException>(() =>
            delivery.Assign(OtherEmployeeId, assignedByUserId: 31, reassignedAt.AddMinutes(1), null));
    }

    [Fact]
    public void Assign_RejectsReassignmentAfterPickup()
    {
        var delivery = CreateAssignedDelivery();
        delivery.PickUp(EmployeeId, UtcNow.AddMinutes(1), null);

        Assert.Throws<InvalidOperationException>(() =>
            delivery.Assign(OtherEmployeeId, assignedByUserId: 30, UtcNow.AddMinutes(2), null));
    }

    [Fact]
    public void Lifecycle_RecordsTransitionsAndRequiresVerifiedOtpForCompletion()
    {
        var delivery = CreateAssignedDelivery();

        delivery.PickUp(EmployeeId, UtcNow.AddMinutes(1), " Handle carefully ");
        delivery.Start(EmployeeId, UtcNow.AddMinutes(2));
        delivery.Arrive(EmployeeId, UtcNow.AddMinutes(3));

        Assert.True(delivery.IsTrackingActive);
        Assert.Throws<InvalidOperationException>(() =>
            delivery.Complete(EmployeeId, UtcNow.AddMinutes(4), null));

        delivery.RecordOtpVerified(EmployeeId, UtcNow.AddMinutes(4));
        delivery.Complete(EmployeeId, UtcNow.AddMinutes(5), " Handed to customer ");

        Assert.Equal(DeliveryStatus.Delivered, delivery.Status);
        Assert.Equal("Handle carefully", delivery.OperationalNotes);
        Assert.Equal("Handed to customer", delivery.Remarks);
        Assert.Equal(UtcNow.AddMinutes(5), delivery.CompletedAtUtc);
        Assert.False(delivery.IsTrackingActive);
    }

    [Fact]
    public void Operations_RejectEmployeeWhoIsNotCurrentlyAssigned()
    {
        var delivery = CreateAssignedDelivery();

        Assert.Throws<InvalidOperationException>(() =>
            delivery.PickUp(OtherEmployeeId, UtcNow.AddMinutes(1), null));

        delivery.PickUp(EmployeeId, UtcNow.AddMinutes(1), null);
        delivery.Start(EmployeeId, UtcNow.AddMinutes(2));

        Assert.Throws<InvalidOperationException>(() =>
            delivery.Arrive(OtherEmployeeId, UtcNow.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() =>
            delivery.EnsureCanRecordLocation(OtherEmployeeId));
        Assert.Throws<InvalidOperationException>(() =>
            delivery.Fail(OtherEmployeeId, UtcNow.AddMinutes(3), DeliveryFailureReasons.Other, null, null, null));
    }

    [Fact]
    public void Lifecycle_RejectsOutOfOrderAndNonUtcTransitions()
    {
        var delivery = CreateAssignedDelivery();

        Assert.Throws<InvalidOperationException>(() => delivery.Start(EmployeeId, UtcNow));
        Assert.Throws<ArgumentException>(() => delivery.PickUp(EmployeeId, DateTime.Now, null));
    }

    [Fact]
    public void Tracking_IsAllowedOnlyDuringOutForDeliveryAndArrived()
    {
        var delivery = CreateAssignedDelivery();

        Assert.Throws<InvalidOperationException>(() => delivery.EnsureCanRecordLocation(EmployeeId));
        delivery.PickUp(EmployeeId, UtcNow.AddMinutes(1), null);
        Assert.Throws<InvalidOperationException>(() => delivery.EnsureCanRecordLocation(EmployeeId));

        delivery.Start(EmployeeId, UtcNow.AddMinutes(2));
        delivery.EnsureCanRecordLocation(EmployeeId);
        delivery.Arrive(EmployeeId, UtcNow.AddMinutes(3));
        delivery.EnsureCanRecordLocation(EmployeeId);

        delivery.Fail(EmployeeId, UtcNow.AddMinutes(4), DeliveryFailureReasons.Other, null, null, null);
        Assert.False(delivery.IsTrackingActive);
        Assert.Throws<InvalidOperationException>(() => delivery.EnsureCanRecordLocation(EmployeeId));
    }

    [Fact]
    public void Fail_NormalizesSupportedReasonAndRecordsOptionalLocation()
    {
        var delivery = CreateAssignedDelivery();

        delivery.Fail(
            EmployeeId,
            UtcNow.AddMinutes(1),
            " customer NOT available ",
            " Customer requested retry ",
            12.9716m,
            77.5946m);

        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Equal(DeliveryFailureReasons.CustomerNotAvailable, delivery.FailureReason);
        Assert.Equal("Customer requested retry", delivery.Remarks);
        Assert.Equal(12.9716m, delivery.FailureLatitude);
        Assert.Equal(77.5946m, delivery.FailureLongitude);
        Assert.False(delivery.IsTrackingActive);
    }

    [Fact]
    public void Fail_RejectsUnsupportedReasonPartialCoordinatesAndTerminalStates()
    {
        var delivery = CreateAssignedDelivery();

        Assert.Throws<ArgumentException>(() =>
            delivery.Fail(EmployeeId, UtcNow.AddMinutes(1), "Weather", null, null, null));
        Assert.Throws<ArgumentException>(() =>
            delivery.Fail(EmployeeId, UtcNow.AddMinutes(1), DeliveryFailureReasons.Other, null, 12m, null));

        delivery.Fail(EmployeeId, UtcNow.AddMinutes(1), DeliveryFailureReasons.VehicleIssue, null, null, null);
        Assert.Throws<InvalidOperationException>(() =>
            delivery.Fail(EmployeeId, UtcNow.AddMinutes(2), DeliveryFailureReasons.Other, null, null, null));
    }

    [Fact]
    public void DeliveryOtp_TracksAttemptsAndBlocksAtConfiguredLimit()
    {
        var otp = CreateOtp(maximumAttempts: 2);

        otp.RecordFailedAttempt(UtcNow.AddMinutes(1));
        otp.RecordFailedAttempt(UtcNow.AddMinutes(2));

        Assert.Equal(2, otp.AttemptCount);
        Assert.Throws<InvalidOperationException>(() => otp.EnsureVerifiable(UtcNow.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => otp.Consume(UtcNow.AddMinutes(3)));
    }

    [Fact]
    public void DeliveryOtp_ExpiresAtBoundaryAndCanOnlyBeConsumedOnce()
    {
        var expired = CreateOtp(expiresAtUtc: UtcNow.AddMinutes(5));
        Assert.Throws<InvalidOperationException>(() => expired.EnsureVerifiable(UtcNow.AddMinutes(5)));

        var consumed = CreateOtp();
        consumed.Consume(UtcNow.AddMinutes(1));

        Assert.Equal(UtcNow.AddMinutes(1), consumed.ConsumedAtUtc);
        Assert.Throws<InvalidOperationException>(() => consumed.Consume(UtcNow.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => consumed.RecordFailedAttempt(UtcNow.AddMinutes(2)));
    }

    [Fact]
    public void DeliveryOtp_RejectsInvalidConstructionAndNonUtcOperations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryOtp(0, "hash", UtcNow.AddMinutes(5), 3, UtcNow));
        Assert.Throws<ArgumentException>(() => new DeliveryOtp(1, " ", UtcNow.AddMinutes(5), 3, UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryOtp(1, "hash", UtcNow, 3, UtcNow));
        Assert.Throws<ArgumentException>(() =>
            new DeliveryOtp(1, "hash", DateTime.Now.AddMinutes(5), 3, DateTime.Now));

        var otp = CreateOtp();
        Assert.Throws<ArgumentException>(() => otp.EnsureVerifiable(DateTime.Now));
        Assert.Throws<ArgumentException>(() => otp.RecordFailedAttempt(DateTime.Now));
        Assert.Throws<ArgumentException>(() => otp.Consume(DateTime.Now));
    }

    [Fact]
    public void DeliveryLocation_CapturesValidReading()
    {
        var location = new DeliveryLocation(1, EmployeeId, 12.9716m, 77.5946m, 8.5m, UtcNow);

        Assert.Equal(1, location.DeliveryId);
        Assert.Equal(EmployeeId, location.EmployeeId);
        Assert.Equal(12.9716m, location.Latitude);
        Assert.Equal(77.5946m, location.Longitude);
        Assert.Equal(8.5m, location.AccuracyMetres);
        Assert.Equal(UtcNow, location.RecordedAtUtc);
    }

    [Fact]
    public void DeliveryLocation_RejectsInvalidIdentityCoordinatesAccuracyAndTimestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryLocation(0, EmployeeId, 0m, 0m, null, UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryLocation(1, 0, 0m, 0m, null, UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryLocation(1, EmployeeId, 90.1m, 0m, null, UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryLocation(1, EmployeeId, 0m, -180.1m, null, UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryLocation(1, EmployeeId, 0m, 0m, -0.1m, UtcNow));
        Assert.Throws<ArgumentException>(() => new DeliveryLocation(1, EmployeeId, 0m, 0m, null, DateTime.Now));
    }

    private static Delivery CreateAssignedDelivery()
    {
        var delivery = CreateOrderDelivery();
        delivery.Assign(EmployeeId, assignedByUserId: 30, UtcNow, null);
        return delivery;
    }

    private static DeliveryOtp CreateOtp(DateTime? expiresAtUtc = null, int maximumAttempts = 3) =>
        new(1, "hashed-delivery-code", expiresAtUtc ?? UtcNow.AddMinutes(10), maximumAttempts, UtcNow);

    private static Delivery CreateOrderDelivery(
        long orderId = 10,
        long customerId = 1,
        long branchId = 2,
        string referenceNumber = " DD-ORDER-001 ",
        decimal latitude = 12.9716m,
        decimal longitude = 77.5946m) =>
        Delivery.ForOrder(
            orderId,
            customerId,
            branchId,
            new DateOnly(2026, 8, 17),
            referenceNumber,
            " Customer Name ",
            " 9999999999 ",
            " 1 Main Road, Bengaluru ",
            " Leave at reception ",
            latitude,
            longitude);

    private static Delivery CreateSubscriptionDelivery() =>
        Delivery.ForSubscriptionOccurrence(
            subscriptionDeliveryId: 11,
            customerId: 1,
            branchId: 2,
            scheduledDate: new DateOnly(2026, 8, 17),
            referenceNumber: "SUB-001/20260817",
            customerName: "Customer Name",
            customerMobile: "9999999999",
            destinationAddress: "1 Main Road, Bengaluru",
            deliveryInstructions: null,
            destinationLatitude: 12.9716m,
            destinationLongitude: 77.5946m);
}
