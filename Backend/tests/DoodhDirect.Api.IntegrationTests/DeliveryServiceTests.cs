using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Deliveries;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class DeliveryServiceTests
{
    [Fact]
    public async Task MaterializeEligible_CreatesOrderAndSubscriptionDeliveriesAndIsIdempotent()
    {
        await using var harness = await DeliveryHarness.CreateAsync();

        var first = await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor,
            harness.Today,
            CancellationToken.None);
        var second = await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor,
            harness.Today,
            CancellationToken.None);

        Assert.Equal(new DeliveryMaterializationResult(1, 1), first);
        Assert.Equal(new DeliveryMaterializationResult(0, 0), second);
        var deliveries = await harness.Db.Deliveries
            .AsNoTracking()
            .OrderBy(x => x.SourceType)
            .ToListAsync();
        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, x =>
            x.SourceType == DeliverySourceType.OneTimeOrder &&
            x.OrderId == harness.Order.Id &&
            x.ScheduledDate == harness.Today);
        Assert.Contains(deliveries, x =>
            x.SourceType == DeliverySourceType.SubscriptionOccurrence &&
            x.SubscriptionDeliveryId == harness.SubscriptionDelivery.Id &&
            x.ScheduledDate == harness.Today);
        Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "DELIVERY.MATERIALIZE")
            .ToListAsync());
    }

    [Fact]
    public async Task MaterializeEligible_RestrictsBranchActorAndAllowsGlobalActor()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var otherBranchActor = new DeliveryActor(harness.Manager.Id, [harness.OtherBranch.Id]);

        var restricted = await harness.Service.MaterializeEligibleAsync(
            otherBranchActor,
            harness.Today,
            CancellationToken.None);
        var global = await harness.Service.MaterializeEligibleAsync(
            new DeliveryActor(harness.Manager.Id, [], HasGlobalAccess: true),
            harness.Today,
            CancellationToken.None);

        Assert.Equal(new DeliveryMaterializationResult(0, 0), restricted);
        Assert.Equal(new DeliveryMaterializationResult(1, 1), global);
    }

    [Fact]
    public async Task MaterializeEligible_RejectsActorWithoutBranchOrGlobalScope()
    {
        await using var harness = await DeliveryHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.MaterializeEligibleAsync(
                new DeliveryActor(harness.Manager.Id, []),
                harness.Today,
                CancellationToken.None));

        Assert.Contains("branch assignment", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task BranchReads_HideResourcesOutsideActorScope()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        await harness.MaterializeAsync();
        var otherBranchActor = new DeliveryActor(harness.Manager.Id, [harness.OtherBranch.Id]);

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.GetForBranchAsync(
            otherBranchActor,
            harness.Branch.Id,
            harness.Today,
            null,
            CancellationToken.None));

        var visible = await harness.Service.GetForBranchAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            harness.Today,
            DeliveryStatus.ReadyForAssignment,
            CancellationToken.None);
        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public async Task Assign_RequiresActiveDeliveryStaffInDeliveryBranch()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();

        var wrongBranch = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.AssignAsync(
            harness.ManagerActor,
            deliveryId,
            new AssignDeliveryRequest(harness.OtherBranchStaff.PublicId, null),
            CancellationToken.None));
        var inactive = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.AssignAsync(
            harness.ManagerActor,
            deliveryId,
            new AssignDeliveryRequest(harness.InactiveStaff.PublicId, null),
            CancellationToken.None));

        Assert.Equal("employeeId", wrongBranch.Field);
        Assert.Equal("employeeId", inactive.Field);
        var delivery = await harness.Db.Deliveries.AsNoTracking().SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.ReadyForAssignment, delivery.Status);
        Assert.Null(delivery.AssignedEmployeeId);
    }

    [Fact]
    public async Task AssignAndReassign_BeforePickupPersistHistoryAndSynchronizeOrder()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();

        var assigned = await harness.Service.AssignAsync(
            harness.ManagerActor,
            deliveryId,
            new AssignDeliveryRequest(harness.Staff.PublicId, "Initial route"),
            CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var reassigned = await harness.Service.AssignAsync(
            harness.ManagerActor,
            deliveryId,
            new AssignDeliveryRequest(harness.SecondStaff.PublicId, "Coverage change"),
            CancellationToken.None);

        Assert.Equal(harness.Staff.PublicId, assigned.AssignedEmployeeId);
        Assert.Equal(harness.SecondStaff.PublicId, reassigned.AssignedEmployeeId);
        Assert.Equal(2, reassigned.Assignments.Count);
        Assert.Equal(["Initial route", "Coverage change"], reassigned.Assignments.Select(x => x.Reason));
        harness.Db.ChangeTracker.Clear();
        var delivery = await harness.Db.Deliveries
            .AsNoTracking()
            .Include(x => x.Assignments)
            .SingleAsync(x => x.PublicId == deliveryId);
        var order = await harness.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == harness.Order.Id);
        Assert.Equal(DeliveryStatus.Assigned, delivery.Status);
        Assert.Equal(harness.SecondStaff.Id, delivery.AssignedEmployeeId);
        Assert.Equal(2, delivery.Assignments.Count);
        Assert.Equal(OrderStatus.Assigned, order.Status);
        Assert.Equal(2, harness.Realtime.Deliveries.Count);
        Assert.Equal(2, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.ASSIGN" || x.Action == "DELIVERY.REASSIGN"));
    }

    [Fact]
    public async Task StaffTransitions_HideDeliveryFromEmployeeWhoIsNotAssigned()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.PickUpAsync(
            harness.StaffActor(harness.SecondStaff),
            deliveryId,
            new DeliveryNotesRequest(null),
            CancellationToken.None));

        var delivery = await harness.Db.Deliveries.AsNoTracking().SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.Assigned, delivery.Status);
    }

    [Fact]
    public async Task OrderWorkflow_RequiresOtpAndSynchronizesDeliveredStatus()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryNotesRequest(null),
            CancellationToken.None));
        await harness.Service.IssueOtpAsync(harness.StaffActor(harness.Staff), deliveryId, CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => harness.Service.IssueOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            CancellationToken.None));

        var message = Assert.Single(harness.OtpDelivery.Messages);
        Assert.Equal("9999999999", message.Destination);
        var invalid = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest("000000" == message.Code ? "111111" : "000000"),
            CancellationToken.None));
        Assert.Equal("code", invalid.Field);

        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(message.Code),
            CancellationToken.None);
        var completed = await harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryNotesRequest("Handed to customer"),
            CancellationToken.None);

        Assert.NotNull(verified.OtpVerifiedAtUtc);
        Assert.Equal(DeliveryStatus.Delivered, completed.Status);
        Assert.False(completed.IsTrackingActive);
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(OrderStatus.Delivered,
            (await harness.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == harness.Order.Id)).Status);
        var otp = await harness.Db.DeliveryOtps.AsNoTracking().SingleAsync();
        Assert.Equal(1, otp.AttemptCount);
        Assert.NotNull(otp.ConsumedAtUtc);
    }

    [Fact]
    public async Task LocationTracking_IsVisibleToCustomerOnlyWhileActiveAndRejectsStaleTimestamp()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        var beforeStart = await harness.Service.GetForCustomerAsync(harness.Customer.Id, deliveryId, CancellationToken.None);
        Assert.False(beforeStart.IsTrackingActive);
        Assert.Null(beforeStart.LatestLocation);

        await harness.Service.PickUpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryNotesRequest(null),
            CancellationToken.None);
        await harness.Service.StartAsync(harness.StaffActor(harness.Staff), deliveryId, CancellationToken.None);
        var location = await harness.Service.RecordLocationAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryLocationRequest(12.972m, 77.595m, 8m, harness.Clock.UtcNow),
            CancellationToken.None);
        var active = await harness.Service.GetForCustomerAsync(harness.Customer.Id, deliveryId, CancellationToken.None);

        Assert.True(active.IsTrackingActive);
        Assert.Equal(location, active.LatestLocation);
        Assert.Single(harness.Realtime.Locations);
        var stale = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.RecordLocationAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryLocationRequest(12.972m, 77.595m, null, harness.Clock.UtcNow.AddMinutes(-16)),
            CancellationToken.None));
        Assert.Equal("recordedAtUtc", stale.Field);
    }

    [Fact]
    public async Task SubscriptionFailure_SynchronizesOccurrenceWithoutConsumingEntitlement()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        var failed = await harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(
                DeliveryFailureReasons.CustomerNotAvailable,
                "No response",
                12.972m,
                77.595m),
            CancellationToken.None);

        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal(DeliveryFailureReasons.CustomerNotAvailable, failed.FailureReason);
        harness.Db.ChangeTracker.Clear();
        var occurrence = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.Id == harness.SubscriptionDelivery.Id);
        var subscription = await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        Assert.Equal(SubscriptionDeliveryStatus.Failed, occurrence.Status);
        Assert.Equal(0, subscription.UsedEntitlement);
    }

    private sealed class DeliveryHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private DeliveryHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            CapturingOtpDeliveryService otpDelivery,
            CapturingRealtimePublisher realtime,
            DeliveryService service,
            User customer,
            User manager,
            User staff,
            User secondStaff,
            User otherBranchStaff,
            User inactiveStaff,
            Branch branch,
            Branch otherBranch,
            Order order,
            Subscription subscription,
            SubscriptionDelivery subscriptionDelivery)
        {
            this.connection = connection;
            Db = db;
            Clock = clock;
            OtpDelivery = otpDelivery;
            Realtime = realtime;
            Service = service;
            Customer = customer;
            Manager = manager;
            Staff = staff;
            SecondStaff = secondStaff;
            OtherBranchStaff = otherBranchStaff;
            InactiveStaff = inactiveStaff;
            Branch = branch;
            OtherBranch = otherBranch;
            Order = order;
            Subscription = subscription;
            SubscriptionDelivery = subscriptionDelivery;
        }

        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public CapturingOtpDeliveryService OtpDelivery { get; }
        public CapturingRealtimePublisher Realtime { get; }
        public DeliveryService Service { get; }
        public User Customer { get; }
        public User Manager { get; }
        public User Staff { get; }
        public User SecondStaff { get; }
        public User OtherBranchStaff { get; }
        public User InactiveStaff { get; }
        public Branch Branch { get; }
        public Branch OtherBranch { get; }
        public Order Order { get; }
        public Subscription Subscription { get; }
        public SubscriptionDelivery SubscriptionDelivery { get; }
        public DateOnly Today => DateOnly.FromDateTime(Clock.UtcNow);
        public DeliveryActor ManagerActor => new(Manager.Id, [Branch.Id]);
        public DeliveryActor StaffActor(User employee) => new(employee.Id, [Branch.Id]);

        public async Task MaterializeAsync() =>
            _ = await Service.MaterializeEligibleAsync(ManagerActor, Today, CancellationToken.None);

        public async Task<Guid> MaterializeOrderAsync()
        {
            await MaterializeAsync();
            return await Db.Deliveries
                .AsNoTracking()
                .Where(x => x.OrderId == Order.Id)
                .Select(x => x.PublicId)
                .SingleAsync();
        }

        public async Task<Guid> MaterializeSubscriptionAsync()
        {
            await MaterializeAsync();
            return await Db.Deliveries
                .AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId == SubscriptionDelivery.Id)
                .Select(x => x.PublicId)
                .SingleAsync();
        }

        public Task<DeliveryResult> AssignAsync(Guid deliveryId, User employee) =>
            Service.AssignAsync(
                ManagerActor,
                deliveryId,
                new AssignDeliveryRequest(employee.PublicId, null),
                CancellationToken.None);

        public async Task AdvanceToArrivedAsync(Guid deliveryId, User employee)
        {
            var actor = StaffActor(employee);
            await AssignAsync(deliveryId, employee);
            await Service.PickUpAsync(actor, deliveryId, new DeliveryNotesRequest(null), CancellationToken.None);
            await Service.StartAsync(actor, deliveryId, CancellationToken.None);
            await Service.ArriveAsync(actor, deliveryId, CancellationToken.None);
        }

        public static async Task<DeliveryHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var customer = User(UserType.Customer, "Customer", "9999999999");
            var manager = User(UserType.Employee, "Delivery Manager", "9000000000");
            var staff = User(UserType.Employee, "Delivery Staff One", "9000000001");
            var secondStaff = User(UserType.Employee, "Delivery Staff Two", "9000000002");
            var otherBranchStaff = User(UserType.Employee, "Other Branch Staff", "9000000003");
            var inactiveStaff = User(UserType.Employee, "Inactive Staff", "9000000004");
            inactiveStaff.Deactivate();
            var role = new Role(AuthorizationCodes.DeliveryStaff, "Delivery Staff");
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var otherBranch = new Branch("NORTH", "North Branch", "Bengaluru", "Karnataka", 13.0358m, 77.5970m);
            db.AddRange(customer, manager, staff, secondStaff, otherBranchStaff, inactiveStaff, role, branch, otherBranch);
            await db.SaveChangesAsync();

            staff.AssignRole(role, branch.Id);
            secondStaff.AssignRole(role, branch.Id);
            otherBranchStaff.AssignRole(role, otherBranch.Id);
            inactiveStaff.AssignRole(role, branch.Id);
            var category = new ProductCategory("MILK", "Milk");
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            var address = new CustomerAddress(
                customer.Id,
                "Home",
                "1 Main Road",
                "Central",
                "Bengaluru",
                "Karnataka",
                "560001",
                "Customer",
                "9999999999",
                12.9716m,
                77.5946m);
            db.AddRange(product, address);
            await db.SaveChangesAsync();

            var order = new Order(
                customer.Id,
                address.Id,
                branch.Id,
                "order-delivery-1",
                "ORD-DEL-001",
                80m,
                0m,
                branch.Code,
                branch.Name,
                address.Label,
                address.AddressLine1,
                address.AddressLine2,
                address.Locality,
                address.City,
                address.State,
                address.PinCode,
                address.Landmark,
                address.DeliveryInstructions,
                address.ContactName,
                address.ContactMobile,
                address.Latitude,
                address.Longitude);
            order.ConfirmPayment();
            var today = new DateOnly(2026, 8, 16);
            var subscription = new Subscription(
                customer.Id,
                product.Id,
                address.Id,
                branch.Id,
                "subscription-delivery-1",
                today,
                today.AddDays(7),
                1m,
                80m,
                2,
                product.Sku,
                product.Name,
                product.UnitOfMeasure,
                branch.Code,
                branch.Name,
                "1 Main Road, Central, Bengaluru, Karnataka 560001");
            subscription.AddDelivery(today);
            subscription.Activate(new DateTime(2026, 8, 16, 3, 0, 0, DateTimeKind.Utc));
            db.AddRange(order, subscription);
            await db.SaveChangesAsync();
            var subscriptionDelivery = subscription.Deliveries.Single();
            db.ChangeTracker.Clear();

            var clock = new TestClock(new DateTime(2026, 8, 16, 4, 0, 0, DateTimeKind.Utc));
            var otpDelivery = new CapturingOtpDeliveryService();
            var realtime = new CapturingRealtimePublisher();
            var service = new DeliveryService(
                db,
                clock,
                new TestPasswordHasher(),
                otpDelivery,
                realtime,
                Options.Create(new DeliveryOptions
                {
                    OtpCodeLength = 6,
                    OtpExpiryMinutes = 10,
                    OtpMaximumAttempts = 3,
                    MaximumLocationAgeMinutes = 15,
                    MaximumLocationFutureSkewMinutes = 5,
                    MaximumLocationsPerDelivery = 10,
                    LocationRetentionDays = 30
                }));
            return new DeliveryHarness(
                connection,
                db,
                clock,
                otpDelivery,
                realtime,
                service,
                customer,
                manager,
                staff,
                secondStaff,
                otherBranchStaff,
                inactiveStaff,
                branch,
                otherBranch,
                order,
                subscription,
                subscriptionDelivery);
        }

        private static User User(UserType type, string name, string mobile)
        {
            var user = new User(type);
            user.SetProfile(name);
            user.SetContact(mobile, null);
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class CapturingOtpDeliveryService : IOtpDeliveryService
    {
        public List<(string Destination, string Code)> Messages { get; } = [];

        public Task SendAsync(string destination, string code, CancellationToken cancellationToken)
        {
            Messages.Add((destination, code));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingRealtimePublisher : IDeliveryRealtimePublisher
    {
        public List<DeliveryResult> Deliveries { get; } = [];
        public List<(Guid DeliveryId, DeliveryLocationResult Location)> Locations { get; } = [];

        public Task DeliveryChangedAsync(DeliveryResult delivery, CancellationToken cancellationToken)
        {
            Deliveries.Add(delivery);
            return Task.CompletedTask;
        }

        public Task LocationChangedAsync(
            Guid deliveryId,
            DeliveryLocationResult location,
            CancellationToken cancellationToken)
        {
            Locations.Add((deliveryId, location));
            return Task.CompletedTask;
        }
    }
}
