using DoodhDirect.Domain.Cameras;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;

namespace DoodhDirect.Application.Reports;

public enum ReportModule
{
    Customers,
    Employees,
    Orders,
    Subscriptions,
    Payments,
    Wallets,
    Deliveries,
    Dairy,
    MilkTests,
    Cameras,
    Notifications,
    Audit
}

public enum ReportSortDirection
{
    Ascending,
    Descending
}

public enum ReportExportFormat
{
    Csv,
    Xlsx
}

public sealed record ReportDateRange(DateTime? From, DateTime? To);

public sealed record ReportFilter(
    ReportDateRange? DateRange = null,
    IReadOnlyCollection<Guid>? BranchIds = null,
    string? Search = null,
    IReadOnlyCollection<string>? Statuses = null,
    Guid? CustomerId = null,
    Guid? EmployeeId = null,
    Guid? ProductId = null,
    string? PaymentState = null,
    int Page = 1,
    int PageSize = 50,
    string? SortBy = null,
    ReportSortDirection SortDirection = ReportSortDirection.Descending);

public sealed record ReportPage<T>(
    IReadOnlyCollection<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasNextPage);

public sealed record ReportActor(
    long UserId,
    IReadOnlyCollection<long> BranchIds,
    bool HasGlobalAccess);

public sealed record DashboardRequest(
    ReportDateRange? DateRange = null,
    IReadOnlyCollection<Guid>? BranchIds = null);

public sealed record DashboardMetrics(
    int Customers,
    int ActiveCustomers,
    int Employees,
    int Orders,
    decimal OneTimeOrderRevenue,
    int ActiveSubscriptions,
    decimal SuccessfulPayments,
    decimal PendingPayments,
    decimal Refunds,
    decimal WalletBalances,
    int Deliveries,
    int SuccessfulDeliveries,
    int FailedDeliveries,
    decimal MilkProduced,
    decimal MilkUsed,
    int PendingMilkTests,
    int AvailableCameras,
    int NotificationFailures);

public sealed record CustomerReportRow(
    Guid Id,
    string? DisplayName,
    string? Mobile,
    string? Email,
    bool IsActive,
    DateTime CreatedAt,
    int OrderCount,
    decimal LifetimeOrderValue,
    decimal WalletBalance);

public sealed record EmployeeReportRow(
    Guid Id,
    string? DisplayName,
    string? Mobile,
    string? Email,
    bool IsActive,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<Guid> BranchIds,
    int AssignedDeliveries,
    int CompletedDeliveries);

public sealed record OrderReportRow(
    Guid Id,
    string OrderNumber,
    OrderType Type,
    OrderStatus Status,
    DateTime CreatedAt,
    Guid CustomerId,
    string? CustomerName,
    Guid BranchId,
    string BranchCode,
    string BranchName,
    decimal PayableAmount);

public sealed record SubscriptionReportRow(
    Guid Id,
    SubscriptionStatus Status,
    Guid CustomerId,
    string? CustomerName,
    Guid ProductId,
    string ProductName,
    Guid BranchId,
    string BranchName,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal PayableAmount,
    int TotalEntitlement,
    int UsedEntitlement,
    int RemainingEntitlement);

public sealed record PaymentReportRow(
    Guid Id,
    Guid CustomerId,
    PaymentMethod Method,
    PaymentStatus Status,
    decimal Amount,
    decimal RefundedAmount,
    string Currency,
    DateTime CreatedAt,
    DateTime? VerifiedAt,
    DateTime? FailedAt);

public sealed record WalletReportRow(
    Guid CustomerId,
    string? CustomerName,
    decimal Balance,
    string Currency,
    DateTime? LastActivityAt,
    int TransactionCount);

public sealed record DeliveryReportRow(
    Guid Id,
    Guid BranchId,
    string BranchName,
    Guid CustomerId,
    string? CustomerName,
    Guid? AssignedEmployeeId,
    string? AssignedEmployeeName,
    DateOnly ScheduledDate,
    DeliveryStatus Status,
    DateTime? CompletedAt,
    string? FailureCode);

public sealed record DairyReportRow(
    ReportModule Source,
    Guid BranchId,
    string BranchName,
    DateTime OccurredAt,
    decimal Quantity,
    string Unit,
    string? Status,
    string? Purpose);

public sealed record MilkTestReportRow(
    Guid Id,
    Guid BranchId,
    string BranchName,
    Guid CustomerId,
    DateTime RequestedAt,
    MilkTestStatus Status,
    MilkTestCustomerDecision CustomerDecision,
    int ParameterCount,
    int ImageCount,
    DateTime? CompletedAt);

public sealed record CameraReportRow(
    Guid Id,
    Guid BranchId,
    string BranchName,
    string DisplayName,
    bool IsPublic,
    bool IsActive,
    int DisplayOrder,
    CameraStreamProtocol? StreamProtocol);

public sealed record NotificationReportRow(
    Guid Id,
    string EventType,
    NotificationEventStatus EventStatus,
    DateTime OccurredAt,
    bool IsCritical,
    int DeliveryCount,
    int FailedDeliveryCount,
    int AttemptCount);

public sealed record AuditReportRow(
    long Id,
    long? UserId,
    string Action,
    string EntityType,
    string EntityId,
    string? Reason,
    DateTime CreatedAt);

public sealed record ExportRequest(
    ReportModule Module,
    ReportFilter Filter,
    ReportExportFormat Format);

public sealed record ExportResult(
    string FileName,
    string ContentType,
    byte[] Content,
    int RowCount);

public interface IReportService
{
    Task<DashboardMetrics> GetDashboardAsync(
        ReportActor actor,
        DashboardRequest request,
        CancellationToken cancellationToken);

    Task<ReportPage<CustomerReportRow>> GetCustomersAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<EmployeeReportRow>> GetEmployeesAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<OrderReportRow>> GetOrdersAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<SubscriptionReportRow>> GetSubscriptionsAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<PaymentReportRow>> GetPaymentsAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<WalletReportRow>> GetWalletsAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<DeliveryReportRow>> GetDeliveriesAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<DairyReportRow>> GetDairyAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<MilkTestReportRow>> GetMilkTestsAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<CameraReportRow>> GetCamerasAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<NotificationReportRow>> GetNotificationsAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);
    Task<ReportPage<AuditReportRow>> GetAuditAsync(ReportActor actor, ReportFilter filter, CancellationToken cancellationToken);

    Task<ExportResult> ExportAsync(ReportActor actor, ExportRequest request, CancellationToken cancellationToken);
}

public static class ReportPermissions
{
    public const string DashboardRead = "REPORTS.DASHBOARD.READ";
    public const string AdministrationRead = "REPORTS.ADMINISTRATION.READ";
    public const string FinancialRead = "REPORTS.FINANCIAL.READ";
    public const string OperationsRead = "REPORTS.OPERATIONS.READ";
    public const string MilkTestsRead = "REPORTS.MILK_TESTS.READ";
    public const string AuditRead = "REPORTS.AUDIT.READ";
    public const string Export = "REPORTS.EXPORT";
}
