using System.Security.Claims;
using System.Text.Json;
using DoodhDirect.Api.Authorization;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Identity;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class IdentitySeedServiceTests
{
    [Fact]
    public async Task SeedAsync_IsIdempotent_AndSeedsCanonicalRolesPermissionsAndOwnerGlobalAccess()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        var service = new IdentitySeedService(db);

        await service.SeedAsync(CancellationToken.None);
        var firstRoleCount = await db.Roles.CountAsync();
        var firstPermissionCount = await db.Permissions.CountAsync();
        var firstAssignmentCount = await db.RolePermissions.CountAsync();

        await service.SeedAsync(CancellationToken.None);

        Assert.Equal(AuthorizationCodes.Roles.Count, firstRoleCount);
        Assert.Equal(AuthorizationCodes.Permissions.Count, firstPermissionCount);
        Assert.Equal(firstRoleCount, await db.Roles.CountAsync());
        Assert.Equal(firstPermissionCount, await db.Permissions.CountAsync());
        Assert.Equal(firstAssignmentCount, await db.RolePermissions.CountAsync());

        var ownerPermissionCodes = await db.RolePermissions
            .Where(assignment => assignment.Role.Code == AuthorizationCodes.Owner)
            .Select(assignment => assignment.Permission.Code)
            .ToListAsync();

        Assert.Equal(
            AuthorizationCodes.Permissions.Keys.OrderBy(code => code),
            ownerPermissionCodes.OrderBy(code => code));
        Assert.Contains(AuthorizationCodes.GlobalAccess, ownerPermissionCodes);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DoodhDirectDbContext>(options => options
            .UseInMemoryDatabase($"identity-seed-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        return services.BuildServiceProvider();
    }
}

public sealed class AuditingAuthorizationMiddlewareResultHandlerTests
{
    private static readonly AuthorizationPolicy Policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    [Fact]
    public async Task HandleAsync_WritesUnauthorizedEnvelopeAndChallengeAudit()
    {
        await using var harness = CreateHarness();
        var context = CreateHttpContext(harness.Provider, "/api/v1/auth/me");
        var nextCalled = false;

        await harness.Handler.HandleAsync(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            context,
            Policy,
            PolicyAuthorizationResult.Challenge());

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        AssertEnvelope(context, "UNAUTHORIZED", "Authentication is required.");

        Assert.Null(harness.Logger.Exception);
        var audit = Assert.Single(await harness.ReadAuditsAsync());
        Assert.Null(audit.UserId);
        Assert.Equal("AUTHORIZATION_CHALLENGED", audit.Action);
        Assert.Equal("Endpoint", audit.EntityType);
        Assert.Equal("GET /api/v1/auth/me", audit.Reason);
        Assert.Equal(harness.Clock.UtcNow, audit.CreatedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_WritesForbiddenEnvelopeAndUserAudit()
    {
        await using var harness = CreateHarness();
        var context = CreateHttpContext(harness.Provider, "/api/v1/branches/42");
        context.Request.Method = HttpMethods.Post;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("user_id", "73")],
            authenticationType: "Test"));

        await harness.Handler.HandleAsync(
            _ => Task.CompletedTask,
            context,
            Policy,
            PolicyAuthorizationResult.Forbid());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        AssertEnvelope(
            context,
            "FORBIDDEN",
            "You are not authorized to perform this action.");

        Assert.Null(harness.Logger.Exception);
        var audit = Assert.Single(await harness.ReadAuditsAsync());
        Assert.Equal(73, audit.UserId);
        Assert.Equal("AUTHORIZATION_FORBIDDEN", audit.Action);
        Assert.Equal("POST /api/v1/branches/42", audit.Reason);
    }

    [Fact]
    public async Task HandleAsync_DelegatesSuccessfulAuthorization_WithoutAudit()
    {
        await using var harness = CreateHarness();
        var context = CreateHttpContext(harness.Provider, "/api/v1/auth/me");
        var nextCalled = false;

        await harness.Handler.HandleAsync(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            context,
            Policy,
            PolicyAuthorizationResult.Success());

        Assert.True(nextCalled);
        Assert.Empty(await harness.ReadAuditsAsync());
    }

    [Fact]
    public async Task HandleAsync_ReturnsAuthorizationResponse_WhenAuditPersistenceFails()
    {
        var handler = new AuditingAuthorizationMiddlewareResultHandler(
            new ThrowingScopeFactory(),
            new TestClock(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc)),
            NullLogger<AuditingAuthorizationMiddlewareResultHandler>.Instance);
        var context = CreateHttpContext(new ServiceCollection().BuildServiceProvider(), "/protected");

        await handler.HandleAsync(
            _ => Task.CompletedTask,
            context,
            Policy,
            PolicyAuthorizationResult.Challenge());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        AssertEnvelope(context, "UNAUTHORIZED", "Authentication is required.");
    }

    private static AuthorizationAuditHarness CreateHarness()
    {
        var databaseName = $"authorization-audit-tests-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<DoodhDirectDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        var provider = services.BuildServiceProvider();
        var clock = new TestClock(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));
        var logger = new CapturingLogger<AuditingAuthorizationMiddlewareResultHandler>();
        var handler = new AuditingAuthorizationMiddlewareResultHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clock,
            logger);
        return new AuthorizationAuditHarness(provider, clock, handler, logger);
    }

    private static DefaultHttpContext CreateHttpContext(IServiceProvider provider, string path)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static void AssertEnvelope(DefaultHttpContext context, string code, string message)
    {
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(message, root.GetProperty("message").GetString());
        var error = Assert.Single(root.GetProperty("errors").EnumerateArray());
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(message, error.GetProperty("message").GetString());
    }

    private sealed record AuthorizationAuditHarness(
        ServiceProvider Provider,
        TestClock Clock,
        AuditingAuthorizationMiddlewareResultHandler Handler,
        CapturingLogger<AuditingAuthorizationMiddlewareResultHandler> Logger) : IAsyncDisposable
    {
        public async Task<List<DoodhDirect.Domain.Auditing.AuditLog>> ReadAuditsAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
            return await db.AuditLogs.AsNoTracking().ToListAsync();
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NullLogger<T>.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Exception = exception;
        }
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("Simulated audit storage failure.");
    }
}
