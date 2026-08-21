using DoodhDirect.Domain.Deliveries;

namespace DoodhDirect.Domain.Tests;

public sealed class DeliveryDomainTests
{
    private const long EmployeeId = 20;
    private const long OtherEmployeeId = 21;
    private static readonly DateTime IndiaLocalNow = new(2026, 8, 16, 9, 30, 0, DateTimeKind.Unspecified);

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

        var initial = delivery.Assign(EmployeeId, assignedByUserId: 30, IndiaLocalNow, " Morning route ");
        var reassignedAt = IndiaLocalNow.AddMinutes(2);
        var reassignment = delivery.Assign(OtherEmployeeId, assignedByUserId: 31, reassignedAt, "Coverage");

        Assert.Equal(DeliveryStatus.Assigned, delivery.Status);
        Assert.Equal(OtherEmployeeId, delivery.AssignedEmployeeId);
        Assert.Equal(reassignedAt, delivery.AssignedAt);
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
        delivery.PickUp(EmployeeId, IndiaLocalNow.AddMinutes(1), null);

        Assert.Throws<InvalidOperationException>(() =>
            delivery.Assign(OtherEmployeeId, assignedByUserId: 30, IndiaLocalNow.AddMinutes(2), null));
    }

    [Fact]
    public void Lifecycle_RecordsTransitionsAndRequiresVerifiedOtpForCompletion()
    {
        var delivery = CreateAssignedDelivery();

        delivery.PickUp(EmployeeId, IndiaLocalNow.AddMinutes(1), " Handle carefully ");
        delivery.Start(EmployeeId, IndiaLocalNow.AddMinutes(2));
        delivery.Arrive(EmployeeId, IndiaLocalNow.AddMinutes(3));

        Assert.True(delivery.IsTrackingActive);
        Assert.Throws<InvalidOperationException>(() =>
            delivery.Complete(EmployeeId, IndiaLocalNow.AddMinutes(4), null));

        delivery.RecordOtpVerified(EmployeeId, IndiaLocalNow.AddMinutes(4));
        delivery.Complete(EmployeeId, IndiaLocalNow.AddMinutes(5), " Handed to customer ");

        Assert.Equal(DeliveryStatus.Delivered, delivery.Status);
        Assert.Equal("Handle carefully", delivery.OperationalNotes);
        Assert.Equal("Handed to customer", delivery.Remarks);
        Assert.Equal(IndiaLocalNow.AddMinutes(5), delivery.CompletedAt);
        Assert.False(delivery.IsTrackingActive);
    }

    [Fact]
    public void Operations_RejectEmployeeWhoIsNotCurrentlyAssigned()
    {
        var delivery = CreateAssignedDelivery();

        Assert.Throws<InvalidOperationException>(() =>
            delivery.PickUp(OtherEmployeeId, IndiaLocalNow.AddMinutes(1), null));

        delivery.PickUp(EmployeeId, IndiaLocalNow.AddMinutes(1), null);
        delivery.Start(EmployeeId, IndiaLocalNow.AddMinutes(2));

        Assert.Throws<InvalidOperationException>(() =>
            delivery.Arrive(OtherEmployeeId, IndiaLocalNow.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() =>
            delivery.EnsureCanRecordLocation(OtherEmployeeId));
        Assert.Throws<InvalidOperationException>(() =>
            delivery.Fail(OtherEmployeeId, IndiaLocalNow.AddMinutes(3), DeliveryFailureReasons.Other, null, null, null));
    }

    [Fact]
    public void Lifecycle_RejectsOutOfOrderAndNonIndiaLocalTransitions()
    {
        var delivery = CreateAssignedDelivery();

        Assert.Throws<InvalidOperationException>(() => delivery.Start(EmployeeId, IndiaLocalNow));
        Assert.Throws<ArgumentException>(() => delivery.PickUp(EmployeeId, DateTime.Now, null));
    }

    [Fact]
    public void Tracking_IsAllowedOnlyDuringOutForDeliveryAndArrived()
    {
        var delivery = CreateAssignedDelivery();

        Assert.Throws<InvalidOperationException>(() => delivery.EnsureCanRecordLocation(EmployeeId));
        delivery.PickUp(EmployeeId, IndiaLocalNow.AddMinutes(1), null);
        Assert.Throws<InvalidOperationException>(() => delivery.EnsureCanRecordLocation(EmployeeId));

        delivery.Start(EmployeeId, IndiaLocalNow.AddMinutes(2));
        delivery.EnsureCanRecordLocation(EmployeeId);
        delivery.Arrive(EmployeeId, IndiaLocalNow.AddMinutes(3));
        delivery.EnsureCanRecordLocation(EmployeeId);

        delivery.Fail(EmployeeId, IndiaLocalNow.AddMinutes(4), DeliveryFailureReasons.Other, null, null, null);
        Assert.False(delivery.IsTrackingActive);
        Assert.Throws<InvalidOperationException>(() => delivery.EnsureCanRecordLocation(EmployeeId));
    }

    [Fact]
    public void Fail_NormalizesSupportedReasonAndRecordsOptionalLocation()
    {
        var delivery = CreateAssignedDelivery();

        delivery.Fail(
            EmployeeId,
            IndiaLocalNow.AddMinutes(1),
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
            delivery.Fail(EmployeeId, IndiaLocalNow.AddMinutes(1), "Weather", null, null, null));
        Assert.Throws<ArgumentException>(() =>
            delivery.Fail(EmployeeId, IndiaLocalNow.AddMinutes(1), DeliveryFailureReasons.Other, null, 12m, null));

        delivery.Fail(EmployeeId, IndiaLocalNow.AddMinutes(1), DeliveryFailureReasons.VehicleIssue, null, null, null);
        Assert.Throws<InvalidOperationException>(() =>
            delivery.Fail(EmployeeId, IndiaLocalNow.AddMinutes(2), DeliveryFailureReasons.Other, null, null, null));
    }

    [Fact]
    public void DeliveryOtp_TracksAttemptsAndBlocksAtConfiguredLimit()
    {
        var otp = CreateOtp(maximumAttempts: 2);

        otp.RecordFailedAttempt(IndiaLocalNow.AddMinutes(1));
        otp.RecordFailedAttempt(IndiaLocalNow.AddMinutes(2));

        Assert.Equal(2, otp.AttemptCount);
        Assert.Throws<InvalidOperationException>(() => otp.EnsureVerifiable(IndiaLocalNow.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => otp.Consume(IndiaLocalNow.AddMinutes(3)));
    }

    [Fact]
    public void DeliveryOtp_ExpiresAtBoundaryAndCanOnlyBeConsumedOnce()
    {
        var expired = CreateOtp(expiresAt: IndiaLocalNow.AddMinutes(5));
        Assert.Throws<InvalidOperationException>(() => expired.EnsureVerifiable(IndiaLocalNow.AddMinutes(5)));

        var consumed = CreateOtp();
        consumed.Consume(IndiaLocalNow.AddMinutes(1));

        Assert.Equal(IndiaLocalNow.AddMinutes(1), consumed.ConsumedAt);
        Assert.Throws<InvalidOperationException>(() => consumed.Consume(IndiaLocalNow.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => consumed.RecordFailedAttempt(IndiaLocalNow.AddMinutes(2)));
    }

    [Fact]
    public void DeliveryOtp_RejectsInvalidConstructionAndNonIndiaLocalOperations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryOtp(0, "hash", IndiaLocalNow.AddMinutes(5), 3, IndiaLocalNow));
        Assert.Throws<ArgumentException>(() => new DeliveryOtp(1, " ", IndiaLocalNow.AddMinutes(5), 3, IndiaLocalNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryOtp(1, "hash", IndiaLocalNow, 3, IndiaLocalNow));
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
        var location = new DeliveryLocation(1, EmployeeId, 12.9716m, 77.5946m, 8.5m, IndiaLocalNow);

        Assert.Equal(1, location.DeliveryId);
        Assert.Equal(EmployeeId, location.EmployeeId);
        Assert.Equal(12.9716m, location.Latitude);
        Assert.Equal(77.5946m, location.Longitude);
        Assert.Equal(8.5m, location.AccuracyMetres);
        Assert.Equal(IndiaLocalNow, location.RecordedAt);
    }

    [Fact]
    public void DeliveryLocation_RejectsInvalidIdentityCoordinatesAccuracyAndTimestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryLocation(0, EmployeeId, 0m, 0m, null, IndiaLocalNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryLocation(1, 0, 0m, 0m, null, IndiaLocalNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryLocation(1, EmployeeId, 90.1m, 0m, null, IndiaLocalNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryLocation(1, EmployeeId, 0m, -180.1m, null, IndiaLocalNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryLocation(1, EmployeeId, 0m, 0m, -0.1m, IndiaLocalNow));
        Assert.Throws<ArgumentException>(() => new DeliveryLocation(1, EmployeeId, 0m, 0m, null, DateTime.Now));
    }

    private static Delivery CreateAssignedDelivery()
    {
        var delivery = CreateOrderDelivery();
        delivery.Assign(EmployeeId, assignedByUserId: 30, IndiaLocalNow, null);
        return delivery;
    }

    private static DeliveryOtp CreateOtp(DateTime? expiresAt = null, int maximumAttempts = 3) =>
        new(1, "hashed-delivery-code", expiresAt ?? IndiaLocalNow.AddMinutes(10), maximumAttempts, IndiaLocalNow);

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
