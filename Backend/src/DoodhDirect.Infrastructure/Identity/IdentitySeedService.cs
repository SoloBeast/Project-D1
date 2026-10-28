using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class IdentitySeedService(DoodhDirectDbContext dbContext)
{
    /// <summary>
    /// Stage 1 employee self-ordering plus Stage 2 own-resource support access:
    /// own-identity permissions granted to every employee role. All of these
    /// scope to the authenticated user's own rows (customerId/userId taken
    /// from the JWT, never from client input), so an employee can order, pay,
    /// manage subscriptions, track their own deliveries, and raise support
    /// tickets for themselves without gaining access to any other customer's
    /// data, branch-wide ticket/delivery views, or any administrative API.
    /// </summary>
    private static readonly string[] EmployeeSelfServicePermissions =
    [
        AuthorizationCodes.OrdersCreateOwn,
        AuthorizationCodes.OrdersReadOwn,
        AuthorizationCodes.OrdersCancelOwn,
        AuthorizationCodes.SubscriptionsCreateOwn,
        AuthorizationCodes.SubscriptionsReadOwn,
        AuthorizationCodes.SubscriptionsManageOwn,
        AuthorizationCodes.PaymentsCreateOwn,
        AuthorizationCodes.PaymentsReadOwn,
        AuthorizationCodes.WalletReadOwn,
        AuthorizationCodes.WalletTopUpOwn,
        AuthorizationCodes.WalletWithdrawOwn,
        AuthorizationCodes.DeliveriesReadOwn,
        AuthorizationCodes.MilkTestsRequestOwn,
        AuthorizationCodes.MilkTestsReadOwn,
        AuthorizationCodes.MilkTestsDecideOwn,
        AuthorizationCodes.RefundReplacementRequestOwn,
        AuthorizationCodes.RefundReplacementReadOwn,
    ];

    private static readonly IReadOnlyDictionary<string, string[]> RolePermissions =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [AuthorizationCodes.Customer] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.OrdersCreateOwn,
                AuthorizationCodes.OrdersReadOwn,
                AuthorizationCodes.OrdersCancelOwn,
                AuthorizationCodes.DeliveriesReadOwn,
                AuthorizationCodes.MilkTestsRequestOwn,
                AuthorizationCodes.MilkTestsReadOwn,
                AuthorizationCodes.MilkTestsDecideOwn,
                AuthorizationCodes.SubscriptionsCreateOwn,
                AuthorizationCodes.SubscriptionsReadOwn,
                AuthorizationCodes.SubscriptionsManageOwn,
                AuthorizationCodes.PaymentsCreateOwn,
                AuthorizationCodes.PaymentsReadOwn,
                AuthorizationCodes.WalletReadOwn,
                AuthorizationCodes.WalletTopUpOwn,
                AuthorizationCodes.WalletWithdrawOwn,
                AuthorizationCodes.RefundReplacementRequestOwn,
                AuthorizationCodes.RefundReplacementReadOwn,
                AuthorizationCodes.CamerasViewPublic
            ],
            [AuthorizationCodes.DeliveryStaff] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.BranchAccess,
                AuthorizationCodes.DeliveriesOperateAssigned,
                AuthorizationCodes.DeliveriesTrackAssigned,
                AuthorizationCodes.MilkTestsOperateAssigned,
                ..EmployeeSelfServicePermissions,
            ],
            [AuthorizationCodes.DairyManager] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.UsersRead,
                AuthorizationCodes.BranchAccess,
                AuthorizationCodes.OrdersReadBranch,
                AuthorizationCodes.MilkTestsReadBranch,
                AuthorizationCodes.DeliveriesReadBranch,
                AuthorizationCodes.DeliveriesAssignBranch,
                AuthorizationCodes.DairyRead,
                AuthorizationCodes.DairyManage,
                AuthorizationCodes.CamerasRead,
                AuthorizationCodes.CamerasManage,
                AuthorizationCodes.ReportsDashboardRead,
                AuthorizationCodes.ReportsOperationsRead,
                AuthorizationCodes.ReportsMilkTestsRead,
                AuthorizationCodes.ReportsExport,
                AuthorizationCodes.RefundReplacementReadBranch,
                AuthorizationCodes.RefundReplacementManageBranch,
                AuthorizationCodes.SetupRefundReplacementRead,
                ..EmployeeSelfServicePermissions,
            ],
            [AuthorizationCodes.DeliveryManager] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.UsersRead,
                AuthorizationCodes.BranchAccess,
                AuthorizationCodes.DeliveriesReadBranch,
                AuthorizationCodes.DeliveriesAssignBranch,
                AuthorizationCodes.ReportsDashboardRead,
                AuthorizationCodes.ReportsOperationsRead,
                AuthorizationCodes.ReportsMilkTestsRead,
                AuthorizationCodes.ReportsExport,
                AuthorizationCodes.RefundReplacementReadBranch,
                ..EmployeeSelfServicePermissions,
            ],
            [AuthorizationCodes.CustomerSupport] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.UsersRead,
                AuthorizationCodes.CustomerProfilesRead,
                AuthorizationCodes.OrdersRead,
                AuthorizationCodes.OrdersReadBranch,
                AuthorizationCodes.MilkTestsReadBranch,
                AuthorizationCodes.ReportsDashboardRead,
                AuthorizationCodes.ReportsAdministrationRead,
                AuthorizationCodes.RefundReplacementReadBranch,
                AuthorizationCodes.RefundReplacementManageBranch,
                AuthorizationCodes.SetupRefundReplacementRead,
                ..EmployeeSelfServicePermissions,
            ],
            [AuthorizationCodes.Accountant] =
            [
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.UsersRead,
                AuthorizationCodes.CustomerProfilesRead,
                AuthorizationCodes.OrdersRead,
                AuthorizationCodes.PaymentsRefund,
                AuthorizationCodes.WalletAdjust,
                AuthorizationCodes.WithdrawalsProcess,
                AuthorizationCodes.ReportsDashboardRead,
                AuthorizationCodes.ReportsAdministrationRead,
                AuthorizationCodes.ReportsFinancialRead,
                AuthorizationCodes.ReportsExport,
                AuthorizationCodes.RefundBankExport,
                ..EmployeeSelfServicePermissions,
            ],
            [AuthorizationCodes.SystemAdmin] =
            [
                AuthorizationCodes.GlobalAccess,
                AuthorizationCodes.ProfileReadOwn,
                AuthorizationCodes.ProfileUpdateOwn,
                AuthorizationCodes.SessionsManageOwn,
                AuthorizationCodes.UsersRead,
                AuthorizationCodes.UsersManage,
                AuthorizationCodes.EmployeesRead,
                AuthorizationCodes.EmployeesManage,
                AuthorizationCodes.CustomerProfilesRead,
                AuthorizationCodes.CustomerProfilesManage,
                AuthorizationCodes.CatalogueRead,
                AuthorizationCodes.CatalogueManage,
                AuthorizationCodes.OrdersRead,
                AuthorizationCodes.OrdersReadBranch,
                AuthorizationCodes.DairyRead,
                AuthorizationCodes.DairyManage,
                AuthorizationCodes.CamerasRead,
                AuthorizationCodes.CamerasManage,
                AuthorizationCodes.NotificationTemplatesRead,
                AuthorizationCodes.NotificationTemplatesManage,
                AuthorizationCodes.DeliveriesReadBranch,
                AuthorizationCodes.DeliveriesAssignBranch,
                AuthorizationCodes.PaymentsRefund,
                AuthorizationCodes.WalletAdjust,
                AuthorizationCodes.WithdrawalsProcess,
                AuthorizationCodes.ReportsDashboardRead,
                AuthorizationCodes.ReportsAdministrationRead,
                AuthorizationCodes.ReportsFinancialRead,
                AuthorizationCodes.ReportsOperationsRead,
                AuthorizationCodes.ReportsMilkTestsRead,
                AuthorizationCodes.ReportsAuditRead,
                AuthorizationCodes.ReportsExport,
                AuthorizationCodes.SetupNumberSeriesRead,
                AuthorizationCodes.SetupNumberSeriesManage,
                AuthorizationCodes.SetupTaxChargesRead,
                AuthorizationCodes.SetupTaxChargesManage,
                AuthorizationCodes.BranchesRead,
                AuthorizationCodes.BranchesManage,
                AuthorizationCodes.SetupOtpProviderRead,
                AuthorizationCodes.SetupOtpProviderManage,
                AuthorizationCodes.SetupIntegrationsRead,
                AuthorizationCodes.SetupIntegrationsManage,
                AuthorizationCodes.SetupRefundReplacementRead,
                AuthorizationCodes.SetupRefundReplacementManage,
                AuthorizationCodes.SetupBrandingRead,
                AuthorizationCodes.SetupBrandingManage,
                AuthorizationCodes.SetupCompanyBankRead,
                AuthorizationCodes.SetupCompanyBankManage,
                AuthorizationCodes.RefundReplacementRequestOwn,
                AuthorizationCodes.RefundReplacementReadOwn,
                AuthorizationCodes.RefundReplacementReadBranch,
                AuthorizationCodes.RefundReplacementManageBranch
            ],
            [AuthorizationCodes.Owner] = AuthorizationCodes.Permissions.Keys.ToArray()
        };

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        await executionStrategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

            var roles = await dbContext.Roles.ToDictionaryAsync(x => x.Code, StringComparer.Ordinal, cancellationToken);
            foreach (var definition in AuthorizationCodes.Roles)
            {
                if (roles.ContainsKey(definition.Key))
                    continue;

                var role = new Role(definition.Key, definition.Value);
                dbContext.Roles.Add(role);
                roles.Add(definition.Key, role);
            }

            var permissions = await dbContext.Permissions.ToDictionaryAsync(x => x.Code, StringComparer.Ordinal, cancellationToken);
            foreach (var definition in AuthorizationCodes.Permissions)
            {
                if (permissions.ContainsKey(definition.Key))
                    continue;

                var permission = new Permission(definition.Key, definition.Value);
                dbContext.Permissions.Add(permission);
                permissions.Add(definition.Key, permission);
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            var existingAssignments = await dbContext.RolePermissions
                .Select(x => new { x.RoleId, x.PermissionId })
                .ToListAsync(cancellationToken);
            var assignmentKeys = existingAssignments
                .Select(x => (x.RoleId, x.PermissionId))
                .ToHashSet();

            foreach (var roleDefinition in RolePermissions)
            {
                var role = roles[roleDefinition.Key];
                foreach (var permissionCode in roleDefinition.Value)
                {
                    var permission = permissions[permissionCode];
                    if (assignmentKeys.Add((role.Id, permission.Id)))
                        dbContext.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
