using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class DeliveriesControllerTests
{
    [Theory]
    [InlineData(typeof(CustomerDeliveriesController), "api/v1/deliveries")]
    [InlineData(typeof(DeliveryStaffController), "api/v1/delivery")]
    [InlineData(typeof(DeliveryManagementController), "api/v1/delivery-management")]
    public void Controller_UsesExpectedRoute(Type controllerType, string expectedRoute)
    {
        var route = Assert.Single(controllerType.GetCustomAttributes<RouteAttribute>());

        Assert.Equal(expectedRoute, route.Template);
    }

    [Theory]
    [InlineData(typeof(CustomerDeliveriesController), nameof(CustomerDeliveriesController.GetMine), AuthorizationCodes.DeliveriesReadOwn)]
    [InlineData(typeof(CustomerDeliveriesController), nameof(CustomerDeliveriesController.Get), AuthorizationCodes.DeliveriesReadOwn)]
    [InlineData(typeof(DeliveryStaffController), nameof(DeliveryStaffController.GetToday), AuthorizationCodes.DeliveriesOperateAssigned)]
    [InlineData(typeof(DeliveryStaffController), nameof(DeliveryStaffController.Get), AuthorizationCodes.DeliveriesOperateAssigned)]
    [InlineData(typeof(DeliveryStaffController), nameof(DeliveryStaffController.PickUp), AuthorizationCodes.DeliveriesOperateAssigned)]
    [InlineData(typeof(DeliveryStaffController), nameof(DeliveryStaffController.Start), AuthorizationCodes.DeliveriesOperateAssigned)]
    [InlineData(typeof(DeliveryStaffController), nameof(DeliveryStaffController.Arrive), AuthorizationCodes.DeliveriesOperateAssigned)]
    [InlineData(typeof(DeliveryStaffController), nameof(DeliveryStaffController.IssueOtp), AuthorizationCodes.DeliveriesOperateAssigned)]
    [InlineData(typeof(DeliveryStaffController), nameof(DeliveryStaffController.VerifyOtp), AuthorizationCodes.DeliveriesOperateAssigned)]
    [InlineData(typeof(DeliveryStaffController), nameof(DeliveryStaffController.Complete), AuthorizationCodes.DeliveriesOperateAssigned)]
    [InlineData(typeof(DeliveryStaffController), nameof(DeliveryStaffController.Fail), AuthorizationCodes.DeliveriesOperateAssigned)]
    [InlineData(typeof(DeliveryStaffController), nameof(DeliveryStaffController.RecordLocation), AuthorizationCodes.DeliveriesTrackAssigned)]
    public void CustomerAndStaffAction_RequiresExpectedPermission(
        Type controllerType,
        string methodName,
        string permission)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public));
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal($"permission:{permission}", authorize.Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Fact]
    public void ManagementController_RequiresBranchReadPermission()
    {
        var authorize = Assert.Single(
            typeof(DeliveryManagementController).GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal($"permission:{AuthorizationCodes.DeliveriesReadBranch}", authorize.Policy);
    }

    [Theory]
    [InlineData(nameof(DeliveryManagementController.Materialize))]
    [InlineData(nameof(DeliveryManagementController.Assign))]
    public void ManagementMutation_AlsoRequiresBranchAssignmentPermission(string methodName)
    {
        var method = typeof(DeliveryManagementController).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public);
        var authorize = Assert.Single(method!.GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal($"permission:{AuthorizationCodes.DeliveriesAssignBranch}", authorize.Policy);
    }

    [Theory]
    [InlineData(nameof(DeliveryManagementController.GetBranch))]
    [InlineData(nameof(DeliveryManagementController.GetEmployees))]
    [InlineData(nameof(DeliveryManagementController.Get))]
    public void ManagementRead_DoesNotAddAssignmentPermission(string methodName)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(DeliveryManagementController).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public));

        Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Fact]
    public void RequireActor_ParsesDistinctBranchesAndGlobalAccess()
    {
        var controller = CreateActorController(
            new Claim("user_id", "73"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "12"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "invalid"),
            new Claim(AuthorizationCodes.PermissionClaim, AuthorizationCodes.GlobalAccess));

        var actor = controller.GetActor();

        Assert.Equal(73, actor.UserId);
        Assert.Equal([11L, 12L], actor.BranchIds);
        Assert.True(actor.HasGlobalAccess);
    }

    [Fact]
    public void RequireActor_WithoutGlobalPermission_HasBranchOnlyAccess()
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
    [InlineData("-1")]
    public void RequireActor_WithMissingOrInvalidUserId_IsUnauthorized(string? userId)
    {
        var claims = userId is null ? [] : new[] { new Claim("user_id", userId) };
        var controller = CreateActorController(claims);

        Assert.Throws<UnauthorizedAppException>(() => controller.GetActor());
    }

    [Fact]
    public void RequireActor_ParsesInvariantUserAndBranchIdentifiers()
    {
        var controller = CreateActorController(
            new Claim("user_id", 73L.ToString(CultureInfo.InvariantCulture)),
            new Claim(AuthorizationCodes.BranchClaim, 11L.ToString(CultureInfo.InvariantCulture)));

        var actor = controller.GetActor();

        Assert.Equal(73, actor.UserId);
        Assert.Equal([11L], actor.BranchIds);
    }

    private static TestDeliveryController CreateActorController(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };

        return new TestDeliveryController
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class TestDeliveryController : DeliveryControllerBase
    {
        public DeliveryActor GetActor() => RequireActor();
    }
}
