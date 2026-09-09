using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace DoodhDirect.Infrastructure.Identity;

/// <summary>
/// Opt-in bootstrap for non-Development environments (UAT, Staging): when
/// 'UatBootstrap:Enabled' is exactly true it creates/ensures exactly one Owner and
/// one System Administrator. Credentials are supplied at runtime through
/// configuration (environment variables or appsettings) — there are no hard-coded
/// defaults and the service fails closed if they are missing while enabled. When
/// Enabled is absent or false the service is skipped, leaving the environment with
/// zero users.
/// Roles and permissions are ensured by <see cref="IdentitySeedService"/>, which
/// runs before this service during application startup.
/// </summary>
public sealed class UatBootstrapSeedService(
    DoodhDirectDbContext dbContext,
    IPasswordHasher passwordHasher,
    IIndiaTimeProvider timeProvider,
    IConfiguration configuration,
    IHostEnvironment environment)
{
    public const string SectionName = "UatBootstrap";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        // UatBootstrap is opt-in: non-Development environments start with zero users
        // unless 'UatBootstrap:Enabled' is configured as exactly true (case-insensitive).
        // Missing, malformed or false values all skip bootstrap; credentials are only
        // required when bootstrap is actually enabled.
        var enabledValue = configuration[$"{SectionName}:Enabled"];
        if (!bool.TryParse(enabledValue, out var enabled) || !enabled)
        {
            return;
        }

        var owner = LoadAccount("Owner", AuthorizationCodes.Owner, UserType.Owner, "Owner");
        var systemAdmin = LoadAccount(
            "SystemAdmin",
            AuthorizationCodes.SystemAdmin,
            UserType.SystemAdministrator,
            "System Administrator");

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        await executionStrategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

            var roleCodes = new[] { owner.RoleCode, systemAdmin.RoleCode };
            var roles = await dbContext.Roles
                .Where(role => roleCodes.Contains(role.Code))
                .ToDictionaryAsync(role => role.Code, StringComparer.Ordinal, cancellationToken);
            if (roles.Count != roleCodes.Length)
            {
                throw new InvalidOperationException(
                    $"UatBootstrap requires the roles '{AuthorizationCodes.Owner}' and " +
                    $"'{AuthorizationCodes.SystemAdmin}' to exist. IdentitySeedService must run first.");
            }

            await EnsureAccountAsync(owner, roles[owner.RoleCode], cancellationToken);
            await EnsureAccountAsync(systemAdmin, roles[systemAdmin.RoleCode], cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task EnsureAccountAsync(
        BootstrapAccount account,
        Role role,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .Include(item => item.UserRoles)
            .SingleOrDefaultAsync(item => item.Email == account.Email, cancellationToken);

        if (user is null)
        {
            user = new User(account.UserType);
            user.SetProfile(account.DisplayName);
            user.SetContact(null, account.Email);
            user.SetPasswordHash(passwordHasher.Hash(account.Password));
            user.Activate();
            user.MarkEmailVerified(timeProvider.Now);
            user.AssignRole(role, branchId: null);
            dbContext.Users.Add(user);
            return;
        }

        if (user.UserType != account.UserType)
        {
            throw new InvalidOperationException(
                $"UatBootstrap expected user '{account.Email}' to be a {account.UserType} " +
                $"but found {user.UserType}. Refusing to mutate the existing account.");
        }

        if (!user.IsActive)
        {
            user.Activate();
        }

        if (!user.HasVerifiedEmail)
        {
            user.MarkEmailVerified(timeProvider.Now);
        }

        if (!user.UserRoles.Any(assignment => assignment.RoleId == role.Id && assignment.BranchId == null))
        {
            user.AssignRole(role, branchId: null);
        }
    }

    private BootstrapAccount LoadAccount(
        string accountKey,
        string roleCode,
        UserType userType,
        string defaultDisplayName)
    {
        var prefix = $"{SectionName}:{accountKey}";
        var email = configuration[$"{prefix}:Email"];
        var password = configuration[$"{prefix}:Password"];
        var displayName = configuration[$"{prefix}:DisplayName"];

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException(
                $"UatBootstrap requires '{prefix}:Email' to be configured " +
                $"(environment variable 'UatBootstrap__{accountKey}__Email') before the API " +
                "can start in this environment.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"UatBootstrap requires '{prefix}:Password' to be configured " +
                $"(environment variable 'UatBootstrap__{accountKey}__Password') before the API " +
                "can start in this environment.");
        }

        return new BootstrapAccount(
            email.Trim().ToLowerInvariant(),
            password,
            string.IsNullOrWhiteSpace(displayName) ? defaultDisplayName : displayName.Trim(),
            roleCode,
            userType);
    }

    private sealed record BootstrapAccount(
        string Email,
        string Password,
        string DisplayName,
        string RoleCode,
        UserType UserType);
}
