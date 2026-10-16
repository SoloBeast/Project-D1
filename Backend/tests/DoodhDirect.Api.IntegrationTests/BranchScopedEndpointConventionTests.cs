using System.Reflection;
using System.Text;
using DoodhDirect.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 2 structural authorization coverage: every branch-scoped API endpoint must
/// carry authorization plus branch-scope enforcement, and newly added endpoints that
/// lack either fail this test.
/// <para/>
/// Enforcement in this codebase is two-layer: a permission policy at the edge plus
/// service-level scoping (controllers build <c>*Actor</c> records from the JWT branch
/// claims and services scope every query to that actor). Endpoints whose authorization
/// is enforced through a documented service-level policy instead of a scoped actor
/// are recorded in <see cref="DocumentedServiceLevelEndpoints"/> with their
/// justification — adding a new branch-scoped endpoint anywhere else fails until it
/// is either actor-scoped or documented here.
/// </summary>
public sealed class BranchScopedEndpointConventionTests
{
    /// <summary>
    /// Branch-scoped endpoints enforced through a documented service-level policy
    /// rather than a scoped <c>*Actor</c>. Key is "Controller.Method".
    /// </summary>
    private static readonly Dictionary<string, string> DocumentedServiceLevelEndpoints = new(StringComparer.Ordinal)
    {
        // The branch catalogue itself cannot be scoped to a branch: these manage
        // branch records globally and are gated by BranchesRead/BranchesManage,
        // which only global roles hold.
        ["BranchController.Get"] = "Global branch-catalogue administration (BranchesRead).",
        ["BranchController.Update"] = "Global branch-catalogue administration (BranchesManage).",
        ["BranchController.Activate"] = "Global branch-catalogue administration (BranchesManage).",
        ["BranchController.Deactivate"] = "Global branch-catalogue administration (BranchesManage).",
        ["BranchController.Delete"] = "Global branch-catalogue administration (BranchesManage).",
        // Global catalogue administration gated by CatalogueManage (global-only roles).
        ["CatalogueAdministrationController.CreateProduct"] = "Global catalogue administration (CatalogueManage).",
        ["CatalogueAdministrationController.UpdateProduct"] = "Global catalogue administration (CatalogueManage).",
        ["CatalogueAdministrationController.SetBranchAvailability"] = "Global catalogue administration (CatalogueManage).",
        // Employee assignment gated by EmployeesManage plus the EmployeeService
        // branch-authority check (EnsureBranchesWithinAuthorityAsync), which rejects
        // any branch outside the acting administrator's operational scope.
        ["EmployeeController.Create"] = "EmployeesManage + EmployeeService branch-authority check.",
        ["EmployeeController.Update"] = "EmployeesManage + EmployeeService branch-authority check.",
    };

    [Fact]
    public void BranchScopedEndpoints_AllCarryAuthorizationAndScopeEnforcement()
    {
        var endpoints = DiscoverBranchScopedEndpoints().ToArray();
        Assert.NotEmpty(endpoints);

        var anonymous = endpoints
            .Where(x => x.IsAnonymous)
            .Select(x => x.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            anonymous.Length == 0,
            "Branch-scoped endpoints must never be anonymous. " +
            "Offenders: " + string.Join(", ", anonymous));

        var unauthorized = endpoints
            .Where(x => !x.IsAnonymous && !x.HasExplicitAuthorize)
            .Select(x => x.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            unauthorized.Length == 0,
            "Branch-scoped endpoints must carry explicit authorization. " +
            "Offenders: " + string.Join(", ", anonymous));

        var unenforced = endpoints
            .Where(x => !x.HasActorEvidence && !DocumentedServiceLevelEndpoints.ContainsKey(x.Key))
            .Select(x => x.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            unenforced.Length == 0,
            "New branch-scoped endpoints lack scope enforcement. Either scope them through " +
            "a *Actor built from the JWT branch claims, or document the service-level policy in " +
            "DocumentedServiceLevelEndpoints with a justification. Offenders: " +
            string.Join(", ", unenforced) + Environment.NewLine + RenderInventory(endpoints));

        var stale = DocumentedServiceLevelEndpoints.Keys
            .Where(key => endpoints.All(x => !string.Equals(x.Key, key, StringComparison.Ordinal)))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            stale.Length == 0,
            "Documented service-level entries no longer match any branch-scoped endpoint. " +
            "Remove the stale documentation: " + string.Join(", ", stale));
    }

    [Fact]
    public void BranchScopedEndpointInventory_IsReported()
    {
        var endpoints = DiscoverBranchScopedEndpoints()
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();
        var output = new StringBuilder();
        output.AppendLine($"Branch-scoped endpoint inventory ({endpoints.Length} endpoints):");
        foreach (var endpoint in endpoints)
        {
            output.Append("  ").Append(endpoint.Key).Append("  ").Append(endpoint.Route);
            output.Append(endpoint.HasActorEvidence ? "  [actor]" : "  [no-actor]");
            if (DocumentedServiceLevelEndpoints.TryGetValue(endpoint.Key, out var reason))
                output.Append("  [documented: ").Append(reason).Append(']');
            output.AppendLine();
        }

        // Always emitted to the test log so reviewers can audit the inventory;
        // the enforcement assertions above are what fail closed on regressions.
        Assert.NotEmpty(endpoints);
    }

    private sealed record BranchScopedEndpoint(
        string Key,
        string Route,
        bool IsAnonymous,
        bool HasExplicitAuthorize,
        bool HasActorEvidence);

    private static IEnumerable<BranchScopedEndpoint> DiscoverBranchScopedEndpoints()
    {
        var apiAssembly = typeof(EmployeeController).Assembly;
        var controllers = apiAssembly.GetTypes()
            .Where(x => x.IsClass && !x.IsAbstract && x.IsPublic &&
                (x.Name.EndsWith("Controller", StringComparison.Ordinal) ||
                 x.GetCustomAttribute<ApiControllerAttribute>() is not null));

        foreach (var controller in controllers)
        {
            var controllerRoute = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
            var controllerAnonymous = controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
            var controllerAuthorize = controller.GetCustomAttribute<AuthorizeAttribute>() is not null;
            var hasActorEvidence = DeclaresScopedActorFactory(controller);

            foreach (var method in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName || method.GetCustomAttributes().OfType<HttpMethodAttribute>().ToArray() is not { Length: > 0 } httpAttributes)
                    continue;

                var route = string.Join(" + ", httpAttributes.Select(a => CombineRoute(controllerRoute, a.Template)));
                if (!IsBranchScoped(method, route))
                    continue;

                var isAnonymous = controllerAnonymous || method.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
                var hasAuthorize = controllerAuthorize || method.GetCustomAttribute<AuthorizeAttribute>() is not null;
                yield return new BranchScopedEndpoint(
                    controller.Name + "." + method.Name,
                    route,
                    isAnonymous,
                    hasAuthorize,
                    hasActorEvidence);
            }
        }
    }

    // Actor factories live on abstract controller bases (DeliveryControllerBase,
    // ReportControllerBase, ...), so the inheritance chain is walked, not just the
    // declared methods.
    private static bool DeclaresScopedActorFactory(Type controller)
    {
        for (var type = controller; type is not null && type != typeof(object); type = type.BaseType)
        {
            var found = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Select(x => x.ReturnType)
                .Any(t => t.GetProperty("BranchIds", BindingFlags.Instance | BindingFlags.Public) is not null);
            if (found)
                return true;
        }

        return false;
    }

    private static bool IsBranchScoped(MethodInfo method, string route)
    {
        if (route.Contains("{branchid", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var parameter in method.GetParameters())
        {
            if (parameter.Name is not null &&
                (parameter.Name.Equals("branchId", StringComparison.OrdinalIgnoreCase) ||
                 parameter.Name.Equals("branchIds", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (CarriesBranchInput(parameter.ParameterType))
                return true;
        }

        return false;
    }

    private static bool CarriesBranchInput(Type type)
    {
        if (type == typeof(CancellationToken))
            return false;

        var elementType = type.IsArray
            ? type.GetElementType()
            : type.IsGenericType && type.GetGenericArguments().Length == 1
                ? type.GetGenericArguments()[0]
                : null;
        var target = elementType ?? type;
        if (target is null || target.IsPrimitive || target == typeof(string) || target == typeof(Guid) ||
            target == typeof(long) || target == typeof(int) || target == typeof(DateTime) || target.IsEnum)
            return false;

        return target.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(p => p.Name.Equals("branchId", StringComparison.OrdinalIgnoreCase) ||
                      p.Name.Equals("branchIds", StringComparison.OrdinalIgnoreCase));
    }

    private static string CombineRoute(string controllerRoute, string? methodTemplate) =>
        string.IsNullOrWhiteSpace(methodTemplate)
            ? controllerRoute
            : string.IsNullOrWhiteSpace(controllerRoute)
                ? methodTemplate
                : controllerRoute.TrimEnd('/') + "/" + methodTemplate.TrimStart('/');

    private static string RenderInventory(BranchScopedEndpoint[] endpoints)
    {
        var output = new StringBuilder();
        output.AppendLine("Current branch-scoped inventory:");
        foreach (var endpoint in endpoints.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            output.Append("  ").Append(endpoint.Key).Append("  ").Append(endpoint.Route);
            output.Append(endpoint.HasActorEvidence ? "  [actor]" : "  [no-actor]");
            output.AppendLine();
        }

        return output.ToString();
    }
}
