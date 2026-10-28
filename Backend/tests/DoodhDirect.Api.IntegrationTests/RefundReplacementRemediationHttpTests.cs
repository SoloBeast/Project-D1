using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Legacy remediation over HTTP: staff POSTs
/// <c>/api/v1/refund-replacements/{id}/remediate</c> and receives the
/// superseded legacy ticket paired with the new Pending Refund ticket, with
/// exact wire names. Retries return the same pair; callers without the branch
/// manage permission are refused.
/// </summary>
public sealed class RefundReplacementRemediationHttpTests
{
    private static int _scenarioCounter;

    private FoundationApiFactory _factory = null!;

    private sealed class QuietRemediationFactory : FoundationApiFactory
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
    public async Task Remediate_ReturnsSupersededPairOverHttp()
    {
        await using var factory = new QuietRemediationFactory();
        _factory = factory;
        await using var setup = await OrderScenarioAsync(factory);
        using var client = AuthenticatedClient(setup.StaffToken);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/refund-replacements/{setup.LegacyPublicId:D}/remediate",
            new { reason = "Replacement has no fulfillment workflow for one-time orders." },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        var data = body.RootElement.GetProperty("data");
        var legacy = data.GetProperty("legacyTicket");
        var refund = data.GetProperty("refundTicket");
        Assert.Equal("Superseded", legacy.GetProperty("status").GetString());
        Assert.Equal("Replacement", legacy.GetProperty("type").GetString());
        Assert.Equal("Pending", refund.GetProperty("status").GetString());
        Assert.Equal("Refund", refund.GetProperty("type").GetString());
        Assert.Equal(
            legacy.GetProperty("requestNumber").GetString(),
            refund.GetProperty("supersedesRequestNumber").GetString());
        Assert.Equal(
            refund.GetProperty("requestNumber").GetString(),
            legacy.GetProperty("supersededByRequestNumber").GetString());
        Assert.Equal(
            legacy.GetProperty("requestId").GetString(),
            refund.GetProperty("supersedesRequestPublicId").GetString());
    }

    [Fact]
    public async Task Remediate_Retry_ReturnsSamePairOverHttp()
    {
        await using var factory = new QuietRemediationFactory();
        _factory = factory;
        await using var setup = await OrderScenarioAsync(factory);
        using var client = AuthenticatedClient(setup.StaffToken);
        var path = $"/api/v1/refund-replacements/{setup.LegacyPublicId:D}/remediate";

        using var first = await client.PostAsJsonAsync(
            path, new { reason = "First attempt." }, CancellationToken.None);
        using var second = await client.PostAsJsonAsync(
            path, new { reason = "Retry attempt." }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstRefund = JsonDocument.Parse(await first.Content.ReadAsStringAsync(CancellationToken.None))
            .RootElement.GetProperty("data").GetProperty("refundTicket").GetProperty("requestId").GetString();
        var secondRefund = JsonDocument.Parse(await second.Content.ReadAsStringAsync(CancellationToken.None))
            .RootElement.GetProperty("data").GetProperty("refundTicket").GetProperty("requestId").GetString();
        Assert.Equal(firstRefund, secondRefund);
    }

    [Fact]
    public async Task Remediate_WithoutManagePermission_IsForbidden()
    {
        await using var factory = new QuietRemediationFactory();
        _factory = factory;
        await using var setup = await OrderScenarioAsync(factory);
        using var client = AuthenticatedClient(setup.CustomerToken);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/refund-replacements/{setup.LegacyPublicId:D}/remediate",
            new { reason = "Customer must not remediate." },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Remediate_UnknownTicket_IsNotFound()
    {
        await using var factory = new QuietRemediationFactory();
        _factory = factory;
        await using var setup = await OrderScenarioAsync(factory);
        using var client = AuthenticatedClient(setup.StaffToken);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/refund-replacements/{Guid.NewGuid():D}/remediate",
            new { reason = "No such ticket." },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient AuthenticatedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Seeds one isolated order scenario into the test host database: branch,
    /// users, a failed one-time-order delivery and its approved legacy
    /// Replacement ticket. All dates derive from the host clock.
    /// </summary>
    private async Task<OrderRemediationSetup> OrderScenarioAsync(FoundationApiFactory factory)
    {
        var n = Interlocked.Increment(ref _scenarioCounter);
        var scope = factory.Services.CreateAsyncScope();
        try
        {
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<DoodhDirectDbContext>();
            var time = services.GetRequiredService<IIndiaTimeProvider>();
            var now = time.Now;

            if (!await db.NumberSeries.AnyAsync(x => x.Code == "REFUNDREPLACEMENT"))
            {
                db.NumberSeries.Add(new NumberSeries(
                    "REFUNDREPLACEMENT", "Refund/replacement request numbers", "RR/{NUMBER:000000}",
                    1, 1, NumberSeriesResetPolicy.Never));
                await db.SaveChangesAsync();
            }

            var branch = new Branch($"E2ER{n}", $"E2E Remediation Branch {n}", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var manager = NewUser(UserType.Employee, $"E2E Remediation Manager {n}", $"930000{n:D4}");
            var customer = NewUser(UserType.Customer, $"E2E Remediation Customer {n}", $"940000{n:D4}");
            db.AddRange(branch, manager, customer);
            await db.SaveChangesAsync();

            var order = new Order(
                customer.Id, null, branch.Id, $"e2e-rem-order-{n}-{Guid.NewGuid():N}", $"ORD-E2E-REM-{n}",
                200m, 0m, branch.Code, branch.Name, "Home", "1 Main Road", null,
                "Central", "Bengaluru", "Karnataka", "560001", null, null,
                "Customer", customer.Mobile ?? $"940000{n:D4}", 12.9716m, 77.5946m);
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            var delivery = Delivery.ForOrder(
                order.Id, customer.Id, branch.Id, DateOnly.FromDateTime(now),
                $"REF-E2E-REM-{n}", "Customer", customer.Mobile ?? $"940000{n:D4}",
                "Home, 1 Main Road, Central, Bengaluru", null, 12.9716m, 77.5946m);
            delivery.AssignDeliveryNumber($"DLV-E2E-REM-{n}");
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();

            delivery.Assign(manager.Id, manager.Id, now, null);
            delivery.PickUp(manager.Id, now, null);
            delivery.Start(manager.Id, now);
            delivery.Arrive(manager.Id, now);
            delivery.Fail(manager.Id, now, DeliveryFailureReasons.CustomerNotAvailable, null, 12.9716m, 77.5946m);
            await db.SaveChangesAsync();

            var ticket = new RefundReplacementRequest(
                order.Id, delivery.Id, customer.Id, branch.Id, null,
                RefundReplacementType.Replacement, RefundReplacementSource.CustomerPostDelivery,
                "Customer missed the delivery call.", null, now, now.AddHours(48));
            ticket.AssignRequestNumber($"RR-E2E-REM-{n}");
            ticket.Approve(manager.Id, now, "Legacy approval.");
            db.RefundReplacementRequests.Add(ticket);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var jwt = services.GetRequiredService<ITokenService>();
            string Mint(User user, string[] roles, string[] permissions) => jwt.Create(
                user,
                new UserSession(
                    user.Id, $"e2e-rem-device-{n}", "E2E Device", "Test", "127.0.0.1", "E2E Agent", now),
                roles,
                permissions,
                [branch.Id],
                now,
                user.AuthorizationVersion).AccessToken;
            var staffToken = Mint(manager, ["SUPPORT"], [AuthorizationCodes.RefundReplacementManageBranch]);
            var customerToken = Mint(customer, ["CUSTOMER"], [AuthorizationCodes.RefundReplacementReadOwn]);

            return new OrderRemediationSetup(
                factory, scope, ticket.PublicId, branch.Id, staffToken, customerToken);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private static User NewUser(UserType type, string name, string mobile)
    {
        var user = new User(type);
        user.SetProfile(name);
        user.SetContact(mobile, null);
        return user;
    }

    private sealed class OrderRemediationSetup(
        FoundationApiFactory factory,
        AsyncServiceScope scope,
        Guid legacyPublicId,
        long branchId,
        string staffToken,
        string customerToken) : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope = scope;
        private bool _disposed;

        public Guid LegacyPublicId { get; } = legacyPublicId;

        public long BranchId { get; } = branchId;

        public string StaffToken { get; } = staffToken;

        public string CustomerToken { get; } = customerToken;

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
