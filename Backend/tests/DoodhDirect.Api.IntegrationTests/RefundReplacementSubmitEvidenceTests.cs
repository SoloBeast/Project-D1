using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
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
/// Stage 2.1: mandatory evidence is enforced at submission, not just approval.
/// A photo-required ticket cannot enter the normal submitted state without
/// valid evidence bound atomically in the submission transaction. Ownership,
/// delivery state and the requirement itself are always server-derived; the
/// image bytes ride with the call under the authenticated actor, so there is
/// no attachment reference to forge and no other user's blob to steal.
/// </summary>
public sealed class RefundReplacementSubmitEvidenceTests
{
    [Fact]
    public async Task Delivered_NoPhoto_DirectSubmit_RejectedWithoutTicket()
    {
        await using var harness = await SubmitHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.SubmitAsync(harness.Delivery.PublicId, withPhoto: false));
        Assert.Contains("proof image", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await harness.Db.RefundReplacementRequests.CountAsync());
        Assert.Empty(harness.Files);
    }

    [Fact]
    public async Task Delivered_ValidPhoto_SubmitSucceedsWithImageAttached()
    {
        await using var harness = await SubmitHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);

        var result = await harness.SubmitAsync(harness.Delivery.PublicId);

        Assert.Equal(RefundReplacementStatus.Pending, result.Status);
        Assert.True(result.ProofImageRequired);
        var detail = await harness.Db.RefundReplacementRequests
            .Include(x => x.Images)
            .SingleAsync();
        var image = Assert.Single(detail.Images);
        Assert.Equal("proof.jpg", image.FileName);
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.Equal(harness.ImageBytes.Length, image.FileSize);
        Assert.Equal(harness.Customer.Id, image.UploadedByUserId);
        Assert.Single(harness.Files);
        Assert.Equal(harness.ImageBytes, harness.Files[image.StorageKey]);
    }

    [Theory]
    [InlineData("image/gif", 5, "The proof image must be a JPEG, PNG or WebP image.")]
    [InlineData("image/jpeg", 0, "The proof image is empty.")]
    [InlineData("image/jpeg", 10485761, "The proof image cannot exceed 10 MB.")]
    public async Task Delivered_InvalidPhoto_SubmitRejected_NothingPersisted(
        string contentType, long fileSize, string expectedMessage)
    {
        await using var harness = await SubmitHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);

        var error = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.SubmitAsync(
                harness.CustomerActor(),
                new SubmitRefundReplacementRequest(
                    harness.Delivery.PublicId, RefundReplacementType.Refund, "Quality issue", null),
                CancellationToken.None,
                new SubmitRefundReplacementImage(
                    new MemoryStream(harness.ImageBytes, writable: false),
                    "proof.jpg",
                    contentType,
                    fileSize)));
        Assert.Equal(expectedMessage, error.Message);
        Assert.Equal(0, await harness.Db.RefundReplacementRequests.CountAsync());
        Assert.Empty(harness.Files);
    }

    [Fact]
    public async Task Delivered_SizeMismatch_SubmitRollsBackBlob()
    {
        await using var harness = await SubmitHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);
        harness.Storage.ReportedSizeOffset = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.SubmitAsync(harness.Delivery.PublicId));

        Assert.Equal(0, await harness.Db.RefundReplacementRequests.CountAsync());
        Assert.Empty(harness.Files);
        Assert.Single(harness.Storage.DeletedKeys);
    }

    [Fact]
    public async Task FailedDelivery_NoPhoto_SubmitSucceeds()
    {
        await using var harness = await SubmitHarness.CreateAsync();
        await harness.FailAsync(harness.Delivery, harness.Staff.Id);

        var result = await harness.SubmitAsync(harness.Delivery.PublicId, withPhoto: false);

        Assert.Equal(RefundReplacementStatus.Pending, result.Status);
        Assert.False(result.ProofImageRequired);
        Assert.Empty(harness.Files);
    }

    [Fact]
    public async Task FailedDelivery_OptionalPhoto_SubmitSucceedsWithImage()
    {
        await using var harness = await SubmitHarness.CreateAsync();
        await harness.FailAsync(harness.Delivery, harness.Staff.Id);

        var result = await harness.SubmitAsync(harness.Delivery.PublicId);

        Assert.Equal(RefundReplacementStatus.Pending, result.Status);
        Assert.False(result.ProofImageRequired);
        Assert.Single(
            await harness.Db.RefundReplacementImages.ToArrayAsync());
    }

    [Fact]
    public async Task MilkTestFlow_PhotoAtSubmit_Rejected()
    {
        await using var harness = await SubmitHarness.CreateAsync();
        await harness.CreateRejectedMilkTestOnFailedDeliveryAsync();

        // Explicit reference with a photo: the flow is upload-closed.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.SubmitAsync(harness.Delivery.PublicId));
        Assert.Contains("not required", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await harness.Db.RefundReplacementRequests.CountAsync());
        Assert.Empty(harness.Files);
    }

    [Fact]
    public async Task OtherCustomersDelivery_SubmitWithPhoto_NotFound()
    {
        await using var harness = await SubmitHarness.CreateAsync();
        await harness.DeliverAsync(harness.OtherDelivery, harness.Staff.Id);

        // A valid photo cannot launder access to another customer's delivery:
        // the delivery lookup is scoped to the authenticated owner.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.SubmitAsync(harness.OtherDelivery.PublicId));
        Assert.Equal(0, await harness.Db.RefundReplacementRequests.CountAsync());
        Assert.Empty(harness.Files);
    }

    [Fact]
    public async Task DuplicateSubmitWithPhoto_Conflict_SingleTicketSingleBlob()
    {
        await using var harness = await SubmitHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);

        var first = await harness.SubmitAsync(harness.Delivery.PublicId);
        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.SubmitAsync(harness.Delivery.PublicId));
        Assert.Contains("active", error.Message, StringComparison.OrdinalIgnoreCase);

        // The retry stored nothing new: one ticket, one image, one blob.
        Assert.Single(await harness.Db.RefundReplacementRequests.ToArrayAsync());
        Assert.Single(await harness.Db.RefundReplacementImages.ToArrayAsync());
        Assert.Single(harness.Files);
        Assert.Empty(harness.Storage.DeletedKeys);
        Assert.Equal(first.RequestId, (await harness.Db.RefundReplacementRequests.SingleAsync()).PublicId);
    }

    [Fact]
    public async Task ApprovalGate_RejectsPreRuleTicketWithoutEvidence()
    {
        await using var harness = await SubmitHarness.CreateAsync();
        await harness.DeliverAsync(harness.Delivery, harness.Staff.Id);

        // A photo-required ticket row predating submit-time enforcement (or
        // written around it): the approval gate still refuses it.
        var entity = new RefundReplacementRequest(
            harness.Order.Id,
            harness.Delivery.Id,
            harness.Customer.Id,
            harness.Branch.Id,
            null,
            RefundReplacementType.Refund,
            RefundReplacementSource.CustomerPostDelivery,
            "Quality issue",
            null,
            harness.Clock.Now,
            harness.Clock.Now.AddHours(48));
        entity.AssignRequestNumber("RR/000099");
        harness.Db.RefundReplacementRequests.Add(entity);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.ApproveAsync(
                harness.StaffActor(),
                entity.PublicId,
                new ApproveRefundReplacementRequest("Approved", ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
                CancellationToken.None));
        Assert.Contains("proof image", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            RefundReplacementStatus.Pending,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    private sealed class SubmitHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private SubmitHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            CapturingMediaStorage storage,
            RefundReplacementService service,
            User customer,
            User otherCustomer,
            User staff,
            User manager,
            Branch branch,
            Order order,
            Delivery delivery,
            Order otherOrder,
            Delivery otherDelivery)
        {
            this.connection = connection;
            Db = db;
            Clock = clock;
            Storage = storage;
            Files = storage.Files;
            Service = service;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Staff = staff;
            Manager = manager;
            Branch = branch;
            Order = order;
            Delivery = delivery;
            OtherOrder = otherOrder;
            OtherDelivery = otherDelivery;
        }

        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public CapturingMediaStorage Storage { get; }
        public Dictionary<string, byte[]> Files { get; }
        public RefundReplacementService Service { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public User Staff { get; }
        public User Manager { get; }
        public Branch Branch { get; }
        public Order Order { get; }
        public Delivery Delivery { get; }
        public Order OtherOrder { get; }
        public Delivery OtherDelivery { get; }

        public byte[] ImageBytes { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x01];

        public RefundReplacementActor CustomerActor() => new(Customer.Id, new HashSet<long>(), false);

        public RefundReplacementActor StaffActor() =>
            new(Manager.Id, new HashSet<long> { Branch.Id }, false);

        public Task<RefundReplacementRequestResult> SubmitAsync(
            Guid deliveryId, string reason = "Quality issue", bool withPhoto = true) =>
            Service.SubmitAsync(
                CustomerActor(),
                new SubmitRefundReplacementRequest(deliveryId, RefundReplacementType.Refund, reason, null),
                CancellationToken.None,
                withPhoto ? ProofImage() : null);

        public SubmitRefundReplacementImage ProofImage() =>
            new(new MemoryStream(ImageBytes, writable: false), "proof.jpg", "image/jpeg", ImageBytes.Length);

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

        public async Task<MilkTest> CreateRejectedMilkTestOnFailedDeliveryAsync()
        {
            Delivery.Assign(Staff.Id, Staff.Id, Clock.Now, null);
            await Db.SaveChangesAsync();

            var milkTest = new MilkTest(Delivery.Id, Customer.Id, Branch.Id, Staff.Id, Clock.Now);
            Db.MilkTests.Add(milkTest);
            await Db.SaveChangesAsync();

            milkTest.AddParameter("FAT", "Fat", 4.2m, "%");
            await Db.SaveChangesAsync();

            milkTest.AddImage(new MilkTestImage(
                milkTest.Id,
                $"milk-tests/{milkTest.Id}.jpg",
                "proof.jpg",
                "image/jpeg",
                5,
                Staff.Id,
                Clock.Now));
            await Db.SaveChangesAsync();

            Clock.Advance(TimeSpan.FromMinutes(1));
            milkTest.Complete(Staff.Id, Clock.Now, null);
            await Db.SaveChangesAsync();

            Clock.Advance(TimeSpan.FromMinutes(1));
            milkTest.Reject(Clock.Now, "The milk did not meet expectations");
            Delivery.FailForCustomerMilkTest(Clock.Now, milkTest.CustomerRemarks);
            await Db.SaveChangesAsync();
            return milkTest;
        }

        public static async Task<SubmitHarness> CreateAsync()
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
            var staff = CreateUser(UserType.Employee, "Assigned Staff", "9000000001");
            var manager = CreateUser(UserType.Employee, "Manager", "9000000002");
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            db.AddRange(customer, otherCustomer, staff, manager, branch);
            db.NumberSeries.Add(new NumberSeries(
                RefundReplacementService.SeriesCode,
                "Customer refund/replacement request numbers",
                "RR/{NUMBER:000000}",
                1,
                1,
                NumberSeriesResetPolicy.Never));
            await db.SaveChangesAsync();

            var now = new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified);
            var order = await AddOrderAsync(db, customer, branch, "submit-order-1", "ORD-SUB-001", now);
            var delivery = await AddDeliveryAsync(db, order, customer, staff, branch, "REF-SUB-001", "DLV-SUB-001", now);
            var otherOrder = await AddOrderAsync(db, otherCustomer, branch, "submit-order-2", "ORD-SUB-002", now);
            var otherDelivery = await AddDeliveryAsync(db, otherOrder, otherCustomer, staff, branch, "REF-SUB-002", "DLV-SUB-002", now);

            var clock = new TestClock(now.AddMinutes(1));
            var storage = new CapturingMediaStorage();
            var service = new RefundReplacementService(
                db,
                clock,
                new RefundReplacementConfigurationService(db, clock),
                new NumberSeriesService(db, clock),
                storage,
                new TestNotificationEventWriter(db, clock));
            return new SubmitHarness(
                connection, db, clock, storage, service,
                customer, otherCustomer, staff, manager, branch,
                order, delivery, otherOrder, otherDelivery);
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

        internal sealed class CapturingMediaStorage : IMediaStorage
        {
            public Dictionary<string, byte[]> Files { get; } = [];
            public List<string> DeletedKeys { get; } = [];
            public long ReportedSizeOffset { get; set; }

            public async Task<StoredMediaResult> SaveAsync(
                string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
            {
                using var buffered = new MemoryStream();
                await content.CopyToAsync(buffered, cancellationToken);
                var bytes = buffered.ToArray();
                Files[storageKey] = bytes;
                return new StoredMediaResult(storageKey, contentType, bytes.Length + ReportedSizeOffset);
            }

            public Task<StoredMediaContent> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
                Files.TryGetValue(storageKey, out var content)
                    ? Task.FromResult(new StoredMediaContent(
                        new MemoryStream(content, writable: false), "image/jpeg", content.Length))
                    : throw new NotFoundException("The media was not found.");

            public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken)
            {
                Files.Remove(storageKey);
                DeletedKeys.Add(storageKey);
                return Task.CompletedTask;
            }
        }
    }
}
