using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 2 conditional ticket photo evidence: a confirmed failed delivery with
/// no product received is exempt (server-derived from the authoritative
/// delivery row); delivered-product claims require a photo enforced at the
/// authoritative approval gate. Client input can never influence the decision.
/// </summary>
public sealed class RefundReplacementConditionalEvidenceTests
{
    [Fact]
    public async Task FailedDelivery_NoPhoto_SubmitSucceedsAsExempt()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.FailAsync(harness.Delivery, harness.Staff.Id);

        var eligibility = await harness.Service.GetEligibilityAsync(
            harness.CustomerActor(), harness.Delivery.PublicId, CancellationToken.None);
        Assert.True(eligibility.IsEligible);
        Assert.False(eligibility.IsMilkTestRejectedFlow);
        Assert.False(eligibility.ProofImageRequired);
        Assert.True(eligibility.IsFailedDeliveryWithoutProduct);

        var result = await harness.SubmitAsync(harness.Delivery.PublicId);
        Assert.Equal(RefundReplacementStatus.Pending, result.Status);
        Assert.Equal(RefundReplacementSource.CustomerPostDelivery, result.Source);
        Assert.False(result.ProofImageRequired);
    }

    [Fact]
    public async Task FailedDelivery_NoPhoto_ApproveAndCompleteSucceed()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.FailAsync(harness.Delivery, harness.Staff.Id);
        var request = await harness.SubmitAsync(harness.Delivery.PublicId);

        var approved = await harness.Service.ApproveAsync(
            harness.StaffActor(),
            request.RequestId,
            new ApproveRefundReplacementRequest("Approved", ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);

        await harness.SeedSettlementAsync(harness.Order, harness.Customer.Id);
        var completed = await harness.Service.CompleteAsync(
            harness.StaffActor(),
            request.RequestId,
            new DecideRefundReplacementRequest("Refund processed"),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public async Task Delivered_NoPhoto_SubmitIsRejected_NoTicketCreated()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);

        var eligibility = await harness.Service.GetEligibilityAsync(
            harness.CustomerActor(), harness.Delivery.PublicId, CancellationToken.None);
        Assert.True(eligibility.ProofImageRequired);

        // Stage 2.1: the direct-API submit bypass is closed at submission —
        // no photo-required ticket row is created. The approval gate stays as
        // defense in depth for pre-rule rows (covered separately).
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.SubmitAsync(harness.Delivery.PublicId, withPhoto: false));
        Assert.Contains("proof image", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await harness.Db.RefundReplacementRequests.CountAsync());
    }

    [Fact]
    public async Task Delivered_WithPhoto_ApproveSucceeds()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);
        var request = await harness.SubmitAsync(harness.Delivery.PublicId);
        await harness.UploadAsync(request.RequestId, harness.Customer.Id);

        var approved = await harness.Service.ApproveAsync(
            harness.StaffActor(),
            request.RequestId,
            new ApproveRefundReplacementRequest("Approved", ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
    }

    [Fact]
    public async Task ExemptTicket_OptionalUpload_AcceptedAndValidated()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.FailAsync(harness.Delivery, harness.Staff.Id);
        var request = await harness.SubmitAsync(harness.Delivery.PublicId);

        var image = await harness.UploadAsync(request.RequestId, harness.Customer.Id);
        Assert.Equal("proof.jpg", image.FileName);
        Assert.Equal("image/jpeg", image.ContentType);

        var badType = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.UploadImageAsync(
                harness.Actor(harness.Customer.Id),
                request.RequestId,
                new MemoryStream([0x47, 0x49, 0x46], writable: false),
                "proof.gif",
                "image/gif",
                3,
                CancellationToken.None));
        Assert.Contains("JPEG", badType.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delivered_ClaimingNoProductInReasonText_StaysPhotoRequired()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);

        // The reason text is untrusted: the server derives the requirement
        // from the authoritative delivery row, not from what the customer wrote.
        // Stage 2.1: claiming non-receipt in prose no longer yields a ticket —
        // submission itself is rejected and nothing is created.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.SubmitAsync(
                harness.Delivery.PublicId,
                reason: "Delivery failed, I never received any product, missed call",
                withPhoto: false));
        Assert.Contains("proof image", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await harness.Db.RefundReplacementRequests.CountAsync());
    }

    [Fact]
    public async Task OtherCustomersDelivery_IsIndistinguishableFromMissing()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.DeliverAsync(harness.OtherDelivery, harness.Staff.Id);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.GetEligibilityAsync(
                harness.CustomerActor(), harness.OtherDelivery.PublicId, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.SubmitAsync(harness.OtherDelivery.PublicId));
    }

    [Fact]
    public async Task OtherCustomer_CannotReadOrAttachToTicket()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);
        var request = await harness.SubmitAsync(harness.Delivery.PublicId);

        Assert.Null(await harness.Service.GetForCustomerAsync(
            harness.Actor(harness.OtherCustomer.Id), request.RequestId, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.UploadAsync(request.RequestId, harness.OtherCustomer.Id));
        Assert.Empty(
            await harness.Service.ListForCustomerAsync(
                harness.Actor(harness.OtherCustomer.Id), CancellationToken.None));
    }

    [Fact]
    public async Task FailedDelivery_ReplacementRemainsRefused_RefundOnly()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.FailAsync(harness.Delivery, harness.Staff.Id);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.SubmitAsync(
                harness.CustomerActor(),
                new SubmitRefundReplacementRequest(
                    harness.Delivery.PublicId, RefundReplacementType.Replacement, "Want redelivery", null),
                CancellationToken.None));
        Assert.Contains("Only refund", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejection_NeverRequiresEvidence()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);
        var request = await harness.SubmitAsync(harness.Delivery.PublicId);

        var rejected = await harness.Service.RejectAsync(
            harness.StaffActor(),
            request.RequestId,
            new DecideRefundReplacementRequest("Insufficient detail"),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Rejected, rejected.Status);
    }

    [Fact]
    public async Task Employee_OwnOrder_FullSelfServiceSupportPath()
    {
        await using var harness = await EvidenceHarness.CreateAsync();
        await harness.FailAsync(harness.EmployeeDelivery, harness.Staff.Id);

        var eligibility = await harness.Service.GetEligibilityAsync(
            harness.Actor(harness.Employee.Id), harness.EmployeeDelivery.PublicId, CancellationToken.None);
        Assert.True(eligibility.IsEligible);
        Assert.False(eligibility.ProofImageRequired);

        var submitted = await harness.Service.SubmitAsync(
            harness.Actor(harness.Employee.Id),
            new SubmitRefundReplacementRequest(
                harness.EmployeeDelivery.PublicId, RefundReplacementType.Refund, "Missed the call", null),
            CancellationToken.None);
        Assert.False(submitted.ProofImageRequired);

        var mine = await harness.Service.ListForCustomerAsync(
            harness.Actor(harness.Employee.Id), CancellationToken.None);
        Assert.Single(mine);

        // Another customer's records stay invisible to the employee.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.GetEligibilityAsync(
                harness.Actor(harness.Employee.Id), harness.Delivery.PublicId, CancellationToken.None));
        Assert.Null(await harness.Service.GetForCustomerAsync(
            harness.Actor(harness.Employee.Id), Guid.NewGuid(), CancellationToken.None));
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);
        var customerTicket = await harness.SubmitAsync(harness.Delivery.PublicId);
        Assert.Null(await harness.Service.GetForCustomerAsync(
            harness.Actor(harness.Employee.Id), customerTicket.RequestId, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.UploadAsync(customerTicket.RequestId, harness.Employee.Id));
    }

    private sealed class EvidenceHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private EvidenceHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            Dictionary<string, byte[]> storage,
            RefundReplacementService service,
            User customer,
            User otherCustomer,
            User employee,
            User staff,
            User manager,
            Branch branch,
            Order order,
            Delivery delivery,
            Order otherOrder,
            Delivery otherDelivery,
            Order employeeOrder,
            Delivery employeeDelivery)
        {
            this.connection = connection;
            Db = db;
            Clock = clock;
            Storage = storage;
            Service = service;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Employee = employee;
            Staff = staff;
            Manager = manager;
            Branch = branch;
            Order = order;
            Delivery = delivery;
            OtherDelivery = otherDelivery;
            EmployeeDelivery = employeeDelivery;
            OtherOrder = otherOrder;
            EmployeeOrder = employeeOrder;
        }

        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public Dictionary<string, byte[]> Storage { get; }
        public RefundReplacementService Service { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public User Employee { get; }
        public User Staff { get; }
        public User Manager { get; }
        public Branch Branch { get; }
        public Order Order { get; }
        public Delivery Delivery { get; }
        public Order OtherOrder { get; }
        public Delivery OtherDelivery { get; }
        public Order EmployeeOrder { get; }
        public Delivery EmployeeDelivery { get; }

        public byte[] ImageBytes { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x01];

        public RefundReplacementActor Actor(long userId) =>
            new(userId, new HashSet<long>(), false);

        public RefundReplacementActor CustomerActor() => Actor(Customer.Id);

        public RefundReplacementActor StaffActor() =>
            new(Manager.Id, new HashSet<long> { Branch.Id }, false);

        // Stage 2.1: valid submits carry a photo for photo-required flows.
        // Pass withPhoto: false to assert the missing-evidence rejection.
        public Task<RefundReplacementRequestResult> SubmitAsync(
            Guid deliveryId, string reason = "Quality issue", bool withPhoto = true) =>
            Service.SubmitAsync(
                CustomerActor(),
                new SubmitRefundReplacementRequest(deliveryId, RefundReplacementType.Refund, reason, null),
                CancellationToken.None,
                withPhoto ? ProofImage() : null);

        public SubmitRefundReplacementImage ProofImage() =>
            new(new MemoryStream(ImageBytes, writable: false), "proof.jpg", "image/jpeg", ImageBytes.Length);

        public Task<RefundReplacementImageResult> UploadAsync(Guid requestId, long userId) =>
            Service.UploadImageAsync(
                Actor(userId),
                requestId,
                new MemoryStream(ImageBytes, writable: false),
                "proof.jpg",
                "image/jpeg",
                ImageBytes.Length,
                CancellationToken.None);

        public async Task DeliverAsync(Delivery delivery, long staffId)
        {
            delivery.Assign(staffId, staffId, Clock.Now, null);
            delivery.PickUp(staffId, Clock.Now, null);
            delivery.Start(staffId, Clock.Now);
            delivery.Arrive(staffId, Clock.Now);
            Clock.Advance(TimeSpan.FromMinutes(1));
            delivery.RecordOtpVerified(staffId, Clock.Now);
            Clock.Advance(TimeSpan.FromMinutes(1));
            delivery.Complete(staffId, Clock.Now, null);
            await Db.SaveChangesAsync();
        }

        public async Task FailAsync(Delivery delivery, long staffId)
        {
            delivery.Assign(staffId, staffId, Clock.Now, null);
            delivery.PickUp(staffId, Clock.Now, null);
            delivery.Start(staffId, Clock.Now);
            delivery.Arrive(staffId, Clock.Now);
            Clock.Advance(TimeSpan.FromMinutes(1));
            delivery.Fail(staffId, Clock.Now, DeliveryFailureReasons.CustomerNotAvailable, null, null, null);
            await Db.SaveChangesAsync();
        }

        public async Task SeedSettlementAsync(Order order, long customerId)
        {
            var payment = new Payment(
                order.Id,
                customerId,
                PaymentMethod.Wallet,
                order.PayableAmount,
                "INR",
                $"evidence-settlement-{Guid.NewGuid():N}",
                Clock.Now.AddMinutes(15));
            payment.Succeed(null, "WALLET", Clock.Now);
            var settlement = payment.StartRefund(
                order.PayableAmount, "Order refund settlement", $"evidence-refund-{Guid.NewGuid():N}", Staff.Id);
            settlement.Succeed(Clock.Now);
            payment.CompleteRefund(order.PayableAmount);
            Db.Payments.Add(payment);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public static async Task<EvidenceHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var customer = CreateUser(UserType.Customer, "Customer", "9999999999");
            var otherCustomer = CreateUser(UserType.Customer, "Other Customer", "9999999998");
            var employee = CreateUser(UserType.Employee, "Staff Shopper", "9888888888");
            var staff = CreateUser(UserType.Employee, "Assigned Staff", "9000000001");
            var manager = CreateUser(UserType.Employee, "Manager", "9000000002");
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            db.AddRange(customer, otherCustomer, employee, staff, manager, branch);
            db.NumberSeries.Add(new NumberSeries(
                RefundReplacementService.SeriesCode,
                "Customer refund/replacement request numbers",
                "RR/{NUMBER:000000}",
                1,
                1,
                NumberSeriesResetPolicy.Never));
            await db.SaveChangesAsync();

            var now = new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified);
            var order = await AddOrderAsync(db, customer, branch, "evidence-order-1", "ORD-EVD-001", now);
            var delivery = await AddDeliveryAsync(db, order, customer, staff, branch, "REF-EVD-001", "DLV-EVD-001", now);
            var otherOrder = await AddOrderAsync(db, otherCustomer, branch, "evidence-order-2", "ORD-EVD-002", now);
            var otherDelivery = await AddDeliveryAsync(db, otherOrder, otherCustomer, staff, branch, "REF-EVD-002", "DLV-EVD-002", now);
            var employeeOrder = await AddOrderAsync(db, employee, branch, "evidence-order-3", "ORD-EVD-003", now);
            var employeeDelivery = await AddDeliveryAsync(db, employeeOrder, employee, staff, branch, "REF-EVD-003", "DLV-EVD-003", now);

            var clock = new TestClock(now.AddMinutes(1));
            var storage = new Dictionary<string, byte[]>();
            var service = new RefundReplacementService(
                db,
                clock,
                new RefundReplacementConfigurationService(db, clock),
                new NumberSeriesService(db, clock),
                new CapturingStorage(storage),
                new TestNotificationEventWriter(db, clock));
            return new EvidenceHarness(
                connection, db, clock, storage, service,
                customer, otherCustomer, employee, staff, manager, branch,
                order, delivery, otherOrder, otherDelivery, employeeOrder, employeeDelivery);
        }

        private static User CreateUser(UserType type, string name, string mobile)
        {
            var user = new User(type);
            user.SetProfile(name);
            user.SetContact(mobile, null);
            return user;
        }

        private static async Task<Order> AddOrderAsync(
            DoodhDirectDbContext db, User customer, Branch branch, string idempotencyKey, string orderNumber, DateTime now)
        {
            var order = new Order(
                customer.Id, null, branch.Id, idempotencyKey, orderNumber,
                160m, 0m, branch.Code, branch.Name,
                "Home", "1 Main Road", null, "Central", "Bengaluru", "Karnataka", "560001",
                null, null, "Customer", customer.Mobile ?? "9999999999", 12.9716m, 77.5946m);
            order.ConfirmPayment();
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return order;
        }

        private static async Task<Delivery> AddDeliveryAsync(
            DoodhDirectDbContext db, Order order, User customer, User staff, Branch branch,
            string reference, string deliveryNumber, DateTime now)
        {
            var delivery = Delivery.ForOrder(
                order.Id, customer.Id, branch.Id, DateOnly.FromDateTime(now), reference,
                "Customer", customer.Mobile ?? "9999999999",
                "1 Main Road, Central, Bengaluru, Karnataka 560001", null, 12.9716m, 77.5946m);
            delivery.AssignDeliveryNumber(deliveryNumber);
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();
            return delivery;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private sealed class CapturingStorage(Dictionary<string, byte[]> files) : IMediaStorage
        {
            public async Task<StoredMediaResult> SaveAsync(
                string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
            {
                using var buffered = new MemoryStream();
                await content.CopyToAsync(buffered, cancellationToken);
                files[storageKey] = buffered.ToArray();
                return new StoredMediaResult(storageKey, contentType, buffered.Length);
            }

            public Task<StoredMediaContent> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
                files.TryGetValue(storageKey, out var content)
                    ? Task.FromResult(new StoredMediaContent(
                        new MemoryStream(content, writable: false), "image/jpeg", content.Length))
                    : throw new NotFoundException("The media was not found.");

            public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken)
            {
                files.Remove(storageKey);
                return Task.CompletedTask;
            }
        }
    }
}
