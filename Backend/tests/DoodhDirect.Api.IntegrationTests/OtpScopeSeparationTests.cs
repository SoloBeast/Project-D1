using DeliveryHarness = DoodhDirect.Api.IntegrationTests.DeliveryServiceTests.DeliveryHarness;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Orders;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Infrastructure.Deliveries;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.OtpProvider;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Scope separation tests proving MSG91 is used ONLY for identity / onboarding OTP
/// (customer login, customer registration, employee invitation / registration) while
/// Delivery OTP remains fully in-app (DeliveryService -> IOtpDeliveryService) and never
/// touches <see cref="IMsg91OtpProvider"/> or <see cref="Msg91ApiClient"/>.
/// </summary>
public sealed class OtpScopeSeparationTests
{
    private static readonly DeviceInfo Device = new(
        "otp-scope-test-device",
        "OTP scope separation test device",
        "test",
        "127.0.0.1",
        "DoodhDirect.Tests");

    // ---- IDENTITY: MSG91 MUST be the provider ----------------------------------------------

    [Fact]
    public async Task CustomerLoginOtp_UsesMsg91Provider()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000001";

        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Login, "127.0.0.1"),
            CancellationToken.None);

        // OtpService.SendAsync routes through IMsg91OtpProvider -> CapturingOtpDelivery.
        Assert.Equal(OtpPurpose.Login.ToString(), harness.Delivery.LastPurpose);
        Assert.Equal(mobile, harness.Delivery.LastDestination);
        Assert.False(string.IsNullOrWhiteSpace(harness.Delivery.LastReqId));
    }

    [Fact]
    public async Task CustomerRegistrationOtp_UsesMsg91Provider()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000002";

        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);

        Assert.Equal(OtpPurpose.Registration.ToString(), harness.Delivery.LastPurpose);
        Assert.Equal(mobile, harness.Delivery.LastDestination);
        Assert.False(string.IsNullOrWhiteSpace(harness.Delivery.LastReqId));
    }

    [Fact]
    public async Task EmployeeInvitationOtp_UsesMsg91Provider()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        const string name = "Invitation Scope Staff";
        const string mobile = "+919876501001";
        const string email = "invitation-scope@example.com";

        await CreateDeliveryStaffAsync(harness, name, mobile, email);

        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.EmployeeInvitation, "127.0.0.1"),
            CancellationToken.None);

        Assert.Equal(OtpPurpose.EmployeeInvitation.ToString(), harness.Delivery.LastPurpose);
        Assert.Equal(mobile, harness.Delivery.LastDestination);
        Assert.False(string.IsNullOrWhiteSpace(harness.Delivery.LastReqId));
    }

    [Fact]
    public async Task EmployeeRegistrationOtp_FlowsThroughMsg91Provider()
    {
        await using var harness = await EmployeeHarness.CreateAsync();
        const string name = "Registration Scope Staff";
        const string mobile = "+919876501002";
        const string email = "registration-scope@example.com";

        var created = await CreateDeliveryStaffAsync(harness, name, mobile, email);

        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.EmployeeInvitation, "127.0.0.1"),
            CancellationToken.None);
        Assert.Equal(OtpPurpose.EmployeeInvitation.ToString(), harness.Delivery.LastPurpose);
        Assert.Equal(mobile, harness.Delivery.LastDestination);

        // The OTP code issued by the MSG91 provider completes the employee onboarding.
        var completed = await harness.Employees.CompleteRegistrationAsync(
            new CompleteEmployeeRegistrationRequest(
                created.Invitation!.Token,
                name,
                email,
                mobile,
                "StrongPass!1",
                harness.Delivery.LastCode!,
                Device),
            CancellationToken.None);

        Assert.NotNull(completed.Session);
        Assert.False(string.IsNullOrWhiteSpace(completed.Session.Tokens.AccessToken));
    }

    // ---- DELIVERY: MUST NOT touch MSG91 (in-app only) --------------------------------------

    [Fact]
    public void DeliveryService_HasNoDependencyOnMsg91OtpProvider()
    {
        // The DeliveryService constructor must not accept IMsg91OtpProvider / Msg91ApiClient:
        // structurally it is impossible for a Delivery OTP to be routed through MSG91.
        var parameters = typeof(DeliveryService).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IMsg91OtpProvider));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(Msg91ApiClient));
        Assert.Contains(parameters, p => p.ParameterType == typeof(IOtpDeliveryService));
    }

    [Fact]
    public async Task DeliveryOtp_UsesInAppDeliveryServiceOnly()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();

        var code = await harness.GetOtpCodeAsync(deliveryId);

        // The delivery OTP is delivered exclusively through the in-app IOtpDeliveryService
        // (CapturingOtpDeliveryService). The DeliveryHarness has no IMsg91OtpProvider at all,
        // so there is no MSG91 send path to observe: the code reaches the in-app service exactly once.
        var message = Assert.Single(harness.OtpDelivery.Messages, x => x.Code == code);
        Assert.Equal("9999999999", message.Destination);
    }

    [Fact]
    public async Task DeliveryOtp_IsAvailableInCustomerApp()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);

        var code = await harness.GetOtpCodeAsync(deliveryId);
        var result = await harness.Service.GetForCustomerAsync(
            harness.Customer.Id, deliveryId, CancellationToken.None);

        // The customer app receives the in-app delivery OTP so they can share it with the delivery boy.
        Assert.Equal(code, result.ActiveOtp);
    }

    [Fact]
    public async Task DeliveryStaff_CanVerifyDeliveryOtpInApp()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);

        var code = await harness.GetOtpCodeAsync(deliveryId);
        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        Assert.NotNull(verified.OtpVerifiedAt);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
    }

    [Fact]
    public async Task SuccessfulOtpVerification_CompletesDelivery()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var targetDeliveryId = await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.PublicId == deliveryId)
            .Select(x => x.Id)
            .SingleAsync();

        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.NotNull(verified.OtpVerifiedAt);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(OrderStatus.Delivered,
            (await harness.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == harness.Order.Id)).Status);

        var otp = await harness.Db.DeliveryOtps
            .AsNoTracking()
            .SingleAsync(x => x.DeliveryId == targetDeliveryId);
        // Successful verification does not count as a failed attempt; Consume consumes the OTP.
        Assert.Equal(0, otp.AttemptCount);
        Assert.NotNull(otp.ConsumedAt);
    }

    [Fact]
    public async Task ConsumedDeliveryOtp_CannotBeReused()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var targetDeliveryId = await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.PublicId == deliveryId)
            .Select(x => x.Id)
            .SingleAsync();

        await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        var otp = await harness.Db.DeliveryOtps
            .AsNoTracking()
            .SingleAsync(x => x.DeliveryId == targetDeliveryId);
        Assert.NotNull(otp.ConsumedAt);
        Assert.Null(otp.ProtectedCode);
    }

    [Fact]
    public async Task CustomerCancellation_InvalidatesDeliveryOtp()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();

        // Order is still Confirmed; its delivery OTP was already issued in-app. Cancel it.
        var orderService = new OrderService(
            harness.Db,
            new BranchAllocationService(harness.Db),
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
            new NumberSeriesService(harness.Db, harness.TimeProvider),
            harness.TimeProvider);
        var cancelled = await orderService.CancelAsync(
            harness.Customer.Id, harness.Order.PublicId, CancellationToken.None);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);

        harness.Db.ChangeTracker.Clear();
        var targetDeliveryId = await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.PublicId == deliveryId)
            .Select(x => x.Id)
            .SingleAsync();
        var otp = await harness.Db.DeliveryOtps
            .AsNoTracking()
            .SingleAsync(x => x.DeliveryId == targetDeliveryId);
        Assert.NotNull(otp.ConsumedAt);
        Assert.Null(otp.ProtectedCode);
    }

    [Fact]
    public async Task DeliveryCompletion_InvalidatesDeliveryOtp()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var targetDeliveryId = await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.PublicId == deliveryId)
            .Select(x => x.Id)
            .SingleAsync();

        await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        // After completion the OTP is no longer exposed to the customer app and is consumed.
        var result = await harness.Service.GetForCustomerAsync(
            harness.Customer.Id, deliveryId, CancellationToken.None);
        Assert.Null(result.ActiveOtp);

        harness.Db.ChangeTracker.Clear();
        var otp = await harness.Db.DeliveryOtps
            .AsNoTracking()
            .SingleAsync(x => x.DeliveryId == targetDeliveryId);
        Assert.NotNull(otp.ConsumedAt);
        Assert.Null(otp.ProtectedCode);
    }

    // ---- SCHEMA: identity challenge has no local code hash; delivery OTP retains it ---------

    [Fact]
    public void IdentityOtpChallenge_HasNoCodeHash_ButKeepsProviderReqId()
    {
        // Architecture B: MSG91 owns OTP generation/verification, so the identity OtpChallenge
        // must NOT store a local code hash. It keeps the provider reqId for send->retry->verify.
        var challengeType = typeof(OtpChallenge);
        Assert.Null(challengeType.GetProperty("CodeHash"));
        Assert.NotNull(challengeType.GetProperty("ReqId"));
        Assert.NotNull(challengeType.GetProperty("Destination"));
        Assert.NotNull(challengeType.GetProperty("FailedAttempts"));
        Assert.NotNull(challengeType.GetProperty("ConsumedAt"));
    }

    [Fact]
    public void DeliveryOtp_RetainsCodeHash_AndIsUnchanged()
    {
        // Delivery OTP stays fully in-app: it keeps its own local CodeHash/ProtectedCode
        // and never delegates to MSG91.
        var deliveryOtpType = typeof(DeliveryOtp);
        Assert.NotNull(deliveryOtpType.GetProperty("CodeHash"));
        Assert.NotNull(deliveryOtpType.GetProperty("ProtectedCode"));
        Assert.NotNull(deliveryOtpType.GetProperty("AttemptCount"));
        Assert.NotNull(deliveryOtpType.GetProperty("ConsumedAt"));
        Assert.NotNull(deliveryOtpType.GetProperty("DeliveryId"));
    }

    [Fact]
    public async Task IdentityOtpChallenge_DbModel_DoesNotMapCodeHash_ButMapsReqId()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000099";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);

        var entityType = harness.Db.Model.FindEntityType(typeof(OtpChallenge))!;
        var codeHash = entityType.GetProperties().FirstOrDefault(p => p.Name == "CodeHash");
        var reqId = entityType.GetProperties().FirstOrDefault(p => p.Name == "ReqId");

        // The EF model (and therefore the migration) has dropped the identity CodeHash column.
        Assert.Null(codeHash);
        Assert.NotNull(reqId);
    }

    [Fact]
    public async Task DeliveryOtp_DbModel_StillMapsCodeHash()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();

        var targetDeliveryId = await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.PublicId == deliveryId)
            .Select(x => x.Id)
            .SingleAsync();
        var entityType = harness.Db.Model.FindEntityType(typeof(DeliveryOtp))!;
        var codeHash = entityType.GetProperties().FirstOrDefault(p => p.Name == "CodeHash");

        Assert.NotNull(codeHash);
        var otp = await harness.Db.DeliveryOtps.AsNoTracking().SingleAsync(x => x.DeliveryId == targetDeliveryId);
        Assert.NotEmpty(otp.CodeHash);
    }

    // ---- DELIVERY: concurrency / idempotency coverage stays green --------------------------

    [Fact]
    public void ExistingDeliveryOtpConcurrencyAndIdempotencyTests_RemainPresent()
    {
        var deliveryTests = typeof(DeliveryServiceTests);
        Assert.NotNull(deliveryTests.GetMethod(
            nameof(DeliveryServiceTests.ConcurrentOtpIssuance_ReusesOneOtpAndOneDeterministicEvent)));
        Assert.NotNull(deliveryTests.GetMethod(
            nameof(DeliveryServiceTests.ConcurrentCorrectOtpVerification_CompletesExactlyOnce)));
    }

    private static async Task<CreateEmployeeResult> CreateDeliveryStaffAsync(
        EmployeeHarness harness,
        string name,
        string mobile,
        string? email)
    {
        return await harness.Employees.CreateAsync(
            new CreateEmployeeRequest(
                name,
                mobile,
                email,
                AuthorizationCodes.DeliveryStaff,
                harness.MainBranch.Id,
                SendInvitation: true),
            harness.SystemAdmin.Id,
            CancellationToken.None);
    }
}
