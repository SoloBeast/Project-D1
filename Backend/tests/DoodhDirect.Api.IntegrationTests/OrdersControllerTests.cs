using System.Reflection;
using System.Security.Claims;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class OrdersControllerTests
{
    [Theory]
    [InlineData(typeof(OrdersController), "api/v1/orders")]
    [InlineData(typeof(StaffOrdersController), "api/v1/staff/orders")]
    [InlineData(typeof(OrderAdministrationController), "api/v1/admin/orders")]
    public void Controller_UsesExpectedRoute(Type controllerType, string expectedRoute)
    {
        var route = Assert.Single(controllerType.GetCustomAttributes<RouteAttribute>());

        Assert.Equal(expectedRoute, route.Template);
    }

    [Theory]
    [InlineData(nameof(OrdersController.GetMine), AuthorizationCodes.OrdersReadOwn)]
    [InlineData(nameof(OrdersController.Get), AuthorizationCodes.OrdersReadOwn)]
    [InlineData(nameof(OrdersController.Cancel), AuthorizationCodes.OrdersCancelOwn)]
    [InlineData(nameof(OrdersController.Preview), AuthorizationCodes.OrdersCreateOwn)]
    public void CustomerAction_RequiresExpectedPermission(string methodName, string permission)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(OrdersController).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public));
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal($"permission:{permission}", authorize.Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Fact]
    public void StaffController_RequiresBranchReadPermission()
    {
        var authorize = Assert.Single(
            typeof(StaffOrdersController).GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal($"permission:{AuthorizationCodes.OrdersReadBranch}", authorize.Policy);
    }

    [Fact]
    public void StaffController_DoesNotGrantGlobalOrOwnershipReadPermission()
    {
        var authorize = Assert.Single(
            typeof(StaffOrdersController).GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        var permission = Assert.IsType<string>(authorize.Policy).Split(':', 2)[1];

        Assert.Equal(AuthorizationCodes.OrdersReadBranch, permission);
        Assert.NotEqual(AuthorizationCodes.OrdersRead, permission);
        Assert.NotEqual(AuthorizationCodes.OrdersReadOwn, permission);
        Assert.NotEqual(AuthorizationCodes.OrdersCancelOwn, permission);
        Assert.DoesNotContain(AuthorizationCodes.OrdersCreateOwn, permission, StringComparison.Ordinal);
    }

    [Fact]
    public void StaffController_ReadOnly_ExposesOnlyGetRoutes()
    {
        var httpMethods = typeof(StaffOrdersController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: false))
            .SelectMany(attribute => attribute.HttpMethods)
            .Distinct()
            .ToArray();

        Assert.Equal(["GET"], httpMethods);

        var templates = typeof(StaffOrdersController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: false))
            .Select(attribute => attribute.Template)
            .OfType<string>()
            .ToArray();

        Assert.DoesNotContain(
            templates,
            template => template.Contains("cancel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StaffController_ReadsViaBranchScopedLookupWithParsedActor()
    {
        var service = new RecordingOrderService();
        var controller = CreateController(
            service,
            new Claim("user_id", "73"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "12"));
        var orderId = Guid.NewGuid();

        await controller.GetOrder(orderId, CancellationToken.None);

        var actor = Assert.IsType<OrderActor>(service.LastActor);
        Assert.Equal(orderId, service.LastOrderId);
        Assert.Equal(73, actor.UserId);
        Assert.Equal([11L, 12L], actor.BranchIds.OrderBy(branchId => branchId));
        Assert.False(actor.HasGlobalAccess);
    }

    [Fact]
    public async Task RequireActor_ParsesDistinctBranchesAndGlobalAccess()
    {
        var service = new RecordingOrderService();
        var controller = CreateController(
            service,
            new Claim("user_id", "73"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "12"),
            new Claim(AuthorizationCodes.BranchClaim, "11"),
            new Claim(AuthorizationCodes.BranchClaim, "invalid"),
            new Claim(AuthorizationCodes.PermissionClaim, AuthorizationCodes.GlobalAccess));

        await controller.GetOrder(Guid.NewGuid(), CancellationToken.None);

        var actor = Assert.IsType<OrderActor>(service.LastActor);
        Assert.Equal(73, actor.UserId);
        Assert.True(actor.HasGlobalAccess);
        Assert.Equal([11L, 12L], actor.BranchIds.OrderBy(branchId => branchId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public async Task RequireActor_WithMissingOrInvalidUserId_IsUnauthorized(string? userId)
    {
        var service = new RecordingOrderService();
        var claims = userId is null ? [] : new[] { new Claim("user_id", userId) };
        var controller = CreateController(service, claims);

        await Assert.ThrowsAsync<UnauthorizedAppException>(
            () => controller.GetOrder(Guid.NewGuid(), CancellationToken.None));

        Assert.Null(service.LastActor);
    }

    private static StaffOrdersController CreateController(
        RecordingOrderService service,
        params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };

        return new StaffOrdersController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class RecordingOrderService : IOrderService
    {
        public OrderActor? LastActor { get; private set; }
        public Guid? LastOrderId { get; private set; }

        public Task<OrderResult> GetForBranchAsync(
            OrderActor actor,
            Guid orderId,
            CancellationToken cancellationToken)
        {
            LastActor = actor;
            LastOrderId = orderId;
            return Task.FromResult<OrderResult>(null!);
        }

        public Task<CheckoutResult> PreviewAsync(
            long customerId,
            CheckoutRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrderResult> CreateAsync(
            long customerId,
            CheckoutRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<OrderResult>> GetForCustomerAsync(
            long customerId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<OrderResult>> GetForAdministrationAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrderResult> GetAsync(
            long customerId,
            Guid orderId,
            bool bypassOwnership,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrderResult> CancelAsync(
            long customerId,
            Guid orderId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
