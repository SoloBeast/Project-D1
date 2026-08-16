using System.Globalization;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.Authorization;

public static class AuthorizationPolicyNames
{
    private const string PermissionPrefix = "Permission:";
    private const string BranchPrefix = "Branch:";

    public static string Permission(string permission) => $"{PermissionPrefix}{permission}";

    public static string Branch(string permission) => $"{BranchPrefix}{permission}";

    internal static bool TryGetPermission(string policyName, out string permission)
    {
        if (policyName.StartsWith(PermissionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            permission = policyName[PermissionPrefix.Length..];
            return !string.IsNullOrWhiteSpace(permission);
        }

        permission = string.Empty;
        return false;
    }

    internal static bool TryGetBranchPermission(string policyName, out string permission)
    {
        if (policyName.StartsWith(BranchPrefix, StringComparison.OrdinalIgnoreCase))
        {
            permission = policyName[BranchPrefix.Length..];
            return !string.IsNullOrWhiteSpace(permission);
        }

        permission = string.Empty;
        return false;
    }
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed record BranchScopeRequirement(string RouteValueKey = "branchId") : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.HasClaim(AuthorizationCodes.PermissionClaim, requirement.Permission))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public sealed class BranchScopeAuthorizationHandler : AuthorizationHandler<BranchScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BranchScopeRequirement requirement)
    {
        if (context.User.HasClaim(
            AuthorizationCodes.PermissionClaim,
            AuthorizationCodes.GlobalAccess))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.Resource is not HttpContext httpContext ||
            !httpContext.Request.RouteValues.TryGetValue(requirement.RouteValueKey, out var routeValue) ||
            !long.TryParse(
                Convert.ToString(routeValue, CultureInfo.InvariantCulture),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var branchId))
        {
            return Task.CompletedTask;
        }

        var expectedBranchId = branchId.ToString(CultureInfo.InvariantCulture);
        if (context.User.HasClaim(AuthorizationCodes.BranchClaim, expectedBranchId))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public sealed class DoodhDirectAuthorizationPolicyProvider(
    IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (AuthorizationPolicyNames.TryGetPermission(policyName, out var permission))
        {
            return Task.FromResult<AuthorizationPolicy?>(
                new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission))
                    .Build());
        }

        if (AuthorizationPolicyNames.TryGetBranchPermission(policyName, out permission))
        {
            return Task.FromResult<AuthorizationPolicy?>(
                new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(
                        new PermissionRequirement(permission),
                        new BranchScopeRequirement())
                    .Build());
        }

        return base.GetPolicyAsync(policyName);
    }
}
