using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 6.1.2 — successful HTTP response verification (test-only; no
/// production changes).
///
/// Exercises the real HTTP pipeline end to end: data is seeded into the test
/// host's database, JWTs are minted by the host's own <see cref="JwtTokenService"/>
/// (so they satisfy the production validation parameters), and staff/customer
/// detail/list GETs are called over HTTP with bearer tokens. Assertions read
/// the actual response bodies with case-sensitive property access, proving
/// the exact wire names — not just OpenAPI schemas or direct DTO
/// serialization. All dates are derived dynamically from the ticket flow
/// itself (proposal → approval → reads), so no fixed calendar dates are
/// asserted.
/// </summary>
public sealed class RefundReplacementHttpSuccessTests
{
    private static int _scenarioCounter;

    private FoundationApiFactory _factory = null!;

    /// <summary>
    /// Dedicated test host without background workers. The timeout,
    /// notification-dispatch and reconciliation workers run on their own
    /// scopes against the single shared in-memory connection, so a worker
    /// cycle landing inside a scenario transaction surfaces as a spurious
    /// "SqliteConnection does not support nested transactions" failure.
    /// No test here depends on worker dispatch, and the factory is
    /// per-test, so this is test-only and side-effect free.
    /// </summary>
    private sealed class QuietRefundReplacementFactory : FoundationApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var workers = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType is not null
                        && typeof(BackgroundService).IsAssignableFrom(descriptor.ImplementationType))
                    .ToList();
                foreach (var worker in workers)
                {
                    services.Remove(worker);
                }
            });
        }
    }

    [Fact]
    public async Task StaffDetail_ApprovedRedelivery_ReturnsPersistedDateOverHttp()
    {
        await using var factory = new QuietRefundReplacementFactory();
        _factory = factory;
        await using var setup = await ScenarioSetupAsync(factory, extraOccurrences: 0);
        var requestId = await setup.FailTicketAsync(setup.OccurrenceIds[0], setup.Today);
        await setup.ChooseAsync(requestId, CustomerChoice.ReDelivery);
        var replacementDate = await setup.ApproveRedeliveryAsync(requestId);
        using var client = AuthenticatedClient(setup.StaffToken);

        var (status, body) = await GetJsonAsync(
            client, $"/api/v1/refund-replacements/{requestId:D}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(requestId, Guid.Parse(data.GetProperty("requestId").GetString()!));
        Assert.Equal("Approved", data.GetProperty("status").GetString());
        // The persisted replacement date in yyyy-MM-dd wire format.
        Assert.Equal(
            replacementDate.ToString("yyyy-MM-dd"),
            data.GetProperty("replacementDate").GetString());
        var occurrenceId = await setup.ReplacementOccurrenceIdAsync(requestId);
        Assert.Equal(
            occurrenceId.ToString(),
            data.GetProperty("replacementDeliveryPublicId").GetString());
        // Not yet materialized: explicit null, never a fabricated reference.
        Assert.Equal(JsonValueKind.Null, data.GetProperty("replacementDeliveryNumber").ValueKind);
    }

    [Fact]
    public async Task StaffDetail_MaterializedReplacement_ReturnsGenuineDeliveryNumberOverHttp()
    {
        await using var factory = new QuietRefundReplacementFactory();
        _factory = factory;
        await using var setup = await ScenarioSetupAsync(factory, extraOccurrences: 0);
        var requestId = await setup.FailTicketAsync(setup.OccurrenceIds[0], setup.Today);
        await setup.ChooseAsync(requestId, CustomerChoice.ReDelivery);
        var replacementDate = await setup.ApproveRedeliveryAsync(requestId);
        var deliveryNumber = await setup.MaterializeReplacementAsync(requestId, replacementDate);
        using var client = AuthenticatedClient(setup.StaffToken);

        var (status, body) = await GetJsonAsync(
            client, $"/api/v1/refund-replacements/{requestId:D}");

        Assert.Equal(HttpStatusCode.OK, status);
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(
            replacementDate.ToString("yyyy-MM-dd"),
            data.GetProperty("replacementDate").GetString());
        var number = data.GetProperty("replacementDeliveryNumber").GetString();
        // The genuine public business reference of the materialized
        // operational delivery — a DELIVERY-series number, not a GUID.
        Assert.Equal(deliveryNumber, number);
        Assert.False(string.IsNullOrWhiteSpace(number));
        Assert.False(Guid.TryParse(number, out _));
        var occurrenceId = await setup.ReplacementOccurrenceIdAsync(requestId);
        Assert.Equal(
            occurrenceId.ToString(),
            data.GetProperty("replacementDeliveryPublicId").GetString());
    }

    [Fact]
    public async Task StaffList_ApprovedRedelivery_ReturnsPersistedDateOverHttp()
    {
        await using var factory = new QuietRefundReplacementFactory();
        _factory = factory;
        await using var setup = await ScenarioSetupAsync(factory, extraOccurrences: 0);
        var requestId = await setup.FailTicketAsync(setup.OccurrenceIds[0], setup.Today);
        await setup.ChooseAsync(requestId, CustomerChoice.ReDelivery);
        var replacementDate = await setup.ApproveRedeliveryAsync(requestId);
        using var client = AuthenticatedClient(setup.StaffToken);

        var (status, body) = await GetJsonAsync(
            client,
            $"/api/v1/refund-replacements?branchId={setup.BranchId}&page=1&pageSize=20");

        Assert.Equal(HttpStatusCode.OK, status);
        var item = SinglePageItem(body, requestId);
        Assert.Equal(
            replacementDate.ToString("yyyy-MM-dd"),
            item.GetProperty("replacementDate").GetString());
    }

    [Fact]
    public async Task CustomerDetailAndList_ReturnPersistedDateOverHttp()
    {
        await using var factory = new QuietRefundReplacementFactory();
        _factory = factory;
        await using var setup = await ScenarioSetupAsync(factory, extraOccurrences: 0);
        var requestId = await setup.FailTicketAsync(setup.OccurrenceIds[0], setup.Today);
        await setup.ChooseAsync(requestId, CustomerChoice.ReDelivery);
        var replacementDate = await setup.ApproveRedeliveryAsync(requestId);
        using var client = AuthenticatedClient(setup.CustomerToken);

        var (detailStatus, detailBody) = await GetJsonAsync(
            client, $"/api/v1/customer/refund-replacements/{requestId:D}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        var detail = detailBody.RootElement.GetProperty("data");
        Assert.Equal(
            replacementDate.ToString("yyyy-MM-dd"),
            detail.GetProperty("replacementDate").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("replacementDeliveryNumber").ValueKind);

        var (listStatus, listBody) = await GetJsonAsync(
            client, "/api/v1/customer/refund-replacements");
        Assert.Equal(HttpStatusCode.OK, listStatus);
        var item = SingleListItem(listBody, requestId);
        Assert.Equal(
            replacementDate.ToString("yyyy-MM-dd"),
            item.GetProperty("replacementDate").GetString());
    }

    /// <summary>
    /// D1-D slice 7.1: a subscription cancellation that voids the pending
    /// replacement and refunds it must serialize as the distinct terminal
    /// <c>Cancelled</c> status on both staff and customer reads — never as
    /// a successful fulfillment (<c>Completed</c>) nor as a replacement
    /// failure (<c>Failed</c>). The status persists (fresh HTTP GETs, not
    /// the cancel response) and the wire name is asserted literally.
    /// </summary>
    [Fact]
    public async Task CancelledReplacement_SerializesCancelledStatusOverHttp()
    {
        await using var factory = new QuietRefundReplacementFactory();
        _factory = factory;
        await using var setup = await ScenarioSetupAsync(factory, extraOccurrences: 0);
        var requestId = await setup.FailTicketAsync(setup.OccurrenceIds[0], setup.Today);
        await setup.ChooseAsync(requestId, CustomerChoice.ReDelivery);
        await setup.ApproveRedeliveryAsync(requestId);
        await setup.CancelSubscriptionWithRefundAsync();

        using var staffClient = AuthenticatedClient(setup.StaffToken);
        var (staffStatus, staffBody) = await GetJsonAsync(
            staffClient, $"/api/v1/refund-replacements/{requestId:D}");
        Assert.Equal(HttpStatusCode.OK, staffStatus);
        var staffData = staffBody.RootElement.GetProperty("data");
        Assert.Equal("Cancelled", staffData.GetProperty("status").GetString());
        Assert.NotNull(staffData.GetProperty("completedAt").GetString());

        using var customerClient = AuthenticatedClient(setup.CustomerToken);
        var (customerStatus, customerBody) = await GetJsonAsync(
            customerClient, $"/api/v1/customer/refund-replacements/{requestId:D}");
        Assert.Equal(HttpStatusCode.OK, customerStatus);
        var customerData = customerBody.RootElement.GetProperty("data");
        Assert.Equal("Cancelled", customerData.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RefundAndAwaitingChoice_ExposeNullReplacementFieldsOverHttp()
    {
        await using var factory = new QuietRefundReplacementFactory();
        _factory = factory;
        await using var setup = await ScenarioSetupAsync(factory, extraOccurrences: 1);
        var awaitingId = await setup.FailTicketAsync(setup.OccurrenceIds[0], setup.Today);
        var refundId = await setup.FailTicketAsync(setup.OccurrenceIds[1], setup.Today.AddDays(1));
        await setup.ChooseAsync(refundId, CustomerChoice.Refund);
        await setup.ApproveAsync(refundId);
        // Audit remediation A: completion executes the shared wallet refund,
        // so the scenario subscription needs a covering payment first.
        await setup.SeedSubscriptionPaymentAsync();
        await setup.CompleteAsync(refundId, "Refund verified");

        using var staffClient = AuthenticatedClient(setup.StaffToken);
        var (awaitingStatus, awaitingBody) = await GetJsonAsync(
            staffClient, $"/api/v1/refund-replacements/{awaitingId:D}");
        Assert.Equal(HttpStatusCode.OK, awaitingStatus);
        var awaiting = awaitingBody.RootElement.GetProperty("data");
        Assert.Equal("AwaitingCustomerChoice", awaiting.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, awaiting.GetProperty("replacementDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, awaiting.GetProperty("replacementDeliveryPublicId").ValueKind);
        Assert.Equal(JsonValueKind.Null, awaiting.GetProperty("replacementDeliveryNumber").ValueKind);

        var (refundStatus, refundBody) = await GetJsonAsync(
            staffClient, $"/api/v1/refund-replacements/{refundId:D}");
        Assert.Equal(HttpStatusCode.OK, refundStatus);
        var refund = refundBody.RootElement.GetProperty("data");
        Assert.Equal("Completed", refund.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, refund.GetProperty("replacementDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, refund.GetProperty("replacementDeliveryPublicId").ValueKind);
        Assert.Equal(JsonValueKind.Null, refund.GetProperty("replacementDeliveryNumber").ValueKind);

        using var customerClient = AuthenticatedClient(setup.CustomerToken);
        var (customerStatus, customerBody) = await GetJsonAsync(
            customerClient, $"/api/v1/customer/refund-replacements/{refundId:D}");
        Assert.Equal(HttpStatusCode.OK, customerStatus);
        var customerRefund = customerBody.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Null, customerRefund.GetProperty("replacementDate").ValueKind);
    }

    private HttpClient AuthenticatedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonDocument Body)> GetJsonAsync(
        HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, CancellationToken.None);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return (response.StatusCode, body);
    }

    private static JsonElement SinglePageItem(JsonDocument body, Guid requestId)
    {
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        return SingleMatch(
            body.RootElement.GetProperty("data").GetProperty("items").EnumerateArray(), requestId);
    }

    private static JsonElement SingleListItem(JsonDocument body, Guid requestId)
    {
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        return SingleMatch(body.RootElement.GetProperty("data").EnumerateArray(), requestId);
    }

    private static JsonElement SingleMatch(JsonElement.ArrayEnumerator items, Guid requestId)
    {
        foreach (var item in items)
        {
            if (Guid.Parse(item.GetProperty("requestId").GetString()!) == requestId)
            {
                return item;
            }
        }

        throw new Xunit.Sdk.XunitException($"List response contains no item for request {requestId:D}.");
    }

    /// <summary>
    /// Seeds one isolated scenario into the test host database: branch,
    /// users, catalogue rows, and a subscription with one occurrence for
    /// today plus <paramref name="extraOccurrences"/> for the following days.
    /// All dates derive from the host's real clock, so no fixed calendar
    /// dates are assumed.
    /// </summary>
    private async Task<ScenarioSetup> ScenarioSetupAsync(FoundationApiFactory factory, int extraOccurrences)
    {
        var n = Interlocked.Increment(ref _scenarioCounter);
        var scope = factory.Services.CreateAsyncScope();
        try
        {
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<DoodhDirectDbContext>();
            var time = services.GetRequiredService<IIndiaTimeProvider>();
            var today = time.Today;

            var staffRole = await EnsureSharedSeedAsync(db);
            var branchCode = $"E2E{n}";
            var branch = new Branch(branchCode, $"E2E Branch {n}", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            db.Add(branch);
            db.NumberSeries.Add(new NumberSeries(
                $"DELIVERY_{branchCode}", $"Delivery {branchCode}", "DLV/{SCOPE}/{NUMBER:000000}", 1, 1,
                NumberSeriesResetPolicy.Never, branchCode));
            var manager = NewUser(UserType.Employee, $"E2E Manager {n}", $"910000{n:D4}");
            var staff = NewUser(UserType.Employee, $"E2E Staff {n}", $"910001{n:D4}");
            var customer = NewUser(UserType.Customer, $"E2E Customer {n}", $"920000{n:D4}");
            db.AddRange(manager, staff, customer);
            await db.SaveChangesAsync();
            staff.AssignRole(staffRole, branch.Id);
            await db.SaveChangesAsync();

            var category = new ProductCategory($"E2EMILK{n}", "Milk");
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var sku = $"E2EMILK-{n}";
            var product = new Product(category.Id, sku, "Fresh Milk", null, "litre", 80m);
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka", "560001",
                "Customer", customer.Mobile ?? $"920000{n:D4}", 12.9716m, 77.5946m);
            db.AddRange(product, address);
            await db.SaveChangesAsync();

            var subscription = new Subscription(
                customer.Id, product.Id, address.Id, branch.Id, $"e2e-sub-{n}",
                today, today.AddDays(7), 1m, 80m, 2, sku, "Fresh Milk", "litre",
                branch.Code, branch.Name, "1 Main Road, Central, Bengaluru, Karnataka 560001");
            subscription.AddDelivery(today);
            for (var i = 1; i <= extraOccurrences; i++)
            {
                subscription.AddDelivery(today.AddDays(i));
            }

            subscription.Activate(time.Now);
            db.Add(subscription);
            await db.SaveChangesAsync();
            var occurrenceIds = subscription.Deliveries
                .OrderBy(x => x.ScheduledDate).Select(x => x.Id).ToArray();
            db.ChangeTracker.Clear();

            var jwt = services.GetRequiredService<ITokenService>();
            var now = time.Now;
            string Mint(User user, string[] roles, string[] permissions) => jwt.Create(
                user,
                new UserSession(
                    user.Id, $"e2e-device-{n}", "E2E Device", "Test", "127.0.0.1", "E2E Agent", now),
                roles,
                permissions,
                [branch.Id],
                now,
                user.AuthorizationVersion).AccessToken;
            var staffToken = Mint(manager, ["SUPPORT"], [
                AuthorizationCodes.RefundReplacementManageBranch,
                AuthorizationCodes.RefundReplacementReadBranch]);
            var customerToken = Mint(customer, ["CUSTOMER"], [AuthorizationCodes.RefundReplacementReadOwn]);

            return new ScenarioSetup(
                factory,
                scope,
                customer,
                manager,
                staff,
                branch.Id,
                occurrenceIds,
                today,
                staffToken,
                customerToken);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private static async Task<Role> EnsureSharedSeedAsync(DoodhDirectDbContext db)
    {
        if (!await db.NumberSeries.AnyAsync(x => x.Code == "DELIVERY"))
        {
            db.NumberSeries.Add(new NumberSeries(
                "DELIVERY", "Delivery Number", "DEL/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never));
        }

        if (!await db.NumberSeries.AnyAsync(x => x.Code == RefundReplacementService.SeriesCode))
        {
            db.NumberSeries.Add(new NumberSeries(
                RefundReplacementService.SeriesCode, "Refund/replacement request numbers", "RR/{NUMBER:000000}",
                1, 1, NumberSeriesResetPolicy.Never));
        }

        var role = await db.Roles.SingleOrDefaultAsync(x => x.Code == AuthorizationCodes.DeliveryStaff);
        if (role is null)
        {
            role = new Role(AuthorizationCodes.DeliveryStaff, "Delivery Staff");
            db.Add(role);
        }

        await db.SaveChangesAsync();
        return role;
    }

    private static User NewUser(UserType type, string name, string mobile)
    {
        var user = new User(type);
        user.SetProfile(name);
        user.SetContact(mobile, null);
        return user;
    }

    /// <summary>
    /// Owns one async scope against the test host for scenario setup and
    /// drives the ticket lifecycle through the host's own services. HTTP
    /// calls run through the host pipeline on its own scopes.
    /// </summary>
    private sealed class ScenarioSetup : IAsyncDisposable
    {
        private readonly FoundationApiFactory _factory;
        private readonly AsyncServiceScope _scope;
        private bool _disposed;

        public ScenarioSetup(
            FoundationApiFactory factory,
            AsyncServiceScope scope,
            User customer,
            User manager,
            User staff,
            long branchId,
            long[] occurrenceIds,
            DateOnly today,
            string staffToken,
            string customerToken)
        {
            _factory = factory;
            _scope = scope;
            Customer = customer;
            Manager = manager;
            Staff = staff;
            BranchId = branchId;
            OccurrenceIds = occurrenceIds;
            Today = today;
            StaffToken = staffToken;
            CustomerToken = customerToken;
        }

        public User Customer { get; }
        public User Manager { get; }
        public User Staff { get; }
        public long BranchId { get; }
        public long[] OccurrenceIds { get; }
        public DateOnly Today { get; }
        public string StaffToken { get; }
        public string CustomerToken { get; }

        public AsyncServiceScope Scope() => _factory.Services.CreateAsyncScope();

        public RefundReplacementActor CustomerActor() => new(Customer.Id, new HashSet<long>(), false);

        public RefundReplacementActor StaffActor() =>
            new(Manager.Id, new HashSet<long> { BranchId }, true);

        public async Task<Guid> FailTicketAsync(long occurrenceId, DateOnly throughDate)
        {
            await using var scope = Scope();
            var services = scope.ServiceProvider;
            var deliveries = services.GetRequiredService<IDeliveryService>();
            var managerActor = new DeliveryActor(Manager.Id, [BranchId]);
            await deliveries.MaterializeEligibleAsync(managerActor, throughDate, CancellationToken.None);
            var db = services.GetRequiredService<DoodhDirectDbContext>();
            var deliveryId = await db.Deliveries.AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId == occurrenceId)
                .Select(x => x.PublicId).SingleAsync();
            await deliveries.AssignAsync(
                managerActor, deliveryId, new AssignDeliveryRequest(Staff.PublicId, null),
                CancellationToken.None);
            await deliveries.FailAsync(
                new DeliveryActor(Staff.Id, [BranchId]),
                deliveryId,
                new FailDeliveryRequest(
                    DeliveryFailureReasons.CustomerNotAvailable, "No response", 12.972m, 77.595m),
                CancellationToken.None,
                Proof());
            var ticketDb = services.GetRequiredService<DoodhDirectDbContext>();
            return await ticketDb.RefundReplacementRequests.AsNoTracking()
                .Where(x => x.CustomerId == Customer.Id)
                .OrderByDescending(x => x.Id).Select(x => x.PublicId).FirstAsync();
        }

        public async Task ChooseAsync(Guid requestId, CustomerChoice choice)
        {
            await using var scope = Scope();
            var refunds = scope.ServiceProvider.GetRequiredService<IRefundReplacementService>();
            await refunds.ChooseAsync(
                CustomerActor(), requestId, new ChooseRefundReplacementRequest(choice),
                CancellationToken.None);
        }

        public async Task ApproveAsync(Guid requestId)
        {
            await using var scope = Scope();
            var refunds = scope.ServiceProvider.GetRequiredService<IRefundReplacementService>();
            await refunds.ApproveAsync(
                StaffActor(), requestId, new ApproveRefundReplacementRequest(null),
                CancellationToken.None);
        }

        public async Task<DateOnly> ApproveRedeliveryAsync(Guid requestId)
        {
            await using var scope = Scope();
            var refunds = scope.ServiceProvider.GetRequiredService<IRefundReplacementService>();
            await AddSchedulesAsync(scope);
            var proposal = await refunds.ProposeReplacementDateAsync(
                StaffActor(), requestId, CancellationToken.None);
            var approved = await refunds.ApproveAsync(
                StaffActor(),
                requestId,
                new ApproveRefundReplacementRequest(null, proposal.ProposedDate),
                CancellationToken.None);
            Assert.Equal(proposal.ProposedDate, approved.ReplacementDate);
            return proposal.ProposedDate;
        }

        /// <summary>
        /// D1-D slice 7.1: seeds a successful wallet payment backing the
        /// scenario subscription, then cancels it through the host's own
        /// subscription service — the same path whose frozen refund resolves
        /// the pending re-delivery ticket. Financial mechanics are the
        /// backend's; the helper only arranges the preconditions.
        /// </summary>
        public async Task CancelSubscriptionWithRefundAsync()
        {
            await using var scope = Scope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<DoodhDirectDbContext>();
            var time = services.GetRequiredService<IIndiaTimeProvider>();
            var subscription = await db.Subscriptions.AsNoTracking()
                .Where(x => x.CustomerId == Customer.Id)
                .Select(x => new { x.Id, x.PublicId, x.PayableAmount })
                .SingleAsync();
            var payment = Payment.CreateForSubscription(
                subscription.Id,
                Customer.Id,
                PaymentMethod.Wallet,
                subscription.PayableAmount,
                "INR",
                $"http-cancel-sub-pay-{Guid.NewGuid():N}",
                time.Now.AddMinutes(15));
            payment.Succeed(null, "WALLET", time.Now);
            // Same SQLite workaround as the cancellation service tests:
            // decimals-as-text makes the payment CHECK constraints
            // unreliable there (production SQL Server uses real numerics).
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
            var subscriptions = services.GetRequiredService<ISubscriptionService>();
            var result = await subscriptions.CancelAsync(
                Customer.Id, subscription.PublicId, CancellationToken.None);
            Assert.Equal(SubscriptionStatus.Cancelled, result.Status);
        }

        /// <summary>
        /// Seeds a successful wallet payment covering the scenario
        /// subscription so an explicit-refund completion can execute the
        /// shared delivery-fail refund instead of failing for no coverage.
        /// </summary>
        public async Task SeedSubscriptionPaymentAsync()
        {
            await using var scope = Scope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<DoodhDirectDbContext>();
            var time = services.GetRequiredService<IIndiaTimeProvider>();
            var subscription = await db.Subscriptions.AsNoTracking()
                .Where(x => x.CustomerId == Customer.Id)
                .Select(x => new { x.Id, x.PayableAmount })
                .SingleAsync();
            var payment = Payment.CreateForSubscription(
                subscription.Id,
                Customer.Id,
                PaymentMethod.Wallet,
                subscription.PayableAmount,
                "INR",
                $"http-explicit-refund-pay-{Guid.NewGuid():N}",
                time.Now.AddMinutes(15));
            payment.Succeed(null, "WALLET", time.Now);
            // Same SQLite workaround as the cancellation tests above:
            // decimals-as-text makes the payment CHECK constraints
            // unreliable there (production SQL Server uses real numerics).
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
        }

        public async Task CompleteAsync(Guid requestId, string remarks)
        {
            await using var scope = Scope();
            var refunds = scope.ServiceProvider.GetRequiredService<IRefundReplacementService>();
            await refunds.CompleteAsync(
                StaffActor(), requestId, new DecideRefundReplacementRequest(remarks),
                CancellationToken.None);
        }

        public async Task<Guid> ReplacementOccurrenceIdAsync(Guid requestId)
        {
            await using var scope = Scope();
            var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
            var ticketId = await db.RefundReplacementRequests.AsNoTracking()
                .Where(x => x.PublicId == requestId).Select(x => x.Id).SingleAsync();
            return await db.SubscriptionDeliveries.AsNoTracking()
                .Where(x => x.IsReplacement && x.RefundReplacementRequestId == ticketId)
                .Select(x => x.PublicId).SingleAsync();
        }

        public async Task<string> MaterializeReplacementAsync(Guid requestId, DateOnly replacementDate)
        {
            await using var scope = Scope();
            var services = scope.ServiceProvider;
            var deliveries = services.GetRequiredService<IDeliveryService>();
            await deliveries.MaterializeEligibleAsync(
                new DeliveryActor(Manager.Id, [BranchId]), replacementDate, CancellationToken.None);
            var db = services.GetRequiredService<DoodhDirectDbContext>();
            var ticketId = await db.RefundReplacementRequests.AsNoTracking()
                .Where(x => x.PublicId == requestId).Select(x => x.Id).SingleAsync();
            var number = await db.Deliveries.AsNoTracking()
                .Include(x => x.SubscriptionDelivery)
                .Where(x => x.SubscriptionDelivery != null && x.SubscriptionDelivery.IsReplacement
                    && x.SubscriptionDelivery.RefundReplacementRequestId == ticketId)
                .Select(x => x.DeliveryNumber)
                .SingleAsync();
            Assert.False(string.IsNullOrWhiteSpace(number));
            return number!;
        }

        private async Task AddSchedulesAsync(AsyncServiceScope scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
            var subscription = await db.Subscriptions
                .Include(x => x.Schedules)
                .Where(x => x.CustomerId == Customer.Id)
                .SingleAsync();
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                subscription.AddSchedule(day);
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        private static FailDeliveryProof Proof()
        {
            var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01 };
            return new FailDeliveryProof(
                new MemoryStream(bytes, writable: false), "proof.jpg", "image/jpeg", bytes.Length);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _scope.DisposeAsync();
        }
    }
}
