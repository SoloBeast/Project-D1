using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 2 device lifecycle tests: owner-scoped deactivation, idempotency,
/// cross-user isolation, and logout cleanup that touches only the session's
/// own device. No push provider is configured and no message is ever sent.
/// </summary>
public sealed class DeviceDeactivationTests
{
    private static readonly DeviceInfo DeviceA = new(
        "lifecycle-device-a",
        "Integration test device A",
        "test",
        "127.0.0.1",
        "DoodhDirect.Tests");

    [Fact]
    public async Task Owner_can_deactivate_own_device()
    {
        await using var fixture = await DeviceFixture.CreateAsync();

        var registered = await fixture.Service.RegisterDeviceAsync(
            fixture.Actor,
            new RegisterDeviceRequest("lifecycle-device-a", "push-token-a", "android", "Device A"),
            default);
        Assert.True(registered.IsActive);

        var deactivated = await fixture.Service.DeactivateDeviceAsync(
            fixture.Actor,
            "lifecycle-device-a",
            default);

        Assert.False(deactivated.IsActive);
        Assert.Equal(registered.DeviceId, deactivated.DeviceId);
        Assert.False((await fixture.DeviceAsync("lifecycle-device-a")).IsActive);
    }

    [Fact]
    public async Task Repeated_deactivation_is_safe()
    {
        await using var fixture = await DeviceFixture.CreateAsync();
        await fixture.Service.RegisterDeviceAsync(
            fixture.Actor,
            new RegisterDeviceRequest("lifecycle-device-a", "push-token-a", "android", "Device A"),
            default);

        var first = await fixture.Service.DeactivateDeviceAsync(fixture.Actor, "lifecycle-device-a", default);
        var second = await fixture.Service.DeactivateDeviceAsync(fixture.Actor, "lifecycle-device-a", default);

        Assert.False(first.IsActive);
        Assert.False(second.IsActive);
        Assert.Equal(1, await fixture.Db.UserDevices.CountAsync());
    }

    [Fact]
    public async Task User_cannot_deactivate_another_users_device()
    {
        await using var fixture = await DeviceFixture.CreateAsync();
        await fixture.Service.RegisterDeviceAsync(
            fixture.OtherActor,
            new RegisterDeviceRequest("shared-install-id", "push-token-other", "android", "Other device"),
            default);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            fixture.Service.DeactivateDeviceAsync(
                fixture.Actor,
                "shared-install-id",
                default));

        Assert.True((await fixture.DeviceAsync("shared-install-id")).IsActive);
    }

    [Fact]
    public async Task Deactivating_unknown_device_returns_not_found()
    {
        await using var fixture = await DeviceFixture.CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            fixture.Service.DeactivateDeviceAsync(fixture.Actor, "no-such-device", default));
    }

    [Fact]
    public async Task Deactivated_device_can_be_re_registered_for_account_switch()
    {
        await using var fixture = await DeviceFixture.CreateAsync();
        await fixture.Service.RegisterDeviceAsync(
            fixture.Actor,
            new RegisterDeviceRequest("shared-install-id", "push-token-a", "android", "Device A"),
            default);
        await fixture.Service.DeactivateDeviceAsync(fixture.Actor, "shared-install-id", default);

        var switched = await fixture.Service.RegisterDeviceAsync(
            fixture.OtherActor,
            new RegisterDeviceRequest("shared-install-id", "push-token-a", "android", "Device B"),
            default);

        Assert.True(switched.IsActive);
        var oldRow = await fixture.Db.UserDevices.AsNoTracking()
            .SingleAsync(x => x.UserId == fixture.Actor.UserId);
        Assert.False(oldRow.IsActive);
    }

    [Fact]
    public async Task Logout_deactivates_only_the_sessions_own_device()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Lifecycle Customer", "lifecycle@example.com", null, "correct-password", DeviceA),
            CancellationToken.None);
        var user = await harness.Db.Users.SingleAsync();
        var session = await harness.Db.UserSessions.SingleAsync();
        var notifications = NotificationServiceOn(harness.Db, harness.Clock);

        await notifications.RegisterDeviceAsync(
            new NotificationActor(user.Id),
            new RegisterDeviceRequest("lifecycle-device-a", "push-token-a", "android", "Device A"),
            default);
        await notifications.RegisterDeviceAsync(
            new NotificationActor(user.Id),
            new RegisterDeviceRequest("lifecycle-device-b", "push-token-b", "android", "Device B"),
            default);

        await harness.Authentication.LogoutAsync(session.PublicId, user.Id, CancellationToken.None);

        Assert.False(session.IsActive);
        Assert.False((await DeviceOnAsync(harness.Db, user.Id, "lifecycle-device-a")).IsActive);
        Assert.True((await DeviceOnAsync(harness.Db, user.Id, "lifecycle-device-b")).IsActive);
        Assert.NotNull(registered.Tokens.AccessToken);
    }

    private static NotificationService NotificationServiceOn(DoodhDirectDbContext db, TestClock clock) =>
        new(
            db,
            new SecureTokenGenerator(),
            new NotificationTokenProtector(new EphemeralDataProtectionProvider()),
            clock);

    private static async Task<UserDevice> DeviceOnAsync(
        DoodhDirectDbContext db,
        long userId,
        string deviceIdentifier)
    {
        var hash = new SecureTokenGenerator().Hash(deviceIdentifier);
        return await db.UserDevices.AsNoTracking()
            .SingleAsync(x => x.UserId == userId && x.DeviceIdentifierHash == hash);
    }

    private sealed class DeviceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public DoodhDirectDbContext Db { get; }
        public NotificationService Service { get; }
        public NotificationActor Actor { get; }
        public NotificationActor OtherActor { get; }

        private DeviceFixture(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            NotificationService service,
            NotificationActor actor,
            NotificationActor otherActor)
        {
            _connection = connection;
            Db = db;
            Service = service;
            Actor = actor;
            OtherActor = otherActor;
        }

        public static async Task<DeviceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new DoodhDirectDbContext(
                new DbContextOptionsBuilder<DoodhDirectDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("Customer", "9999999999", "customer@example.com");
            var otherCustomer = CreateCustomer("Other Customer", "8888888888", "other@example.com");
            db.Users.AddRange(customer, otherCustomer);
            await db.SaveChangesAsync();

            var clock = new TestClock(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Unspecified));
            var service = new NotificationService(
                db,
                new SecureTokenGenerator(),
                new NotificationTokenProtector(new EphemeralDataProtectionProvider()),
                clock);
            return new DeviceFixture(
                connection,
                db,
                service,
                new NotificationActor(customer.Id),
                new NotificationActor(otherCustomer.Id));
        }

        private static User CreateCustomer(string name, string mobile, string email)
        {
            var user = new User(UserType.Customer);
            user.SetProfile(name);
            user.SetContact(mobile, email);
            return user;
        }

        public async Task<UserDevice> DeviceAsync(string deviceIdentifier)
        {
            Db.ChangeTracker.Clear();
            var hash = new SecureTokenGenerator().Hash(deviceIdentifier);
            return await Db.UserDevices.AsNoTracking()
                .SingleAsync(x => x.DeviceIdentifierHash == hash);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
