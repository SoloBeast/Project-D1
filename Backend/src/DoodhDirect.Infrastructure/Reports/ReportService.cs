using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Reports;
using DoodhDirect.Domain.Cameras;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Reports;

public sealed class ReportService(DoodhDirectDbContext db, IIndiaTimeProvider timeProvider) : IReportService
{
    private const int MaximumPageSize = 250;
    private const int MaximumExportRows = 10_000;

    public async Task<DashboardMetrics> GetDashboardAsync(ReportActor actor, DashboardRequest request, CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        ValidateDateRange(request.DateRange);
        var branches = await ResolveBranchesAsync(actor, request.BranchIds, cancellationToken);
        var from = request.DateRange?.From;
        var to = request.DateRange?.To;

        var customers = db.Users.AsNoTracking().Where(x => x.UserType == UserType.Customer);
        var employees = db.Users.AsNoTracking().Where(x => x.UserType != UserType.Customer);
        var orders = ApplyBranches(db.Orders.AsNoTracking(), branches);
        var subscriptions = ApplyBranches(db.Subscriptions.AsNoTracking(), branches);
        var payments = ApplyPaymentBranches(db.Payments.AsNoTracking(), branches);
        var deliveries = ApplyBranches(db.Deliveries.AsNoTracking(), branches);
        var production = ApplyBranches(db.MilkProductions.AsNoTracking(), branches);
        var usage = ApplyBranches(db.MilkUsages.AsNoTracking(), branches);
        var tests = ApplyBranches(db.MilkTests.AsNoTracking(), branches);
        var cameras = ApplyBranches(db.Cameras.AsNoTracking(), branches);
        var events = ApplyNotificationScope(db.NotificationEvents.AsNoTracking(), actor, branches);
        var wallets = ApplyWalletBranches(db.Wallets.AsNoTracking(), branches);

        if (!actor.HasGlobalAccess)
        {
            var scopedCustomers = orders.Select(x => x.CustomerId)
                .Concat(subscriptions.Select(x => x.CustomerId))
                .Concat(deliveries.Select(x => x.CustomerId));
            customers = customers.Where(x => scopedCustomers.Contains(x.Id));
            employees = employees.Where(x => x.UserRoles.Any(r => r.BranchId.HasValue && branches.Contains(r.BranchId.Value)));
        }
        if (from.HasValue) { orders = orders.Where(x => x.CreatedAt >= from); subscriptions = subscriptions.Where(x => x.CreatedAt >= from); payments = payments.Where(x => x.CreatedAt >= from); deliveries = deliveries.Where(x => x.CreatedAt >= from); production = production.Where(x => x.ProductionAt >= from); usage = usage.Where(x => x.UsedAt >= from); tests = tests.Where(x => x.RequestedAt >= from); events = events.Where(x => x.OccurredAt >= from); }
        if (to.HasValue) { orders = orders.Where(x => x.CreatedAt < to); subscriptions = subscriptions.Where(x => x.CreatedAt < to); payments = payments.Where(x => x.CreatedAt < to); deliveries = deliveries.Where(x => x.CreatedAt < to); production = production.Where(x => x.ProductionAt < to); usage = usage.Where(x => x.UsedAt < to); tests = tests.Where(x => x.RequestedAt < to); events = events.Where(x => x.OccurredAt < to); }

        return new DashboardMetrics(
            await customers.CountAsync(cancellationToken),
            await customers.CountAsync(x => x.IsActive, cancellationToken),
            await employees.CountAsync(cancellationToken),
            await orders.CountAsync(cancellationToken),
            await orders.Where(x => x.Type == OrderType.OneTime && x.Status != OrderStatus.Cancelled).SumAsync(x => (decimal?)x.PayableAmount, cancellationToken) ?? 0,
            await subscriptions.CountAsync(x => x.Status == SubscriptionStatus.Active, cancellationToken),
            await payments.Where(x => x.Status == PaymentStatus.Success).SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0,
            await payments.Where(x => x.Status == PaymentStatus.Pending || x.Status == PaymentStatus.Initiated).SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0,
            await payments.SumAsync(x => (decimal?)x.RefundedAmount, cancellationToken) ?? 0,
            await wallets.SumAsync(x => (decimal?)x.Balance, cancellationToken) ?? 0,
            await deliveries.CountAsync(cancellationToken),
            await deliveries.CountAsync(x => x.Status == DeliveryStatus.Delivered, cancellationToken),
            await deliveries.CountAsync(x => x.Status == DeliveryStatus.Failed, cancellationToken),
            await production.SumAsync(x => (decimal?)x.QuantityProduced, cancellationToken) ?? 0,
            await usage.SumAsync(x => (decimal?)x.QuantityUsed, cancellationToken) ?? 0,
            await tests.CountAsync(x => x.Status == MilkTestStatus.Requested, cancellationToken),
            await cameras.CountAsync(x => x.IsActive && x.Stream != null, cancellationToken),
            await events.CountAsync(x => x.Status == NotificationEventStatus.Failed, cancellationToken));
    }

    public Task<ReportPage<CustomerReportRow>> GetCustomersAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageCustomers(a, Normalize(f), c);
    public Task<ReportPage<EmployeeReportRow>> GetEmployeesAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageEmployees(a, Normalize(f), c);
    public Task<ReportPage<OrderReportRow>> GetOrdersAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageOrders(a, Normalize(f), c);
    public Task<ReportPage<SubscriptionReportRow>> GetSubscriptionsAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageSubscriptions(a, Normalize(f), c);
    public Task<ReportPage<PaymentReportRow>> GetPaymentsAsync(ReportActor a, ReportFilter f, CancellationToken c) => PagePayments(a, Normalize(f), c);
    public Task<ReportPage<WalletReportRow>> GetWalletsAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageWallets(a, Normalize(f), c);
    public Task<ReportPage<DeliveryReportRow>> GetDeliveriesAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageDeliveries(a, Normalize(f), c);
    public Task<ReportPage<DairyReportRow>> GetDairyAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageDairy(a, Normalize(f), c);
    public Task<ReportPage<MilkTestReportRow>> GetMilkTestsAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageMilkTests(a, Normalize(f), c);
    public Task<ReportPage<CameraReportRow>> GetCamerasAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageCameras(a, Normalize(f), c);
    public Task<ReportPage<NotificationReportRow>> GetNotificationsAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageNotifications(a, Normalize(f), c);
    public Task<ReportPage<AuditReportRow>> GetAuditAsync(ReportActor a, ReportFilter f, CancellationToken c) => PageAudit(a, Normalize(f), c);

    public async Task<ExportResult> ExportAsync(ReportActor actor, ExportRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Module))
        {
            throw new ValidationAppException("The report module is invalid.", nameof(request.Module));
        }

        if (!Enum.IsDefined(request.Format))
        {
            throw new ValidationAppException("The report export format is invalid.", nameof(request.Format));
        }

        var filter = Normalize(request.Filter, MaximumExportRows) with { Page = 1, PageSize = MaximumExportRows };
        var rows = request.Module switch
        {
            ReportModule.Customers => (await PageCustomers(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.Employees => (await PageEmployees(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.Orders => (await PageOrders(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.Subscriptions => (await PageSubscriptions(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.Payments => (await PagePayments(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.Wallets => (await PageWallets(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.Deliveries => (await PageDeliveries(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.Dairy => (await PageDairy(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.MilkTests => (await PageMilkTests(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.Cameras => (await PageCameras(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.Notifications => (await PageNotifications(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            ReportModule.Audit => (await PageAudit(actor, filter, cancellationToken)).Items.Cast<object>().ToArray(),
            _ => throw new ValidationAppException("The report module is invalid.", nameof(request.Module))
        };
        var (extension, contentType, content) = request.Format switch
        {
            ReportExportFormat.Csv => (
                "csv",
                "text/csv; charset=utf-8",
                ReportTabularExporter.Csv(request.Module, rows)),
            ReportExportFormat.Xlsx => (
                "xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ReportTabularExporter.Xlsx(request.Module, rows)),
            _ => throw new ValidationAppException(
                "The report export format is invalid.",
                nameof(request.Format))
        };
        return new ExportResult(
            $"{request.Module.ToString().ToLowerInvariant()}-{timeProvider.Now:yyyyMMddHHmmss}.{extension}",
            contentType,
            content,
            rows.Length);
    }

    private async Task<ReportPage<CustomerReportRow>> PageCustomers(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var q = db.Users.AsNoTracking().Where(x => x.UserType == UserType.Customer);
        if (!a.HasGlobalAccess) { var ids = await ScopedCustomerIds(a, f, ct); q = q.Where(x => ids.Contains(x.Id)); }
        if (f.CustomerId.HasValue) q = q.Where(x => x.PublicId == f.CustomerId);
        if (f.DateRange?.From is { } from) q = q.Where(x => x.CreatedAt >= from);
        if (f.DateRange?.To is { } to) q = q.Where(x => x.CreatedAt < to);
        if (!string.IsNullOrWhiteSpace(f.Search)) q = q.Where(x => (x.DisplayName ?? "").Contains(f.Search) || (x.Mobile ?? "").Contains(f.Search) || (x.Email ?? "").Contains(f.Search));
        var total = await q.CountAsync(ct);
        q = ApplyCustomerSort(q, f);
        var data = await q.Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).Select(x => new CustomerReportRow(x.PublicId, x.DisplayName, x.Mobile, x.Email, x.IsActive, x.CreatedAt, db.Orders.Count(o => o.CustomerId == x.Id), db.Orders.Where(o => o.CustomerId == x.Id).Sum(o => (decimal?)o.PayableAmount) ?? 0, db.Wallets.Where(w => w.CustomerId == x.Id).Select(w => (decimal?)w.Balance).FirstOrDefault() ?? 0)).ToListAsync(ct);
        return MakePage(data, f, total);
    }

    private async Task<ReportPage<EmployeeReportRow>> PageEmployees(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var branches = await ResolveBranchesAsync(a, f.BranchIds, ct);
        var q = db.Users.AsNoTracking().Where(x => x.UserType != UserType.Customer);
        if (!a.HasGlobalAccess || f.BranchIds?.Count > 0) q = q.Where(x => x.UserRoles.Any(r => r.BranchId.HasValue && branches.Contains(r.BranchId.Value)));
        if (f.EmployeeId.HasValue) q = q.Where(x => x.PublicId == f.EmployeeId);
        if (f.DateRange?.From is { } from) q = q.Where(x => x.CreatedAt >= from);
        if (f.DateRange?.To is { } to) q = q.Where(x => x.CreatedAt < to);
        if (!string.IsNullOrWhiteSpace(f.Search)) q = q.Where(x => (x.DisplayName ?? "").Contains(f.Search) || (x.Mobile ?? "").Contains(f.Search) || (x.Email ?? "").Contains(f.Search));
        var total = await q.CountAsync(ct);
        var data = await ApplyEmployeeSort(q, f).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).Select(x => new EmployeeReportRow(x.PublicId, x.DisplayName, x.Mobile, x.Email, x.IsActive, x.UserRoles.Select(r => r.Role.Code).ToArray(), x.UserRoles.Where(r => r.BranchId.HasValue).Select(r => db.Branches.Where(b => b.Id == r.BranchId!.Value).Select(b => b.PublicId).First()).ToArray(), db.Deliveries.Count(d => d.AssignedEmployeeId == x.Id), db.Deliveries.Count(d => d.AssignedEmployeeId == x.Id && d.Status == DeliveryStatus.Delivered))).ToListAsync(ct);
        return MakePage(data, f, total);
    }

    private async Task<ReportPage<OrderReportRow>> PageOrders(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var q = ApplyBranches(db.Orders.AsNoTracking(), await ResolveBranchesAsync(a, f.BranchIds, ct));
        q = q.Where(x => (!f.CustomerId.HasValue || x.Customer.PublicId == f.CustomerId) && (!f.ProductId.HasValue || x.Items.Any(i => i.Product.PublicId == f.ProductId)));
        if (f.DateRange?.From is { } from) q = q.Where(x => x.CreatedAt >= from); if (f.DateRange?.To is { } to) q = q.Where(x => x.CreatedAt < to);
        var statuses = ParseStatuses<OrderStatus>(f.Statuses); if (statuses.Count > 0) q = q.Where(x => statuses.Contains(x.Status)); if (!string.IsNullOrWhiteSpace(f.Search)) q = q.Where(x => x.OrderNumber.Contains(f.Search) || (x.Customer.DisplayName ?? "").Contains(f.Search));
        var total = await q.CountAsync(ct); var data = await ApplyOrderSort(q, f).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).Select(x => new OrderReportRow(x.PublicId, x.OrderNumber, x.Type, x.Status, x.CreatedAt, x.Customer.PublicId, x.Customer.DisplayName, x.Branch.PublicId, x.Branch.Code, x.Branch.Name, x.PayableAmount)).ToListAsync(ct); return MakePage(data, f, total);
    }

    private async Task<ReportPage<SubscriptionReportRow>> PageSubscriptions(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var q = ApplyBranches(db.Subscriptions.AsNoTracking(), await ResolveBranchesAsync(a, f.BranchIds, ct));
        if (f.CustomerId.HasValue) q = q.Where(x => x.Customer.PublicId == f.CustomerId); if (f.ProductId.HasValue) q = q.Where(x => x.Product.PublicId == f.ProductId); var statuses = ParseStatuses<SubscriptionStatus>(f.Statuses); if (statuses.Count > 0) q = q.Where(x => statuses.Contains(x.Status)); if (f.DateRange?.From is { } from) q = q.Where(x => x.CreatedAt >= from); if (f.DateRange?.To is { } to) q = q.Where(x => x.CreatedAt < to); if (!string.IsNullOrWhiteSpace(f.Search)) q = q.Where(x => (x.Customer.DisplayName ?? "").Contains(f.Search) || x.ProductNameSnapshot.Contains(f.Search));
        var total = await q.CountAsync(ct); var data = await ApplySubscriptionSort(q, f).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).Select(x => new SubscriptionReportRow(x.PublicId, x.Status, x.Customer.PublicId, x.Customer.DisplayName, x.Product.PublicId, x.ProductNameSnapshot, x.Branch.PublicId, x.Branch.Name, x.StartDate, x.EndDate, x.PayableAmount, x.TotalEntitlement, x.UsedEntitlement, x.TotalEntitlement - x.UsedEntitlement)).ToListAsync(ct); return MakePage(data, f, total);
    }

    private async Task<ReportPage<PaymentReportRow>> PagePayments(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var q = ApplyPaymentBranches(db.Payments.AsNoTracking(), await ResolveBranchesAsync(a, f.BranchIds, ct)); if (f.CustomerId.HasValue) q = q.Where(x => x.Customer.PublicId == f.CustomerId); var statuses = ParseStatuses<PaymentStatus>(f.Statuses); if (statuses.Count > 0) q = q.Where(x => statuses.Contains(x.Status)); if (!string.IsNullOrWhiteSpace(f.PaymentState)) { var paymentState = ParseStatus<PaymentStatus>(f.PaymentState); q = q.Where(x => x.Status == paymentState); }
        if (f.DateRange?.From is { } from) q = q.Where(x => x.CreatedAt >= from); if (f.DateRange?.To is { } to) q = q.Where(x => x.CreatedAt < to);
        var total = await q.CountAsync(ct); var data = await ApplyPaymentSort(q, f).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).Select(x => new PaymentReportRow(x.PublicId, x.Customer.PublicId, x.Method, x.Status, x.Amount, x.RefundedAmount, x.Currency, x.CreatedAt, x.VerifiedAt, x.FailedAt)).ToListAsync(ct); return MakePage(data, f, total);
    }

    private async Task<ReportPage<WalletReportRow>> PageWallets(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var q = ApplyWalletBranches(db.Wallets.AsNoTracking(), await ResolveBranchesAsync(a, f.BranchIds, ct)); if (f.CustomerId.HasValue) q = q.Where(x => x.Customer.PublicId == f.CustomerId); if (f.DateRange?.From is { } from) q = q.Where(x => x.Transactions.Any(t => t.OccurredAt >= from)); if (f.DateRange?.To is { } to) q = q.Where(x => x.Transactions.Any(t => t.OccurredAt < to)); if (!string.IsNullOrWhiteSpace(f.Search)) q = q.Where(x => (x.Customer.DisplayName ?? "").Contains(f.Search));
        var total = await q.CountAsync(ct); var data = await ApplyWalletSort(q, f).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).Select(x => new WalletReportRow(x.Customer.PublicId, x.Customer.DisplayName, x.Balance, x.Currency, x.Transactions.OrderByDescending(t => t.OccurredAt).Select(t => (DateTime?)t.OccurredAt).FirstOrDefault(), x.Transactions.Count())).ToListAsync(ct); return MakePage(data, f, total);
    }

    private async Task<ReportPage<DeliveryReportRow>> PageDeliveries(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var q = ApplyBranches(db.Deliveries.AsNoTracking(), await ResolveBranchesAsync(a, f.BranchIds, ct)); if (f.CustomerId.HasValue) q = q.Where(x => x.Customer.PublicId == f.CustomerId); if (f.EmployeeId.HasValue) q = q.Where(x => x.AssignedEmployee != null && x.AssignedEmployee.PublicId == f.EmployeeId); var statuses = ParseStatuses<DeliveryStatus>(f.Statuses); if (statuses.Count > 0) q = q.Where(x => statuses.Contains(x.Status)); if (f.DateRange?.From is { } from) q = q.Where(x => x.CreatedAt >= from); if (f.DateRange?.To is { } to) q = q.Where(x => x.CreatedAt < to);
        var total = await q.CountAsync(ct); var data = await ApplyDeliverySort(q, f).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).Select(x => new DeliveryReportRow(x.PublicId, x.Branch.PublicId, x.Branch.Name, x.Customer.PublicId, x.Customer.DisplayName, x.AssignedEmployee == null ? null : x.AssignedEmployee.PublicId, x.AssignedEmployee == null ? null : x.AssignedEmployee.DisplayName, x.ScheduledDate, x.Status, x.CompletedAt, x.Status == DeliveryStatus.Failed ? x.FailureReason : null)).ToListAsync(ct); return MakePage(data, f, total);
    }

    private async Task<ReportPage<DairyReportRow>> PageDairy(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var branches = await ResolveBranchesAsync(a, f.BranchIds, ct);
        var production = ApplyBranches(db.MilkProductions.AsNoTracking(), branches)
            .Select(x => new
            {
                BranchId = db.Branches.Where(b => b.Id == x.BranchId).Select(b => b.PublicId).First(),
                BranchName = db.Branches.Where(b => b.Id == x.BranchId).Select(b => b.Name).First(),
                OccurredAt = x.ProductionAt,
                Quantity = x.QuantityProduced,
                x.Unit,
                Purpose = (string?)null
            });
        var usage = ApplyBranches(db.MilkUsages.AsNoTracking(), branches)
            .Select(x => new
            {
                BranchId = db.Branches.Where(b => b.Id == x.BranchId).Select(b => b.PublicId).First(),
                BranchName = db.Branches.Where(b => b.Id == x.BranchId).Select(b => b.Name).First(),
                OccurredAt = x.UsedAt,
                Quantity = x.QuantityUsed,
                x.Batch.Unit,
                Purpose = (string?)x.Purpose
            });
        var query = production.Concat(usage);
        if (f.DateRange?.From is { } from) query = query.Where(x => x.OccurredAt >= from);
        if (f.DateRange?.To is { } to) query = query.Where(x => x.OccurredAt < to);
        var total = await query.CountAsync(ct);
        var sorted = (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch
        {
            (null, _) or ("occurredat", ReportSortDirection.Descending) => query.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.BranchId),
            ("occurredat", ReportSortDirection.Ascending) => query.OrderBy(x => x.OccurredAt).ThenBy(x => x.BranchId),
            ("quantity", ReportSortDirection.Ascending) => query.OrderBy(x => x.Quantity).ThenBy(x => x.OccurredAt),
            ("quantity", ReportSortDirection.Descending) => query.OrderByDescending(x => x.Quantity).ThenByDescending(x => x.OccurredAt),
            ("branchname", ReportSortDirection.Ascending) => query.OrderBy(x => x.BranchName).ThenBy(x => x.OccurredAt),
            ("branchname", ReportSortDirection.Descending) => query.OrderByDescending(x => x.BranchName).ThenByDescending(x => x.OccurredAt),
            _ => throw UnsupportedSort(f.SortBy!, ReportModule.Dairy)
        };
        var rows = await sorted
            .Skip((f.Page - 1) * f.PageSize)
            .Take(f.PageSize)
            .ToListAsync(ct);
        var data = rows.Select(x => new DairyReportRow(
            ReportModule.Dairy,
            x.BranchId,
            x.BranchName,
            x.OccurredAt,
            x.Quantity,
            x.Unit,
            null,
            x.Purpose)).ToArray();
        return MakePage(data, f, total);
    }

    private async Task<ReportPage<MilkTestReportRow>> PageMilkTests(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var q = ApplyBranches(
            db.MilkTests.AsNoTracking(),
            await ResolveBranchesAsync(a, f.BranchIds, ct));
        if (f.CustomerId.HasValue)
        {
            q = q.Where(x => x.Customer.PublicId == f.CustomerId);
        }

        var statuses = ParseStatuses<MilkTestStatus>(f.Statuses);
        if (statuses.Count > 0)
        {
            q = q.Where(x => statuses.Contains(x.Status));
        }
        if (f.DateRange?.From is { } from)
        {
            q = q.Where(x => x.RequestedAt >= from);
        }
        if (f.DateRange?.To is { } to)
        {
            q = q.Where(x => x.RequestedAt < to);
        }

        var total = await q.CountAsync(ct);
        var data = await ApplyMilkTestSort(q, f)
            .Skip((f.Page - 1) * f.PageSize)
            .Take(f.PageSize)
            .Select(x => new MilkTestReportRow(
                x.PublicId,
                db.Branches.Where(b => b.Id == x.BranchId).Select(b => b.PublicId).First(),
                db.Branches.Where(b => b.Id == x.BranchId).Select(b => b.Name).First(),
                x.Customer.PublicId,
                x.RequestedAt,
                x.Status,
                x.CustomerDecision,
                x.Parameters.Count(),
                x.Images.Count(),
                x.CompletedAt))
            .ToListAsync(ct);
        return MakePage(data, f, total);
    }

    private async Task<ReportPage<CameraReportRow>> PageCameras(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var q = ApplyBranches(db.Cameras.AsNoTracking(), await ResolveBranchesAsync(a, f.BranchIds, ct)); if (!string.IsNullOrWhiteSpace(f.Search)) q = q.Where(x => x.DisplayName.Contains(f.Search)); var total = await q.CountAsync(ct); var data = await ApplyCameraSort(q, f).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).Select(x => new CameraReportRow(x.PublicId, db.Branches.Where(b => b.Id == x.BranchId).Select(b => b.PublicId).First(), db.Branches.Where(b => b.Id == x.BranchId).Select(b => b.Name).First(), x.DisplayName, x.IsPublic, x.IsActive, x.DisplayOrder, x.Stream == null ? null : x.Stream.Protocol)).ToListAsync(ct); return MakePage(data, f, total);
    }

    private async Task<ReportPage<NotificationReportRow>> PageNotifications(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var q = ApplyNotificationScope(db.NotificationEvents.AsNoTracking(), a, await ResolveBranchesAsync(a, f.BranchIds, ct)); var statuses = ParseStatuses<NotificationEventStatus>(f.Statuses); if (statuses.Count > 0) q = q.Where(x => statuses.Contains(x.Status)); DateTime? from = f.DateRange?.From; DateTime? to = f.DateRange?.To; if (from.HasValue) q = q.Where(x => x.OccurredAt >= from.Value); if (to.HasValue) q = q.Where(x => x.OccurredAt < to.Value); if (!string.IsNullOrWhiteSpace(f.Search)) q = q.Where(x => x.EventType.Contains(f.Search)); var total = await q.CountAsync(ct); var data = await ApplyNotificationSort(q, f).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).Select(x => new NotificationReportRow(x.PublicId, x.EventType, x.Status, x.OccurredAt, x.IsCritical, x.Notifications.SelectMany(n => n.Deliveries).Count(), x.Notifications.SelectMany(n => n.Deliveries).Count(d => d.Status == NotificationDeliveryStatus.Failed), x.Notifications.SelectMany(n => n.Deliveries).Sum(d => d.AttemptCount))).ToListAsync(ct); return MakePage(data, f, total);
    }

    private async Task<ReportPage<AuditReportRow>> PageAudit(ReportActor a, ReportFilter f, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking(); if (!a.HasGlobalAccess) q = q.Where(x => x.UserId == a.UserId || (x.UserId.HasValue && db.UserRoles.Any(r => r.UserId == x.UserId && r.BranchId.HasValue && a.BranchIds.Contains(r.BranchId.Value)))); DateTime? from = f.DateRange?.From; DateTime? to = f.DateRange?.To; if (from.HasValue) q = q.Where(x => x.CreatedAt >= from.Value); if (to.HasValue) q = q.Where(x => x.CreatedAt < to.Value); if (!string.IsNullOrWhiteSpace(f.Search)) q = q.Where(x => x.Action.Contains(f.Search) || x.EntityType.Contains(f.Search)); var total = await q.CountAsync(ct); var data = await ApplyAuditSort(q, f).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).Select(x => new AuditReportRow(x.Id, x.UserId, x.Action, x.EntityType, x.EntityId, x.Reason, x.CreatedAt)).ToListAsync(ct); return MakePage(data, f, total);
    }

    private async Task<HashSet<long>> ScopedCustomerIds(ReportActor a, ReportFilter f, CancellationToken ct) => (await ApplyBranches(db.Orders.AsNoTracking(), await ResolveBranchesAsync(a, f.BranchIds, ct)).Select(x => x.CustomerId).Concat(ApplyBranches(db.Subscriptions.AsNoTracking(), await ResolveBranchesAsync(a, f.BranchIds, ct)).Select(x => x.CustomerId)).Distinct().ToListAsync(ct)).ToHashSet();
    private async Task<HashSet<long>> ResolveBranchesAsync(ReportActor a, IReadOnlyCollection<Guid>? requested, CancellationToken ct)
    {
        ValidateActor(a);
        var allowed = a.HasGlobalAccess
            ? await db.Branches.AsNoTracking().Select(x => new { x.Id, x.PublicId }).ToListAsync(ct)
            : await db.Branches.AsNoTracking()
                .Where(x => a.BranchIds.Distinct().Contains(x.Id))
                .Select(x => new { x.Id, x.PublicId })
                .ToListAsync(ct);

        if (requested is null || requested.Count == 0)
        {
            return allowed.Select(x => x.Id).ToHashSet();
        }

        var requestedIds = requested.Distinct().ToArray();
        var selected = await db.Branches.AsNoTracking()
            .Where(x => requestedIds.Contains(x.PublicId))
            .Select(x => new { x.Id, x.PublicId })
            .ToListAsync(ct);
        if (selected.Count != requestedIds.Length)
        {
            throw new ValidationAppException(
                "One or more requested report branches do not exist.",
                nameof(ReportFilter.BranchIds));
        }

        var allowedIds = allowed.Select(x => x.Id).ToHashSet();
        if (selected.Any(x => !allowedIds.Contains(x.Id)))
        {
            throw new ForbiddenAppException(
                "One or more requested report branches are outside your permitted scope.");
        }

        return selected.Select(x => x.Id).ToHashSet();
    }
    private static IQueryable<Order> ApplyBranches(IQueryable<Order> q, IReadOnlySet<long> ids) => q.Where(x => ids.Contains(x.BranchId));
    private static IQueryable<Subscription> ApplyBranches(IQueryable<Subscription> q, IReadOnlySet<long> ids) => q.Where(x => ids.Contains(x.BranchId));
    private static IQueryable<Delivery> ApplyBranches(IQueryable<Delivery> q, IReadOnlySet<long> ids) => q.Where(x => ids.Contains(x.BranchId));
    private static IQueryable<MilkProduction> ApplyBranches(IQueryable<MilkProduction> q, IReadOnlySet<long> ids) => q.Where(x => ids.Contains(x.BranchId));
    private static IQueryable<MilkUsage> ApplyBranches(IQueryable<MilkUsage> q, IReadOnlySet<long> ids) => q.Where(x => ids.Contains(x.BranchId));
    private static IQueryable<MilkTest> ApplyBranches(IQueryable<MilkTest> q, IReadOnlySet<long> ids) => q.Where(x => ids.Contains(x.BranchId));
    private static IQueryable<Camera> ApplyBranches(IQueryable<Camera> q, IReadOnlySet<long> ids) => q.Where(x => ids.Contains(x.BranchId));
    private static IQueryable<Payment> ApplyPaymentBranches(IQueryable<Payment> q, IReadOnlySet<long> ids) => q.Where(x => (x.Order != null && ids.Contains(x.Order.BranchId)) || (x.Subscription != null && ids.Contains(x.Subscription.BranchId)));
    private static IQueryable<DoodhDirect.Domain.Wallets.Wallet> ApplyWalletBranches(IQueryable<DoodhDirect.Domain.Wallets.Wallet> q, IReadOnlySet<long> ids) => q.Where(x => x.Transactions.Any(t => (t.Order != null && ids.Contains(t.Order.BranchId)) || (t.Subscription != null && ids.Contains(t.Subscription.BranchId)) || (t.Payment != null && ((t.Payment.Order != null && ids.Contains(t.Payment.Order.BranchId)) || (t.Payment.Subscription != null && ids.Contains(t.Payment.Subscription.BranchId))))));
    private static IQueryable<NotificationEvent> ApplyNotificationScope(IQueryable<NotificationEvent> q, ReportActor a, IReadOnlySet<long> branches) => a.HasGlobalAccess ? q : q.Where(x => x.UserId == a.UserId || x.User.UserRoles.Any(r => r.BranchId.HasValue && branches.Contains(r.BranchId.Value)));
    private static IQueryable<User> ApplyCustomerSort(IQueryable<User> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("createdat", ReportSortDirection.Descending) => q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id), ("createdat", ReportSortDirection.Ascending) => q.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id), ("name" or "displayname", ReportSortDirection.Ascending) => q.OrderBy(x => x.DisplayName).ThenBy(x => x.Id), ("name" or "displayname", ReportSortDirection.Descending) => q.OrderByDescending(x => x.DisplayName).ThenByDescending(x => x.Id), ("isactive", ReportSortDirection.Ascending) => q.OrderBy(x => x.IsActive).ThenBy(x => x.Id), ("isactive", ReportSortDirection.Descending) => q.OrderByDescending(x => x.IsActive).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.Customers) };
    private static IQueryable<User> ApplyEmployeeSort(IQueryable<User> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("createdat", ReportSortDirection.Descending) => q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id), ("createdat", ReportSortDirection.Ascending) => q.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id), ("name" or "displayname", ReportSortDirection.Ascending) => q.OrderBy(x => x.DisplayName).ThenBy(x => x.Id), ("name" or "displayname", ReportSortDirection.Descending) => q.OrderByDescending(x => x.DisplayName).ThenByDescending(x => x.Id), ("isactive", ReportSortDirection.Ascending) => q.OrderBy(x => x.IsActive).ThenBy(x => x.Id), ("isactive", ReportSortDirection.Descending) => q.OrderByDescending(x => x.IsActive).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.Employees) };
    private static IQueryable<Order> ApplyOrderSort(IQueryable<Order> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("createdat", ReportSortDirection.Descending) => q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id), ("createdat", ReportSortDirection.Ascending) => q.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id), ("ordernumber", ReportSortDirection.Ascending) => q.OrderBy(x => x.OrderNumber).ThenBy(x => x.Id), ("ordernumber", ReportSortDirection.Descending) => q.OrderByDescending(x => x.OrderNumber).ThenByDescending(x => x.Id), ("status", ReportSortDirection.Ascending) => q.OrderBy(x => x.Status).ThenBy(x => x.Id), ("status", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id), ("payableamount", ReportSortDirection.Ascending) => q.OrderBy(x => x.PayableAmount).ThenBy(x => x.Id), ("payableamount", ReportSortDirection.Descending) => q.OrderByDescending(x => x.PayableAmount).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.Orders) };
    private static IQueryable<Subscription> ApplySubscriptionSort(IQueryable<Subscription> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("createdat", ReportSortDirection.Descending) => q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id), ("createdat", ReportSortDirection.Ascending) => q.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id), ("startdate", ReportSortDirection.Ascending) => q.OrderBy(x => x.StartDate).ThenBy(x => x.Id), ("startdate", ReportSortDirection.Descending) => q.OrderByDescending(x => x.StartDate).ThenByDescending(x => x.Id), ("enddate", ReportSortDirection.Ascending) => q.OrderBy(x => x.EndDate).ThenBy(x => x.Id), ("enddate", ReportSortDirection.Descending) => q.OrderByDescending(x => x.EndDate).ThenByDescending(x => x.Id), ("status", ReportSortDirection.Ascending) => q.OrderBy(x => x.Status).ThenBy(x => x.Id), ("status", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id), ("payableamount", ReportSortDirection.Ascending) => q.OrderBy(x => x.PayableAmount).ThenBy(x => x.Id), ("payableamount", ReportSortDirection.Descending) => q.OrderByDescending(x => x.PayableAmount).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.Subscriptions) };
    private static IQueryable<Payment> ApplyPaymentSort(IQueryable<Payment> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("createdat", ReportSortDirection.Descending) => q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id), ("createdat", ReportSortDirection.Ascending) => q.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id), ("amount", ReportSortDirection.Ascending) => q.OrderBy(x => x.Amount).ThenBy(x => x.Id), ("amount", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Amount).ThenByDescending(x => x.Id), ("refundedamount", ReportSortDirection.Ascending) => q.OrderBy(x => x.RefundedAmount).ThenBy(x => x.Id), ("refundedamount", ReportSortDirection.Descending) => q.OrderByDescending(x => x.RefundedAmount).ThenByDescending(x => x.Id), ("status", ReportSortDirection.Ascending) => q.OrderBy(x => x.Status).ThenBy(x => x.Id), ("status", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.Payments) };
    private static IQueryable<DoodhDirect.Domain.Wallets.Wallet> ApplyWalletSort(IQueryable<DoodhDirect.Domain.Wallets.Wallet> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("balance", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Balance).ThenByDescending(x => x.Id), ("balance", ReportSortDirection.Ascending) => q.OrderBy(x => x.Balance).ThenBy(x => x.Id), ("customername", ReportSortDirection.Ascending) => q.OrderBy(x => x.Customer.DisplayName).ThenBy(x => x.Id), ("customername", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Customer.DisplayName).ThenByDescending(x => x.Id), ("transactioncount", ReportSortDirection.Ascending) => q.OrderBy(x => x.Transactions.Count).ThenBy(x => x.Id), ("transactioncount", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Transactions.Count).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.Wallets) };
    private static IQueryable<Delivery> ApplyDeliverySort(IQueryable<Delivery> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("scheduleddate", ReportSortDirection.Descending) => q.OrderByDescending(x => x.ScheduledDate).ThenByDescending(x => x.Id), ("scheduleddate", ReportSortDirection.Ascending) => q.OrderBy(x => x.ScheduledDate).ThenBy(x => x.Id), ("status", ReportSortDirection.Ascending) => q.OrderBy(x => x.Status).ThenBy(x => x.Id), ("status", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id), ("completedat", ReportSortDirection.Ascending) => q.OrderBy(x => x.CompletedAt).ThenBy(x => x.Id), ("completedat", ReportSortDirection.Descending) => q.OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.Deliveries) };
    private static IQueryable<MilkTest> ApplyMilkTestSort(IQueryable<MilkTest> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("requestedat", ReportSortDirection.Descending) => q.OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id), ("requestedat", ReportSortDirection.Ascending) => q.OrderBy(x => x.RequestedAt).ThenBy(x => x.Id), ("status", ReportSortDirection.Ascending) => q.OrderBy(x => x.Status).ThenBy(x => x.Id), ("status", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id), ("completedat", ReportSortDirection.Ascending) => q.OrderBy(x => x.CompletedAt).ThenBy(x => x.Id), ("completedat", ReportSortDirection.Descending) => q.OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.MilkTests) };
    private static IQueryable<Camera> ApplyCameraSort(IQueryable<Camera> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("displayorder", ReportSortDirection.Ascending) => q.OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id), ("displayorder", ReportSortDirection.Descending) => q.OrderByDescending(x => x.DisplayOrder).ThenByDescending(x => x.Id), ("displayname", ReportSortDirection.Ascending) => q.OrderBy(x => x.DisplayName).ThenBy(x => x.Id), ("displayname", ReportSortDirection.Descending) => q.OrderByDescending(x => x.DisplayName).ThenByDescending(x => x.Id), ("isactive", ReportSortDirection.Ascending) => q.OrderBy(x => x.IsActive).ThenBy(x => x.Id), ("isactive", ReportSortDirection.Descending) => q.OrderByDescending(x => x.IsActive).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.Cameras) };
    private static IQueryable<NotificationEvent> ApplyNotificationSort(IQueryable<NotificationEvent> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("occurredat", ReportSortDirection.Descending) => q.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id), ("occurredat", ReportSortDirection.Ascending) => q.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id), ("eventtype", ReportSortDirection.Ascending) => q.OrderBy(x => x.EventType).ThenBy(x => x.Id), ("eventtype", ReportSortDirection.Descending) => q.OrderByDescending(x => x.EventType).ThenByDescending(x => x.Id), ("eventstatus" or "status", ReportSortDirection.Ascending) => q.OrderBy(x => x.Status).ThenBy(x => x.Id), ("eventstatus" or "status", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id), ("iscritical", ReportSortDirection.Ascending) => q.OrderBy(x => x.IsCritical).ThenBy(x => x.Id), ("iscritical", ReportSortDirection.Descending) => q.OrderByDescending(x => x.IsCritical).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.Notifications) };
    private static IQueryable<DoodhDirect.Domain.Auditing.AuditLog> ApplyAuditSort(IQueryable<DoodhDirect.Domain.Auditing.AuditLog> q, ReportFilter f) => (f.SortBy?.ToLowerInvariant(), f.SortDirection) switch { (null, _) or ("createdat", ReportSortDirection.Descending) => q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id), ("createdat", ReportSortDirection.Ascending) => q.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id), ("action", ReportSortDirection.Ascending) => q.OrderBy(x => x.Action).ThenBy(x => x.Id), ("action", ReportSortDirection.Descending) => q.OrderByDescending(x => x.Action).ThenByDescending(x => x.Id), ("entitytype", ReportSortDirection.Ascending) => q.OrderBy(x => x.EntityType).ThenBy(x => x.Id), ("entitytype", ReportSortDirection.Descending) => q.OrderByDescending(x => x.EntityType).ThenByDescending(x => x.Id), _ => throw UnsupportedSort(f.SortBy!, ReportModule.Audit) };
    private static ValidationAppException UnsupportedSort(string sortBy, ReportModule module) => new($"Sort field '{sortBy}' is not supported for the {module} report.", nameof(ReportFilter.SortBy));
    private static HashSet<T> ParseStatuses<T>(IReadOnlyCollection<string>? values) where T : struct, Enum => values is null ? [] : values.Select(ParseStatus<T>).ToHashSet();
    private static T ParseStatus<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new ValidationAppException($"Unknown {typeof(T).Name} value '{value}'.", nameof(ReportFilter.Statuses));
    private static ReportFilter Normalize(ReportFilter f, int maximumPageSize = MaximumPageSize) { if (f.Page < 1 || f.PageSize < 1 || f.PageSize > maximumPageSize) throw new ValidationAppException("Report paging values are invalid."); if (!Enum.IsDefined(f.SortDirection)) throw new ValidationAppException("The report sort direction is invalid.", nameof(ReportFilter.SortDirection)); if (f.SortBy is not null && string.IsNullOrWhiteSpace(f.SortBy)) throw new ValidationAppException("The report sort field cannot be blank.", nameof(ReportFilter.SortBy)); ValidateDateRange(f.DateRange); return f with { Search = f.Search?.Trim(), SortBy = f.SortBy?.Trim() }; }
    private static void ValidateDateRange(ReportDateRange? dateRange) { if (dateRange?.From > dateRange?.To) throw new ValidationAppException("The report date range is invalid.", nameof(ReportDateRange)); if (dateRange?.From is { Kind: not DateTimeKind.Unspecified } || dateRange?.To is { Kind: not DateTimeKind.Unspecified }) throw new ValidationAppException("Report dates must be India-local wall-clock values.", nameof(ReportDateRange)); }
    private static void ValidateActor(ReportActor a) { if (a.UserId <= 0 || (!a.HasGlobalAccess && a.BranchIds.Count == 0)) throw new ForbiddenAppException("A valid report actor and branch scope are required."); }
    private static ReportPage<T> MakePage<T>(IReadOnlyCollection<T> data, ReportFilter f, int total) => new(data, f.Page, f.PageSize, total, f.Page * f.PageSize < total);
}
