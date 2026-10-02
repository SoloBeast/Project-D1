using System.Net;
using System.Reflection;
using System.Text;
using DoodhDirect.Application.Identity;
using DoodhDirect.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Guest Customer / Deferred Login authorization contract.
///
/// A guest carries no JWT bearer token, so every endpoint that is not explicitly
/// <c>[AllowAnonymous]</c> MUST return <see cref="HttpStatusCode.Unauthorized"/>
/// (401) through the fallback <c>RequireAuthenticatedUser</c> policy or an explicit
/// <c>[Authorize(Policy = "permission:...")]</c> / <c>[Authorize]</c> attribute.
///
/// These tests prove the backend authorization has NOT been weakened for guests:
/// protected customer/admin endpoints stay 401 for unauthenticated requests, while
/// the public surface (catalogue reads, auth entry points, payment webhook, employee
/// invitation token flow, health) remains reachable without authentication.
/// </summary>
public sealed class GuestAuthorizationTests : IClassFixture<FoundationApiFactory>
{
    private const string Id = "00000000-0000-0000-0000-000000000000";
    private readonly FoundationApiFactory _factory;

    public GuestAuthorizationTests(FoundationApiFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------------
    // HTTP-level: protected endpoints must reject an unauthenticated guest.
    // ---------------------------------------------------------------------

    public static IEnumerable<object[]> ProtectedEndpoints
    {
        get
        {
            foreach (var (method, path) in ProtectedRoutes)
            {
                yield return new object[] { method, path };
            }
        }
    }

    public static IEnumerable<object[]> PublicEndpoints
    {
        get
        {
            foreach (var (method, path) in PublicRoutes)
            {
                yield return new object[] { method, path };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task ProtectedEndpoint_RejectsGuestWithoutToken(string method, string path)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (!HttpMethod.Get.Equals(request.Method))
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(PublicEndpoints))]
    public async Task PublicEndpoint_DoesNotRequireAuthentication(string method, string path)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (!HttpMethod.Get.Equals(request.Method))
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, CancellationToken.None);

        // May be 200/400/404/422 depending on payload and seed state, but never 401.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------
    // Reflection-based: every protected action carries its expected permission
    // policy and is never AllowAnonymous; public surface is AllowAnonymous.
    // ---------------------------------------------------------------------

    public static TheoryData<Type, string, string> PermissionBoundActions => new()
    {
        // Orders (customer)
        { typeof(OrdersController), nameof(OrdersController.Preview), AuthorizationCodes.OrdersCreateOwn },
        { typeof(OrdersController), nameof(OrdersController.Create), AuthorizationCodes.OrdersCreateOwn },
        { typeof(OrdersController), nameof(OrdersController.GetMine), AuthorizationCodes.OrdersReadOwn },
        { typeof(OrdersController), nameof(OrdersController.Get), AuthorizationCodes.OrdersReadOwn },
        { typeof(OrdersController), nameof(OrdersController.Cancel), AuthorizationCodes.OrdersCancelOwn },
        // Customer profile / addresses
        { typeof(CustomerController), nameof(CustomerController.GetProfile), AuthorizationCodes.ProfileReadOwn },
        { typeof(CustomerController), nameof(CustomerController.UpdateProfile), AuthorizationCodes.ProfileUpdateOwn },
        { typeof(CustomerController), nameof(CustomerController.GetAddresses), AuthorizationCodes.ProfileReadOwn },
        { typeof(CustomerController), nameof(CustomerController.CreateAddress), AuthorizationCodes.ProfileUpdateOwn },
        { typeof(CustomerController), nameof(CustomerController.GetAddress), AuthorizationCodes.ProfileReadOwn },
        { typeof(CustomerController), nameof(CustomerController.UpdateAddress), AuthorizationCodes.ProfileUpdateOwn },
        { typeof(CustomerController), nameof(CustomerController.DeactivateAddress), AuthorizationCodes.ProfileUpdateOwn },
        { typeof(CustomerController), nameof(CustomerController.ReverseGeocode), AuthorizationCodes.ProfileUpdateOwn },
        // Subscriptions
        { typeof(SubscriptionsController), nameof(SubscriptionsController.Create), AuthorizationCodes.SubscriptionsCreateOwn },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.GetMine), AuthorizationCodes.SubscriptionsReadOwn },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.Get), AuthorizationCodes.SubscriptionsReadOwn },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.RetryPayment), AuthorizationCodes.SubscriptionsManageOwn },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.Update), AuthorizationCodes.SubscriptionsManageOwn },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.Pause), AuthorizationCodes.SubscriptionsManageOwn },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.Resume), AuthorizationCodes.SubscriptionsManageOwn },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.Cancel), AuthorizationCodes.SubscriptionsManageOwn },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.Skip), AuthorizationCodes.SubscriptionsManageOwn },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.CreateVacation), AuthorizationCodes.SubscriptionsManageOwn },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.GetCalendar), AuthorizationCodes.SubscriptionsReadOwn },
        // Payments
        { typeof(PaymentsController), nameof(PaymentsController.GetCapabilities), AuthorizationCodes.PaymentsCreateOwn },
        { typeof(PaymentsController), nameof(PaymentsController.Create), AuthorizationCodes.PaymentsCreateOwn },
        { typeof(PaymentsController), nameof(PaymentsController.Verify), AuthorizationCodes.PaymentsCreateOwn },
        { typeof(PaymentsController), nameof(PaymentsController.Cancel), AuthorizationCodes.PaymentsCreateOwn },
        { typeof(PaymentsController), nameof(PaymentsController.Get), AuthorizationCodes.PaymentsReadOwn },
        { typeof(PaymentsController), nameof(PaymentsController.Reconcile), AuthorizationCodes.PaymentsRefund },
        { typeof(PaymentsController), nameof(PaymentsController.Refund), AuthorizationCodes.PaymentsRefund },
        // Wallet
        { typeof(WalletController), nameof(WalletController.Get), AuthorizationCodes.WalletReadOwn },
        { typeof(WalletController), nameof(WalletController.GetTransactions), AuthorizationCodes.WalletReadOwn },
        { typeof(WalletAdministrationController), nameof(WalletAdministrationController.Adjust), AuthorizationCodes.WalletAdjust },
        // Deliveries (customer)
        { typeof(CustomerDeliveriesController), nameof(CustomerDeliveriesController.GetMine), AuthorizationCodes.DeliveriesReadOwn },
        { typeof(CustomerDeliveriesController), nameof(CustomerDeliveriesController.Get), AuthorizationCodes.DeliveriesReadOwn },
        // Deliveries (staff)
        { typeof(DeliveryStaffController), nameof(DeliveryStaffController.GetToday), AuthorizationCodes.DeliveriesOperateAssigned },
        { typeof(DeliveryStaffController), nameof(DeliveryStaffController.Get), AuthorizationCodes.DeliveriesOperateAssigned },
        { typeof(DeliveryStaffController), nameof(DeliveryStaffController.PickUp), AuthorizationCodes.DeliveriesOperateAssigned },
        { typeof(DeliveryStaffController), nameof(DeliveryStaffController.Start), AuthorizationCodes.DeliveriesOperateAssigned },
        { typeof(DeliveryStaffController), nameof(DeliveryStaffController.Arrive), AuthorizationCodes.DeliveriesOperateAssigned },
        { typeof(DeliveryStaffController), nameof(DeliveryStaffController.VerifyOtp), AuthorizationCodes.DeliveriesOperateAssigned },
        { typeof(DeliveryStaffController), nameof(DeliveryStaffController.Complete), AuthorizationCodes.DeliveriesOperateAssigned },
        { typeof(DeliveryStaffController), nameof(DeliveryStaffController.Fail), AuthorizationCodes.DeliveriesOperateAssigned },
        { typeof(DeliveryStaffController), nameof(DeliveryStaffController.RecordLocation), AuthorizationCodes.DeliveriesTrackAssigned },
        // Deliveries (management mutations)
        { typeof(DeliveryManagementController), nameof(DeliveryManagementController.Materialize), AuthorizationCodes.DeliveriesAssignBranch },
        { typeof(DeliveryManagementController), nameof(DeliveryManagementController.FetchSubscriptions), AuthorizationCodes.DeliveriesAssignBranch },
        { typeof(DeliveryManagementController), nameof(DeliveryManagementController.Assign), AuthorizationCodes.DeliveriesAssignBranch },
        { typeof(DeliveryManagementController), nameof(DeliveryManagementController.BulkAssign), AuthorizationCodes.DeliveriesAssignBranch },
        // Milk tests (customer + staff)
        { typeof(CustomerDeliveryMilkTestsController), nameof(CustomerDeliveryMilkTestsController.RequestTest), AuthorizationCodes.MilkTestsRequestOwn },
        { typeof(CustomerDeliveryMilkTestsController), nameof(CustomerDeliveryMilkTestsController.Get), AuthorizationCodes.MilkTestsReadOwn },
        { typeof(MilkTestsController), nameof(MilkTestsController.UploadImage), AuthorizationCodes.MilkTestsOperateAssigned },
        { typeof(MilkTestsController), nameof(MilkTestsController.DeleteImage), AuthorizationCodes.MilkTestsOperateAssigned },
        { typeof(MilkTestsController), nameof(MilkTestsController.ReplaceImage), AuthorizationCodes.MilkTestsOperateAssigned },
        { typeof(MilkTestsController), nameof(MilkTestsController.ReplaceImageAsCustomer), AuthorizationCodes.MilkTestsDecideOwn },
        { typeof(MilkTestsController), nameof(MilkTestsController.Complete), AuthorizationCodes.MilkTestsOperateAssigned },
        { typeof(MilkTestsController), nameof(MilkTestsController.Confirm), AuthorizationCodes.MilkTestsDecideOwn },
        { typeof(MilkTestsController), nameof(MilkTestsController.Reject), AuthorizationCodes.MilkTestsDecideOwn },
        // Catalogue administration
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.GetProducts), AuthorizationCodes.CatalogueRead },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.GetProduct), AuthorizationCodes.CatalogueRead },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.CreateProduct), AuthorizationCodes.CatalogueManage },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.UpdateProduct), AuthorizationCodes.CatalogueManage },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.ActivateProduct), AuthorizationCodes.CatalogueManage },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.DeactivateProduct), AuthorizationCodes.CatalogueManage },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.SetBranchAvailability), AuthorizationCodes.CatalogueManage },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.UpsertProductImage), AuthorizationCodes.CatalogueManage },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.RemoveProductImage), AuthorizationCodes.CatalogueManage },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.GetCategories), AuthorizationCodes.CatalogueRead },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.CreateCategory), AuthorizationCodes.CatalogueManage },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.UpdateCategory), AuthorizationCodes.CatalogueManage },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.ActivateCategory), AuthorizationCodes.CatalogueManage },
        { typeof(CatalogueAdministrationController), nameof(CatalogueAdministrationController.DeactivateCategory), AuthorizationCodes.CatalogueManage },
        // Branches
        { typeof(BranchController), nameof(BranchController.List), AuthorizationCodes.BranchesRead },
        { typeof(BranchController), nameof(BranchController.Get), AuthorizationCodes.BranchesRead },
        { typeof(BranchController), nameof(BranchController.Create), AuthorizationCodes.BranchesManage },
        { typeof(BranchController), nameof(BranchController.Update), AuthorizationCodes.BranchesManage },
        { typeof(BranchController), nameof(BranchController.Activate), AuthorizationCodes.BranchesManage },
        { typeof(BranchController), nameof(BranchController.Deactivate), AuthorizationCodes.BranchesManage },
        // Employees
        { typeof(EmployeeController), nameof(EmployeeController.List), AuthorizationCodes.EmployeesRead },
        { typeof(EmployeeController), nameof(EmployeeController.BranchOptions), AuthorizationCodes.EmployeesRead },
        { typeof(EmployeeController), nameof(EmployeeController.Get), AuthorizationCodes.EmployeesRead },
        { typeof(EmployeeController), nameof(EmployeeController.Create), AuthorizationCodes.EmployeesManage },
        { typeof(EmployeeController), nameof(EmployeeController.Update), AuthorizationCodes.EmployeesManage },
        { typeof(EmployeeController), nameof(EmployeeController.ResendInvitation), AuthorizationCodes.EmployeesManage },
        { typeof(EmployeeController), nameof(EmployeeController.CancelInvitation), AuthorizationCodes.EmployeesManage },
        // Notification template administration
        { typeof(NotificationTemplateAdministrationController), nameof(NotificationTemplateAdministrationController.Get), AuthorizationCodes.NotificationTemplatesRead },
        { typeof(NotificationTemplateAdministrationController), nameof(NotificationTemplateAdministrationController.Update), AuthorizationCodes.NotificationTemplatesManage },
        // Cameras (administration)
        { typeof(AdminCamerasController), nameof(AdminCamerasController.Get), AuthorizationCodes.CamerasRead },
        { typeof(AdminCamerasController), nameof(AdminCamerasController.Create), AuthorizationCodes.CamerasManage },
        { typeof(AdminCamerasController), nameof(AdminCamerasController.Update), AuthorizationCodes.CamerasManage },
        // Dairy (method-level strengthen in addition to class-level DairyRead)
        { typeof(DairyController), nameof(DairyController.RecordProduction), AuthorizationCodes.DairyManage },
    };

    public static TheoryData<Type, string> ClassLevelPolicies => new()
    {
        { typeof(OrderAdministrationController), AuthorizationCodes.OrdersRead },
        { typeof(DeliveryManagementController), AuthorizationCodes.DeliveriesReadBranch },
        { typeof(DeliveryStaffMilkTestsController), AuthorizationCodes.MilkTestsOperateAssigned },
        { typeof(PublicCamerasController), AuthorizationCodes.CamerasViewPublic },
        { typeof(DairyController), AuthorizationCodes.DairyRead },
    };

    public static TheoryData<Type> AuthenticatedOnlyControllers => new()
    {
        typeof(NotificationsController),
        typeof(NotificationDevicesController),
        typeof(NotificationPreferencesController),
    };

    public static TheoryData<Type, string> AnonymousActions => new()
    {
        { typeof(CatalogueController), nameof(CatalogueController.GetProducts) },
        { typeof(CatalogueController), nameof(CatalogueController.GetProduct) },
        { typeof(CatalogueController), nameof(CatalogueController.GetProductImage) },
        { typeof(CatalogueController), nameof(CatalogueController.GetCategories) },
        { typeof(AuthController), nameof(AuthController.Register) },
        { typeof(AuthController), nameof(AuthController.Login) },
        { typeof(AuthController), nameof(AuthController.SendOtp) },
        { typeof(AuthController), nameof(AuthController.VerifyOtp) },
        { typeof(AuthController), nameof(AuthController.Refresh) },
    };

    public static TheoryData<Type> AnonymousControllers => new()
    {
        typeof(RazorpayWebhooksController),
        typeof(EmployeeInvitationController),
    };

    [Theory]
    [MemberData(nameof(PermissionBoundActions))]
    public void ProtectedAction_RequiresExpectedPermission(
        Type controllerType, string methodName, string permission)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public));

        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        Assert.Equal($"permission:{permission}", authorize.Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Theory]
    [MemberData(nameof(ClassLevelPolicies))]
    public void Controller_WithClassLevelPolicy_RequiresExpectedPermission(Type controllerType, string permission)
    {
        var authorize = Assert.Single(controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Equal($"permission:{permission}", authorize.Policy);
        Assert.Empty(controllerType.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Theory]
    [MemberData(nameof(AuthenticatedOnlyControllers))]
    public void Controller_WithClassLevelAuthorize_RequiresAuthentication(Type controllerType)
    {
        var authorize = Assert.Single(controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Null(authorize.Policy);
        Assert.Null(authorize.Roles);
        Assert.Empty(controllerType.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Theory]
    [MemberData(nameof(AnonymousActions))]
    public void PublicAction_CarriesAllowAnonymous(Type controllerType, string methodName)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public));

        Assert.NotEmpty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Theory]
    [MemberData(nameof(AnonymousControllers))]
    public void Controller_WithClassLevelAllowAnonymous_IsPublic(Type controllerType)
    {
        Assert.NotEmpty(controllerType.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Theory]
    [InlineData(nameof(AuthController.Logout))]
    [InlineData(nameof(AuthController.Me))]
    public void AuthAction_IsProtectedByFallbackPolicy_NotAllowAnonymous(string methodName)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(AuthController).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public));

        // No explicit AllowAnonymous: the fallback RequireAuthenticatedUser policy
        // protects these (proven by the HTTP-level 401 assertions above).
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    // ---------------------------------------------------------------------
    // Route inventory
    // ---------------------------------------------------------------------

    private static readonly (string Method, string Path)[] ProtectedRoutes =
    [
        // Orders
        ("GET", "/api/v1/orders"),
        ("POST", "/api/v1/orders/checkout-preview"),
        ("POST", "/api/v1/orders"),
        ("GET", $"/api/v1/orders/{Id}"),
        ("POST", $"/api/v1/orders/{Id}/cancel"),
        // Customer profile / addresses
        ("GET", "/api/v1/customers/me"),
        ("PATCH", "/api/v1/customers/me"),
        ("GET", "/api/v1/customers/me/addresses"),
        ("POST", "/api/v1/customers/me/addresses"),
        ("GET", $"/api/v1/customers/me/addresses/{Id}"),
        ("PATCH", $"/api/v1/customers/me/addresses/{Id}"),
        ("DELETE", $"/api/v1/customers/me/addresses/{Id}"),
        ("GET", "/api/v1/customers/me/address-lookup/reverse?latitude=12.9716&longitude=77.5946"),
        // Subscriptions
        ("GET", "/api/v1/subscriptions"),
        ("POST", "/api/v1/subscriptions"),
        ("GET", $"/api/v1/subscriptions/{Id}"),
        ("POST", $"/api/v1/subscriptions/{Id}/retry-payment"),
        ("PATCH", $"/api/v1/subscriptions/{Id}"),
        ("POST", $"/api/v1/subscriptions/{Id}/pause"),
        ("POST", $"/api/v1/subscriptions/{Id}/resume"),
        ("POST", $"/api/v1/subscriptions/{Id}/cancel"),
        ("POST", $"/api/v1/subscriptions/{Id}/skip"),
        ("GET", $"/api/v1/subscriptions/{Id}/calendar"),
        // Payments
        ("GET", "/api/v1/payments/capabilities"),
        ("POST", "/api/v1/payments/create"),
        ("POST", "/api/v1/payments/verify"),
        ("POST", $"/api/v1/payments/{Id}/cancel"),
        ("GET", $"/api/v1/payments/{Id}"),
        ("POST", $"/api/v1/payments/{Id}/reconcile"),
        ("POST", $"/api/v1/payments/{Id}/refund"),
        // Wallet
        ("GET", "/api/v1/wallet"),
        ("GET", "/api/v1/wallet/transactions"),
        ("POST", $"/api/v1/admin/customers/{Id}/wallet/adjust"),
        // Deliveries (customer)
        ("GET", "/api/v1/deliveries"),
        ("GET", $"/api/v1/deliveries/{Id}"),
        // Milk tests (customer)
        ("POST", $"/api/v1/deliveries/{Id}/milk-test"),
        ("GET", $"/api/v1/deliveries/{Id}/milk-test"),
        // Notifications / devices / preferences
        ("GET", "/api/v1/notifications"),
        ("GET", "/api/v1/notifications/unread-count"),
        ("POST", $"/api/v1/notifications/{Id}/read"),
        ("POST", "/api/v1/devices"),
        ("GET", "/api/v1/notification-preferences"),
        ("PATCH", "/api/v1/notification-preferences"),
        // Cameras (public-named but still authenticated)
        ("GET", "/api/v1/cameras/public"),
        ("GET", $"/api/v1/cameras/public/{Id}/stream"),
        // Auth (fallback policy protected)
        ("GET", "/api/v1/auth/me"),
        ("POST", "/api/v1/auth/logout"),
        // Client configuration (fallback policy protected; only signed-in users may load maps)
        ("GET", "/api/v1/client-config"),
        // Deliveries (staff)
        ("GET", "/api/v1/delivery/my-today"),
        ("GET", $"/api/v1/delivery/{Id}"),
        ("POST", $"/api/v1/delivery/{Id}/pickup"),
        ("POST", $"/api/v1/delivery/{Id}/start"),
        ("POST", $"/api/v1/delivery/{Id}/arrive"),
        ("POST", $"/api/v1/delivery/{Id}/verify-otp"),
        ("POST", $"/api/v1/delivery/{Id}/complete"),
        ("POST", $"/api/v1/delivery/{Id}/fail"),
        ("POST", $"/api/v1/delivery/{Id}/location"),
        // Deliveries (management)
        ("POST", "/api/v1/delivery-management/materialize?throughDate=2026-08-31"),
        ("POST", "/api/v1/delivery-management/fetch-subscriptions?throughDate=2026-08-31"),
        ("GET", "/api/v1/delivery-management/branches/1"),
        ("GET", "/api/v1/delivery-management/branches/1/employees"),
        ("GET", $"/api/v1/delivery-management/{Id}"),
        ("POST", $"/api/v1/delivery-management/{Id}/assign"),
        ("POST", "/api/v1/delivery-management/bulk-assign"),
        // Milk tests (staff + shared media)
        ("GET", $"/api/v1/delivery/{Id}/milk-test"),
        ("GET", $"/api/v1/milk-tests/{Id}/images/{Id}/content"),
        // Catalogue administration
        ("GET", "/api/v1/admin/products"),
        ("GET", $"/api/v1/admin/products/{Id}"),
        ("POST", "/api/v1/admin/products"),
        ("PATCH", $"/api/v1/admin/products/{Id}"),
        ("PUT", $"/api/v1/admin/products/{Id}/image"),
        ("DELETE", $"/api/v1/admin/products/{Id}/image"),
        ("GET", "/api/v1/admin/product-categories"),
        ("POST", "/api/v1/admin/product-categories"),
        // Branches administration
        ("GET", "/api/v1/admin/branches"),
        ("GET", $"/api/v1/admin/branches/{Id}"),
        ("POST", "/api/v1/admin/branches"),
        // Employees administration
        ("GET", "/api/v1/admin/employees"),
        ("GET", "/api/v1/admin/employees/branches"),
        ("GET", "/api/v1/admin/employees/1"),
        ("POST", "/api/v1/admin/employees"),
        // Orders administration
        ("GET", "/api/v1/admin/orders"),
        ("GET", $"/api/v1/admin/orders/{Id}"),
        // Notification template administration
        ("GET", "/api/v1/admin/notification-templates"),
        ("PATCH", $"/api/v1/admin/notification-templates/{Id}"),
        // Cameras administration
        ("GET", "/api/v1/admin/cameras"),
        ("POST", "/api/v1/admin/cameras"),
        // Dairy operations
        ("GET", "/api/v1/dairy/branches/1/dashboard"),
        ("POST", "/api/v1/dairy/branches/1/production"),
        ("GET", "/api/v1/dairy/branches/1/batches"),
        ("POST", $"/api/v1/dairy/batches/{Id}/usage"),
    ];

    private static readonly (string Method, string Path)[] PublicRoutes =
    [
        // Catalogue public reads
        ("GET", "/api/v1/products"),
        ("GET", $"/api/v1/products/{Id}"),
        ("GET", $"/api/v1/products/{Id}/image"),
        ("GET", "/api/v1/product-categories"),
        // Auth entry points
        ("POST", "/api/v1/auth/register"),
        ("POST", "/api/v1/auth/login"),
        ("POST", "/api/v1/auth/send-otp"),
        ("POST", "/api/v1/auth/verify-otp"),
        ("POST", "/api/v1/auth/refresh"),
        // Payment webhook (signature is the credential)
        ("POST", "/api/v1/webhooks/razorpay"),
        // Employee invitation token flow (token is the credential)
        ("GET", "/api/v1/employee-invitations/sample-token/verify"),
        ("POST", "/api/v1/employee-invitations/complete"),
        // Health
        ("GET", "/health/live"),
    ];
}
