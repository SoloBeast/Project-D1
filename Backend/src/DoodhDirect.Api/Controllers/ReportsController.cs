using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

public abstract class ReportControllerBase : ControllerBase
{
    protected ReportActor RequireReportActor()
    {
        var userIdValue = User.FindFirstValue("user_id");
        if (!long.TryParse(
            userIdValue,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var userId) || userId <= 0)
        {
            throw new UnauthorizedAppException();
        }

        var branchIds = User.FindAll(AuthorizationCodes.BranchClaim)
            .Select(claim => long.TryParse(
                claim.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var branchId) && branchId > 0
                ? branchId
                : (long?)null)
            .Where(branchId => branchId.HasValue)
            .Select(branchId => branchId!.Value)
            .Distinct()
            .ToArray();

        var hasGlobalAccess = User.HasClaim(
            AuthorizationCodes.PermissionClaim,
            AuthorizationCodes.GlobalAccess);

        return new ReportActor(userId, branchIds, hasGlobalAccess);
    }
}

[ApiController]
[Route("api/v1/admin/reports")]
[Tags("Administration Reports")]
[Produces("application/json")]
[Authorize]
public sealed class ReportsController(IReportService reportService) : ReportControllerBase
{
    [HttpGet("dashboard")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsDashboardRead)]
    public async Task<ActionResult<ApiResponse<DashboardMetrics>>> GetDashboard(
        [FromQuery] DashboardRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DashboardMetrics>.Ok(
            await reportService.GetDashboardAsync(
                RequireReportActor(),
                request,
                cancellationToken)));

    [HttpGet("customers")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsAdministrationRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<CustomerReportRow>>>> GetCustomers(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<CustomerReportRow>>.Ok(
            await reportService.GetCustomersAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("employees")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsAdministrationRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<EmployeeReportRow>>>> GetEmployees(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<EmployeeReportRow>>.Ok(
            await reportService.GetEmployeesAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("orders")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsAdministrationRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<OrderReportRow>>>> GetOrders(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<OrderReportRow>>.Ok(
            await reportService.GetOrdersAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("subscriptions")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsAdministrationRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<SubscriptionReportRow>>>> GetSubscriptions(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<SubscriptionReportRow>>.Ok(
            await reportService.GetSubscriptionsAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("payments")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsFinancialRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<PaymentReportRow>>>> GetPayments(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<PaymentReportRow>>.Ok(
            await reportService.GetPaymentsAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("wallets")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsFinancialRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<WalletReportRow>>>> GetWallets(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<WalletReportRow>>.Ok(
            await reportService.GetWalletsAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("deliveries")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsOperationsRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<DeliveryReportRow>>>> GetDeliveries(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<DeliveryReportRow>>.Ok(
            await reportService.GetDeliveriesAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("dairy")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsOperationsRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<DairyReportRow>>>> GetDairy(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<DairyReportRow>>.Ok(
            await reportService.GetDairyAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("milk-tests")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsMilkTestsRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<MilkTestReportRow>>>> GetMilkTests(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<MilkTestReportRow>>.Ok(
            await reportService.GetMilkTestsAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("cameras")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsOperationsRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<CameraReportRow>>>> GetCameras(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<CameraReportRow>>.Ok(
            await reportService.GetCamerasAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("notifications")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsOperationsRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<NotificationReportRow>>>> GetNotifications(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<NotificationReportRow>>.Ok(
            await reportService.GetNotificationsAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpGet("audit")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsAuditRead)]
    public async Task<ActionResult<ApiResponse<ReportPage<AuditReportRow>>>> GetAudit(
        [FromQuery] ReportFilter filter,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReportPage<AuditReportRow>>.Ok(
            await reportService.GetAuditAsync(RequireReportActor(), filter, cancellationToken)));

    [HttpPost("customers/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsAdministrationRead)]
    public Task<FileContentResult> ExportCustomers([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Customers, request, cancellationToken);

    [HttpPost("employees/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsAdministrationRead)]
    public Task<FileContentResult> ExportEmployees([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Employees, request, cancellationToken);

    [HttpPost("orders/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsAdministrationRead)]
    public Task<FileContentResult> ExportOrders([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Orders, request, cancellationToken);

    [HttpPost("subscriptions/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsAdministrationRead)]
    public Task<FileContentResult> ExportSubscriptions([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Subscriptions, request, cancellationToken);

    [HttpPost("payments/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsFinancialRead)]
    public Task<FileContentResult> ExportPayments([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Payments, request, cancellationToken);

    [HttpPost("wallets/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsFinancialRead)]
    public Task<FileContentResult> ExportWallets([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Wallets, request, cancellationToken);

    [HttpPost("deliveries/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsOperationsRead)]
    public Task<FileContentResult> ExportDeliveries([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Deliveries, request, cancellationToken);

    [HttpPost("dairy/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsOperationsRead)]
    public Task<FileContentResult> ExportDairy([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Dairy, request, cancellationToken);

    [HttpPost("milk-tests/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsMilkTestsRead)]
    public Task<FileContentResult> ExportMilkTests([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.MilkTests, request, cancellationToken);

    [HttpPost("cameras/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsOperationsRead)]
    public Task<FileContentResult> ExportCameras([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Cameras, request, cancellationToken);

    [HttpPost("notifications/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsOperationsRead)]
    public Task<FileContentResult> ExportNotifications([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Notifications, request, cancellationToken);

    [HttpPost("audit/export")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsExport)]
    [Authorize(Policy = "permission:" + AuthorizationCodes.ReportsAuditRead)]
    public Task<FileContentResult> ExportAudit([FromBody] ReportExportApiRequest request, CancellationToken cancellationToken) =>
        Export(ReportModule.Audit, request, cancellationToken);

    private async Task<FileContentResult> Export(
        ReportModule module,
        ReportExportApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await reportService.ExportAsync(
            RequireReportActor(),
            new ExportRequest(module, request.Filter, request.Format),
            cancellationToken);

        Response.Headers.Append("X-Report-Row-Count", result.RowCount.ToString(CultureInfo.InvariantCulture));
        return File(result.Content, result.ContentType, result.FileName);
    }
}

public sealed record ReportExportApiRequest(
    ReportFilter Filter,
    ReportExportFormat Format);
