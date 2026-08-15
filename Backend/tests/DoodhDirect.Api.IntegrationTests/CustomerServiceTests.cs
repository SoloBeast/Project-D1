using DoodhDirect.Application.Common;
using DoodhDirect.Application.Customer;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class CustomerServiceTests
{
    [Fact]
    public async Task GetProfile_CreatesOneProfileAndReturnsSamePublicId()
    {
        await using var harness = await CustomerHarness.CreateAsync();

        var first = await harness.Service.GetProfileAsync(harness.Customer.Id, CancellationToken.None);
        var second = await harness.Service.GetProfileAsync(harness.Customer.Id, CancellationToken.None);

        Assert.Equal(first.PublicId, second.PublicId);
        Assert.Equal(1, await harness.Db.CustomerProfiles.CountAsync());
        Assert.Null(first.FirstName);
    }

    [Fact]
    public async Task UpdateProfile_NormalizesValuesAndPersistsThem()
    {
        await using var harness = await CustomerHarness.CreateAsync();

        var result = await harness.Service.UpdateProfileAsync(
            harness.Customer.Id,
            new UpdateCustomerProfileRequest(
                " Asha ",
                " Sharma ",
                new DateOnly(1992, 4, 9),
                " Female ",
                "+919876543210"),
            CancellationToken.None);

        Assert.Equal("Asha", result.FirstName);
        Assert.Equal("Sharma", result.LastName);
        Assert.Equal("Female", result.Gender);
        Assert.Equal(new DateOnly(1992, 4, 9), result.DateOfBirth);
    }

    [Fact]
    public async Task UpdateProfile_WithFutureDateOfBirth_IsRejected()
    {
        await using var harness = await CustomerHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.UpdateProfileAsync(
                harness.Customer.Id,
                new UpdateCustomerProfileRequest(null, null, new DateOnly(2026, 8, 16), null, null),
                CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(InvalidAddresses))]
    public async Task CreateAddress_WithInvalidInput_IsRejected(UpsertCustomerAddressRequest request)
    {
        await using var harness = await CustomerHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.CreateAddressAsync(harness.Customer.Id, request, CancellationToken.None));
    }

    [Fact]
    public async Task GetAddress_ForAnotherCustomer_ReturnsNotFound()
    {
        await using var harness = await CustomerHarness.CreateAsync();
        var otherCustomer = new User(UserType.Customer);
        harness.Db.Users.Add(otherCustomer);
        await harness.Db.SaveChangesAsync();
        var address = CreateDomainAddress(harness.Customer.Id);
        harness.Db.CustomerAddresses.Add(address);
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.GetAddressAsync(otherCustomer.Id, address.PublicId, CancellationToken.None));
    }

    [Fact]
    public async Task StaffProfileLookup_UsesCustomerPublicId()
    {
        await using var harness = await CustomerHarness.CreateAsync();

        var profile = await harness.Service.GetProfileByCustomerIdAsync(
            harness.Customer.PublicId,
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, profile.PublicId);
        Assert.Equal(harness.Customer.Id, (await harness.Db.CustomerProfiles.SingleAsync()).UserId);
    }

    [Fact]
    public void AddressLifecycle_DefaultSwitchAndDeactivationPreserveHistory()
    {
        var address = CreateDomainAddress(42);

        address.SetDefault();
        Assert.True(address.IsDefault);

        address.Deactivate();
        Assert.False(address.IsActive);
        Assert.False(address.IsDefault);
        Assert.Equal("Home", address.Label);
        Assert.Equal(42, address.UserId);
    }

    public static TheoryData<UpsertCustomerAddressRequest> InvalidAddresses => new()
    {
        ValidAddress() with { Label = " " },
        ValidAddress() with { PinCode = "012345" },
        ValidAddress() with { ContactMobile = "abc" },
        ValidAddress() with { Latitude = null },
        ValidAddress() with { Latitude = 90.0001m },
        ValidAddress() with { Longitude = -180.0001m }
    };

    private static UpsertCustomerAddressRequest ValidAddress() => new(
        "Home",
        "12 Market Road",
        null,
        "Indiranagar",
        "Bengaluru",
        "Karnataka",
        "560038",
        null,
        null,
        "Asha Sharma",
        "+919876543210",
        12.9716m,
        77.5946m,
        true);

    private static CustomerAddress CreateDomainAddress(long userId) => new(
        userId,
        "Home",
        "12 Market Road",
        "Indiranagar",
        "Bengaluru",
        "Karnataka",
        "560038",
        "Asha Sharma",
        "+919876543210",
        12.9716m,
        77.5946m);
}

internal sealed class CustomerHarness : IAsyncDisposable
{
    private CustomerHarness(
        DoodhDirectDbContext db,
        User customer,
        CustomerService service)
    {
        Db = db;
        Customer = customer;
        Service = service;
    }

    public DoodhDirectDbContext Db { get; }
    public User Customer { get; }
    public CustomerService Service { get; }

    public static async Task<CustomerHarness> CreateAsync()
    {
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseInMemoryDatabase($"customer-tests-{Guid.NewGuid():N}")
            .Options;
        var db = new DoodhDirectDbContext(options);
        var customer = new User(UserType.Customer);
        db.Users.Add(customer);
        await db.SaveChangesAsync();
        var clock = new TestClock(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));
        return new CustomerHarness(db, customer, new CustomerService(db, clock));
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
