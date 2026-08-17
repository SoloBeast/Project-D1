using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class ReportsControllerTests
{
    public static TheoryData<string, string, string> ReadActions => new()
    {
        { nameof(ReportsController.GetDashboard), "dashboard", AuthorizationCodes.ReportsDashboardRead },
        { nameof(ReportsController.GetCustomers), "customers", AuthorizationCodes.ReportsAdministrationRead },
        { nameof(ReportsController.GetEmployees), "employees", AuthorizationCodes.ReportsAdministrationRead },
        { nameof(ReportsController.GetOrders), "orders", AuthorizationCodes.ReportsAdministrationRead },
        { nameof(ReportsController.GetSubscriptions), "subscriptions", AuthorizationCodes.ReportsAdministrationRead },
        { nameof(ReportsController.GetPayments), "payments", AuthorizationCodes.ReportsFinancialRead },
        { nameof(ReportsController.GetWallets), "wallets", AuthorizationCodes.ReportsFinancialRead },
        { nameof(ReportsController.GetDeliveries), "deliveries", AuthorizationCodes.ReportsOperationsRead },
        { nameof(ReportsController.GetDairy), "dairy", AuthorizationCodes.ReportsOperationsRead },
        { nameof(ReportsController.GetMilkTests), "milk-tests", AuthorizationCodes.ReportsMilkTestsRead },
        { nameof(ReportsController.GetCameras), "cameras", AuthorizationCodes.ReportsOperationsRead },
        { nameof(ReportsController.GetNotifications), "notifications", AuthorizationCodes.ReportsOperationsRead },
        { nameof(ReportsController.GetAudit), "audit", AuthorizationCodes.ReportsAuditRead }
    };

    public static TheoryData<string, string, string> ExportActions => new()
    {
        { nameof(ReportsController.ExportCustomers), "customers/export", AuthorizationCodes.ReportsAdministrationRead },
        { nameof(ReportsController.ExportEmployees), "employees/export", AuthorizationCodes.ReportsAdministrationRead },
        { nameof(ReportsController.ExportOrders), "orders/export", AuthorizationCodes.ReportsAdministrationRead },
        { nameof(ReportsController.ExportSubscriptions), "subscriptions/export", AuthorizationCodes.ReportsAdministrationRead },
        { nameof(ReportsController.ExportPayments), "payments/export", AuthorizationCodes.ReportsFinancialRead },
        { nameof(ReportsController.ExportWallets), "wallets/export", AuthorizationCodes.ReportsFinancialRead },
        { nameof(ReportsController.ExportDeliveries), "deliveries/export", AuthorizationCodes.ReportsOperationsRead },
        { nameof(ReportsController.ExportDairy), "dairy/export", AuthorizationCodes.ReportsOperationsRead },
        { nameof(ReportsController.ExportMilkTests), "milk-tests/export", AuthorizationCodes.ReportsMilkTestsRead },
        { nameof(ReportsController.ExportCameras), "cameras/export", AuthorizationCodes.ReportsOperationsRead },
        { nameof(ReportsController.ExportNotifications), "notifications/export", AuthorizationCodes.ReportsOperationsRead },
        { nameof(ReportsController.ExportAudit), "audit/export", AuthorizationCodes.ReportsAuditRead }
    };

    [Fact]
    public void Controller_UsesVersionedAdministrationRouteAndRequiresAuthentication()
    {
        var route = Assert.Single(typeof(ReportsController).GetCustomAttributes<RouteAttribute>());
        var authorize = Assert.Single(typeof(ReportsController).GetCustomAttributes<AuthorizeAttribute>());

        Assert.Equal("api/v1/admin/reports", route.Template);
        Assert.Null(authorize.Policy);
    }

    [Theory]
    [MemberData(nameof(ReadActions))]
    public void ReadAction_UsesExpectedRouteAndPermission(
        string methodName,
        string routeTemplate,
        string permission)
    {
        var method = RequireMethod(methodName);
        var route = Assert.Single(method.GetCustomAttributes<HttpGetAttribute>(inherit: false));
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal(routeTemplate, route.Template);
        Assert.Equal($"permission:{permission}", authorize.Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Theory]
    [MemberData(nameof(ExportActions))]
    public void ExportAction_UsesStaticRouteAndRequiresExportAndModuleReadPermissions(
        string methodName,
        string routeTemplate,
        string readPermission)
    {
        var method = RequireMethod(methodName);
        var route = Assert.Single(method.GetCustomAttributes<HttpPostAttribute>(inherit: false));
        var policies = method.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .Select(attribute => attribute.Policy)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(routeTemplate, route.Template);
        Assert.Equal(2, policies.Count);
        Assert.Contains($"permission:{AuthorizationCodes.ReportsExport}", policies);
        Assert.Contains($"permission:{readPermission}", policies);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Fact]
    public void RequireReportActor_ParsesDistinctPositiveBranchesAndGlobalAccess()
    {
        var controller = CreateActorController(
            new Claim("user_id", "73"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "12"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "0"),
            new Claim(AuthorizationCodes.BranchClaim, "invalid"),
            new Claim(
                AuthorizationCodes.PermissionClaim,
                AuthorizationCodes.GlobalAccess));

        var actor = controller.GetActor();

        Assert.Equal(73, actor.UserId);
        Assert.Equal([11L, 12L], actor.BranchIds);
        Assert.True(actor.HasGlobalAccess);
    }

    [Fact]
    public void RequireReportActor_WithoutGlobalPermission_HasBranchOnlyAccess()
    {
        var controller = CreateActorController(
            new Claim("user_id", "73"),
            new Claim(AuthorizationCodes.BranchClaim, "11"));

        var actor = controller.GetActor();

        Assert.Equal([11L], actor.BranchIds);
        Assert.False(actor.HasGlobalAccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    public void RequireReportActor_WithMissingOrInvalidUserId_IsUnauthorized(string? userId)
    {
        var claims = userId is null ? [] : new[] { new Claim("user_id", userId) };
        var controller = CreateActorController(claims);

        Assert.Throws<UnauthorizedAppException>(() => controller.GetActor());
    }

    [Fact]
    public void RequireReportActor_ParsesInvariantIdentifiers()
    {
        var controller = CreateActorController(
            new Claim("user_id", 73L.ToString(CultureInfo.InvariantCulture)),
            new Claim(
                AuthorizationCodes.BranchClaim,
                11L.ToString(CultureInfo.InvariantCulture)));

        var actor = controller.GetActor();

        Assert.Equal(73, actor.UserId);
        Assert.Equal([11L], actor.BranchIds);
    }

    private static MethodInfo RequireMethod(string methodName) =>
        Assert.IsAssignableFrom<MethodInfo>(
            typeof(ReportsController).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public));

    private static TestReportController CreateActorController(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };

        return new TestReportController
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class TestReportController : ReportControllerBase
    {
        public ReportActor GetActor() => RequireReportActor();
    }
}
