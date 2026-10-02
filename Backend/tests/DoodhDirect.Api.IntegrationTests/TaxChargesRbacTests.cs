using System.Reflection;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// RBAC reflection tests for the Tax &amp; Charges admin controller — mirrors the
/// BranchController convention: every action carries a "permission:" policy (READ
/// for surfacing, MANAGE for mutations) and nothing is anonymous.
/// </summary>
public sealed class TaxChargesRbacTests
{
    public static TheoryData<string, string> ManageActions => new()
    {
        { nameof(TaxChargesController.Create), "permission:" + AuthorizationCodes.SetupTaxChargesManage },
        { nameof(TaxChargesController.Update), "permission:" + AuthorizationCodes.SetupTaxChargesManage },
        { nameof(TaxChargesController.Activate), "permission:" + AuthorizationCodes.SetupTaxChargesManage },
        { nameof(TaxChargesController.Deactivate), "permission:" + AuthorizationCodes.SetupTaxChargesManage },
        { nameof(TaxChargesController.SetApplicability), "permission:" + AuthorizationCodes.SetupTaxChargesManage },
        { nameof(TaxChargesController.Delete), "permission:" + AuthorizationCodes.SetupTaxChargesManage }
    };

    public static TheoryData<string, string> ReadActions => new()
    {
        { nameof(TaxChargesController.List), "permission:" + AuthorizationCodes.SetupTaxChargesRead },
        { nameof(TaxChargesController.Get), "permission:" + AuthorizationCodes.SetupTaxChargesRead }
    };

    [Fact]
    public void Controller_UsesAdministrationRouteAndEveryActionRequiresPermission()
    {
        var route = Assert.Single(typeof(TaxChargesController).GetCustomAttributes<RouteAttribute>());
        Assert.Equal("api/v1/admin/setup/tax-charges", route.Template);

        // The controller carries a class-level READ policy; mutation actions override
        // it with a method-level MANAGE policy. The effective policy of every action
        // must be a "permission:" policy and nothing may be anonymous.
        var classAuthorize = Assert.Single(typeof(TaxChargesController).GetCustomAttributes<AuthorizeAttribute>());
        var actionMethods = typeof(TaxChargesController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: false).Any())
            .ToList();

        Assert.NotEmpty(actionMethods);
        foreach (var method in actionMethods)
        {
            var authorize = method.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
                .DefaultIfEmpty(classAuthorize)
                .Single();
            Assert.NotNull(authorize.Policy);
            Assert.StartsWith("permission:", authorize.Policy);
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        }
    }

    [Theory]
    [MemberData(nameof(ManageActions))]
    public void ManageAction_UsesManagePermission(string methodName, string permission)
    {
        var authorize = Assert.Single(RequireMethod(methodName).GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        Assert.Equal(permission, authorize.Policy);
    }

    [Theory]
    [MemberData(nameof(ReadActions))]
    public void ReadAction_FallsBackToClassLevelReadPermission(string methodName, string permission)
    {
        var method = RequireMethod(methodName);
        Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        var classAuthorize = Assert.Single(typeof(TaxChargesController).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(permission, classAuthorize.Policy);
    }

    private static MethodInfo RequireMethod(string methodName) =>
        typeof(TaxChargesController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Action '{methodName}' was not found.");
}
