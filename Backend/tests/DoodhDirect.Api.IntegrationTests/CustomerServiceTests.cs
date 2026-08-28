using DoodhDirect.Application.Common;
using DoodhDirect.Application.Customer;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.Sqlite;
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

    [Theory]
    [InlineData("9876543210")]
    [InlineData("919876543210")]
    [InlineData("+919876543210")]
    public async Task UpdateProfile_AcceptsSupportedIndianMobileForms(string mobile)
    {
        await using var harness = await CustomerHarness.CreateAsync();

        var result = await harness.Service.UpdateProfileAsync(
            harness.Customer.Id,
            new UpdateCustomerProfileRequest(null, null, null, null, mobile),
            CancellationToken.None);

        Assert.Equal(mobile, result.AlternateMobile);
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("987654321")]
    [InlineData("98765432101")]
    [InlineData("+91 9876543210")]
    [InlineData("98765-43210")]
    public async Task UpdateProfile_RejectsInvalidAlternateMobile(string mobile)
    {
        await using var harness = await CustomerHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.UpdateProfileAsync(
                harness.Customer.Id,
                new UpdateCustomerProfileRequest(null, null, null, null, mobile),
                CancellationToken.None));

        Assert.Equal(nameof(UpdateCustomerProfileRequest.AlternateMobile), exception.Field);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Male")]
    [InlineData("Female")]
    [InlineData("Other")]
    public async Task UpdateProfile_AllowsOptionalSupportedGender(string? gender)
    {
        await using var harness = await CustomerHarness.CreateAsync();

        var result = await harness.Service.UpdateProfileAsync(
            harness.Customer.Id,
            new UpdateCustomerProfileRequest(null, null, null, gender, null),
            CancellationToken.None);

        Assert.Equal(string.IsNullOrWhiteSpace(gender) ? null : gender, result.Gender);
    }

    [Fact]
    public async Task UpdateProfile_RejectsUnsupportedGender()
    {
        await using var harness = await CustomerHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.UpdateProfileAsync(
                harness.Customer.Id,
                new UpdateCustomerProfileRequest(null, null, null, "Unknown", null),
                CancellationToken.None));

        Assert.Equal(nameof(UpdateCustomerProfileRequest.Gender), exception.Field);
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
    public async Task DefaultAddressSwitching_IsAtomicAndPreservesCoordinates()
    {
        await using var harness = await CustomerHarness.CreateAsync();

        var first = await harness.Service.CreateAddressAsync(
            harness.Customer.Id,
            ValidAddress() with { IsDefault = true },
            CancellationToken.None);
        var second = await harness.Service.CreateAddressAsync(
            harness.Customer.Id,
            ValidAddress() with
            {
                Label = "Office",
                IsDefault = false,
                Latitude = 13.0827m,
                Longitude = 80.2707m,
            },
            CancellationToken.None);

        var switched = await harness.Service.UpdateAddressAsync(
            harness.Customer.Id,
            second.PublicId,
            ValidAddress() with
            {
                Label = "Office updated",
                IsDefault = true,
                Latitude = second.Latitude,
                Longitude = second.Longitude,
            },
            CancellationToken.None);

        var addresses = await harness.Service.GetAddressesAsync(
            harness.Customer.Id,
            CancellationToken.None);
        Assert.False(addresses.Single(x => x.PublicId == first.PublicId).IsDefault);
        Assert.True(switched.IsDefault);
        Assert.Equal(13.0827m, switched.Latitude);
        Assert.Equal(80.2707m, switched.Longitude);
        Assert.Single(addresses, x => x.IsDefault);
    }

    [Fact]
    public async Task DeactivatedOrForeignAddress_CannotBeMutated()
    {
        await using var harness = await CustomerHarness.CreateAsync();
        var address = await harness.Service.CreateAddressAsync(
            harness.Customer.Id, ValidAddress(), CancellationToken.None);
        await harness.Service.DeactivateAddressAsync(
            harness.Customer.Id, address.PublicId, CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.UpdateAddressAsync(
                harness.Customer.Id,
                address.PublicId,
                ValidAddress(),
                CancellationToken.None));

        var otherCustomer = new User(UserType.Customer);
        harness.Db.Users.Add(otherCustomer);
        await harness.Db.SaveChangesAsync();
        var foreignAddress = await harness.Service.CreateAddressAsync(
            otherCustomer.Id, ValidAddress(), CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.UpdateAddressAsync(
                harness.Customer.Id,
                foreignAddress.PublicId,
                ValidAddress(),
                CancellationToken.None));
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("987654321")]
    [InlineData("98765432101")]
    [InlineData("abc")]
    public async Task CreateAddress_RejectsInvalidContactMobile(string mobile)
    {
        await using var harness = await CustomerHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.CreateAddressAsync(
                harness.Customer.Id,
                ValidAddress() with { ContactMobile = mobile },
                CancellationToken.None));

        Assert.Equal(nameof(UpsertCustomerAddressRequest.ContactMobile), exception.Field);
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
        SqliteConnection connection,
        DoodhDirectDbContext db,
        User customer,
        CustomerService service)
    {
        Connection = connection;
        Db = db;
        Customer = customer;
        Service = service;
    }

    public SqliteConnection Connection { get; }
    public DoodhDirectDbContext Db { get; }
    public User Customer { get; }
    public CustomerService Service { get; }

    public static async Task<CustomerHarness> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new DoodhDirectDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var customer = new User(UserType.Customer);
        db.Users.Add(customer);
        await db.SaveChangesAsync();
        var clock = new TestClock(
            new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Unspecified));
        var timeProvider = new TestIndiaTimeProvider(clock);
        db.NumberSeries.Add(new NumberSeries(
            "CUSTOMER", "Customer Number", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never));
        await db.SaveChangesAsync();
        return new CustomerHarness(
            connection,
            db,
            customer,
            new CustomerService(db, new NumberSeriesService(db, timeProvider), timeProvider));
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
