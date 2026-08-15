using System.Security.Claims;
using DoodhDirect.Api.Authorization;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class AuthorizationHandlerTests
{
    private const string TestPermission = "IDENTITY.USERS.READ";

    [Fact]
    public async Task PermissionHandler_Succeeds_WhenRequiredPermissionClaimIsPresent()
    {
        var requirement = new PermissionRequirement(TestPermission);
        var context = CreateContext(
            requirement,
            Claim(AuthorizationCodes.PermissionClaim, TestPermission));

        await new PermissionAuthorizationHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task PermissionHandler_DoesNotSucceed_WhenRequiredPermissionClaimIsAbsent()
    {
        var requirement = new PermissionRequirement(TestPermission);
        var context = CreateContext(
            requirement,
            Claim(AuthorizationCodes.PermissionClaim, "IDENTITY.USERS.WRITE"));

        await new PermissionAuthorizationHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.False(context.HasFailed);
    }

    [Fact]
    public async Task BranchHandler_Succeeds_WhenRouteBranchMatchesClaim()
    {
        var requirement = new BranchScopeRequirement();
        var httpContext = CreateHttpContext(branchId: "42");
        var context = CreateContext(
            requirement,
            httpContext,
            Claim(AuthorizationCodes.BranchClaim, "42"));

        await new BranchScopeAuthorizationHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task BranchHandler_DoesNotSucceed_WhenRouteBranchDoesNotMatchClaim()
    {
        var requirement = new BranchScopeRequirement();
        var httpContext = CreateHttpContext(branchId: "84");
        var context = CreateContext(
            requirement,
            httpContext,
            Claim(AuthorizationCodes.BranchClaim, "42"));

        await new BranchScopeAuthorizationHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public async Task BranchHandler_DoesNotSucceed_WhenRouteBranchIsMissingOrInvalid(string? branchId)
    {
        var requirement = new BranchScopeRequirement();
        var httpContext = CreateHttpContext(branchId);
        var context = CreateContext(
            requirement,
            httpContext,
            Claim(AuthorizationCodes.BranchClaim, "42"));

        await new BranchScopeAuthorizationHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task BranchHandler_SucceedsWithExplicitGlobalPermission_WithoutBranchClaimOrRoute()
    {
        var requirement = new BranchScopeRequirement();
        var context = CreateContext(
            requirement,
            new DefaultHttpContext(),
            Claim(AuthorizationCodes.PermissionClaim, AuthorizationCodes.GlobalAccess));

        await new BranchScopeAuthorizationHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task PolicyProvider_BuildsAuthenticatedPermissionPolicy()
    {
        var provider = CreatePolicyProvider();

        var policy = await provider.GetPolicyAsync(AuthorizationPolicyNames.Permission(TestPermission));

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, requirement =>
            requirement is DenyAnonymousAuthorizationRequirement);
        var permission = Assert.Single(policy.Requirements.OfType<PermissionRequirement>());
        Assert.Equal(TestPermission, permission.Permission);
        Assert.Empty(policy.Requirements.OfType<BranchScopeRequirement>());
    }

    [Fact]
    public async Task PolicyProvider_BuildsAuthenticatedBranchPermissionPolicy()
    {
        var provider = CreatePolicyProvider();

        var policy = await provider.GetPolicyAsync(AuthorizationPolicyNames.Branch(TestPermission));

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, requirement =>
            requirement is DenyAnonymousAuthorizationRequirement);
        var permission = Assert.Single(policy.Requirements.OfType<PermissionRequirement>());
        Assert.Equal(TestPermission, permission.Permission);
        Assert.Single(policy.Requirements.OfType<BranchScopeRequirement>());
    }

    [Theory]
    [InlineData("Permission:")]
    [InlineData("Branch:")]
    [InlineData("Unknown:IDENTITY.USERS.READ")]
    public async Task PolicyProvider_DoesNotCreatePolicy_ForMalformedOrUnknownDynamicName(string policyName)
    {
        var provider = CreatePolicyProvider();

        var policy = await provider.GetPolicyAsync(policyName);

        Assert.Null(policy);
    }

    private static DoodhDirectAuthorizationPolicyProvider CreatePolicyProvider() =>
        new(Options.Create(new AuthorizationOptions()));

    private static AuthorizationHandlerContext CreateContext(
        IAuthorizationRequirement requirement,
        params Claim[] claims) =>
        CreateContext(requirement, resource: null, claims);

    private static AuthorizationHandlerContext CreateContext(
        IAuthorizationRequirement requirement,
        object? resource,
        params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        return new AuthorizationHandlerContext(
            new[] { requirement },
            new ClaimsPrincipal(identity),
            resource);
    }

    private static DefaultHttpContext CreateHttpContext(string? branchId)
    {
        var context = new DefaultHttpContext();
        if (branchId is not null)
            context.Request.RouteValues["branchId"] = branchId;

        return context;
    }

    private static Claim Claim(string type, string value) => new(type, value);
}
