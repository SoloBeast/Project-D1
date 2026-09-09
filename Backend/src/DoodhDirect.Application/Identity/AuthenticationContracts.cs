using DoodhDirect.Application.Reports;
using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Application.Identity;

public sealed record AuthUserBranchResult(long Id, string Code, string Name);

public sealed record AuthUserResult(
    Guid PublicUserId,
    string? DisplayName,
    string? Email,
    string? Mobile,
    bool EmailVerified,
    bool HasPassword,
    string? PendingEmail,
    string? PendingMobile,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyCollection<long> BranchIds,
    IReadOnlyCollection<AuthUserBranchResult>? BranchDetails = null);

public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresAt,
    DateTime RefreshTokenExpiresAt);

public sealed record AuthSessionResult(AuthUserResult User, TokenPair Tokens);

public sealed record DeviceInfo(string DeviceIdentifier, string? DeviceName, string? Platform, string? IpAddress, string? UserAgent);

public sealed record RegisterRequest(string DisplayName, string? Email, string? Mobile, string Password, DeviceInfo Device);
public sealed record PasswordLoginRequest(string Login, string Password, DeviceInfo Device);
public sealed record SendOtpRequest(string Mobile, OtpPurpose Purpose, string? IpAddress);
public sealed record VerifyOtpRequest(string Mobile, string Code, OtpPurpose Purpose, string ReqId, DeviceInfo Device);

/// <summary>
/// Discriminated result of a mobile OTP verification.
/// <list type="bullet">
/// <item><see cref="RequiresOnboarding"/> is <c>false</c> when the verified mobile belongs to an
/// existing active user — <see cref="Session"/> then carries the authenticated session.</item>
/// <item>For a Registration purpose on a mobile with no account, the challenge has been consumed
/// and <see cref="RequiresOnboarding"/> is <c>true</c> with the provider-verified
/// <see cref="VerifiedMobile"/> and <see cref="ReqId"/> that the client must present to the
/// onboarding completion operation. No account is created by verification alone.</item>
/// </list>
/// </summary>
public sealed record OtpVerificationResult(
    bool RequiresOnboarding,
    AuthSessionResult? Session,
    string? VerifiedMobile,
    string? ReqId);

/// <summary>
/// Completes OTP-driven customer onboarding after a Registration-purpose OTP for an unknown
/// mobile has been verified. The mobile and reqId must match the consumed challenge; the server
/// creates the Customer account (server-chosen role, verified mobile) and starts the session.
/// </summary>
public sealed record CompleteOtpRegistrationRequest(
    string Mobile,
    string ReqId,
    string NewPassword,
    DeviceInfo Device);

/// <summary>Sets a password on an account that currently has none (e.g. created via OTP login).</summary>
public sealed record SetPasswordRequest(string NewPassword);

/// <summary>Changes the current password after verifying the existing one.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Requests a password-reset OTP for the account's canonical mobile number.</summary>
public sealed record ForgotPasswordRequest(string Mobile, string? IpAddress);

/// <summary>Sets a new password after the server has consumed a PasswordReset OTP challenge.</summary>
public sealed record ResetPasswordRequest(string Mobile, string ReqId, string NewPassword, DeviceInfo Device);

/// <summary>Stages a new (or first) email for confirmation and sends an EmailVerification OTP to it.</summary>
public sealed record RequestEmailChangeRequest(string NewEmail, string? IpAddress);

/// <summary>Stages a new mobile for confirmation and sends a mobile OTP to it.</summary>
public sealed record RequestMobileChangeRequest(string NewMobile, string? IpAddress);

/// <summary>Verifies the EmailVerification OTP and commits the staged pending email.</summary>
public sealed record VerifyEmailChangeRequest(string Code, string ReqId, DeviceInfo Device);

public sealed record EmailChangeRequestedResult(string ReqId, string PendingEmail);
public sealed record MobileChangeRequestedResult(string ReqId, string PendingMobile);

public sealed record EmailVerificationResult(string Email, bool EmailVerified);

/// <summary>Verifies a password-reset OTP without creating a session.</summary>
public sealed record VerifyResetOtpRequest(string Destination, string Code, string ReqId, DeviceInfo Device);

public interface IPasswordHasher
{
    string Hash(string value);
    bool Verify(string hash, string value);
}

public interface ITokenService
{
    TokenPair Create(
        User user,
        UserSession session,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> permissions,
        IReadOnlyCollection<long> branchIds,
        DateTime now);

    string HashRefreshToken(string token);
}

public interface IOtpService
{
    Task<SendOtpResult> SendAsync(SendOtpRequest request, CancellationToken cancellationToken);
    Task<SendOtpResult> RetryAsync(RetryOtpRequest request, CancellationToken cancellationToken);
    /// <summary>
    /// Verifies a mobile OTP. Login and other non-Registration purposes return an authenticated
    /// session for an existing active user. A Registration-purpose verification on a mobile that
    /// belongs to no user consumes the challenge and returns an onboarding-required outcome — the
    /// client must continue through the dedicated onboarding completion operation.
    /// </summary>
    Task<OtpVerificationResult> VerifyAsync(VerifyOtpRequest request, CancellationToken cancellationToken);

    /// <summary>Sends an EmailVerification OTP to an email destination.</summary>
    Task<SendOtpResult> SendEmailOtpAsync(string email, string? ipAddress, CancellationToken cancellationToken);

    /// <summary>Resends the EmailVerification OTP for an email destination.</summary>
    Task<SendOtpResult> RetryEmailOtpAsync(string email, string? ipAddress, CancellationToken cancellationToken);

    /// <summary>Verifies the EmailVerification OTP and commits the user's staged pending email.</summary>
    Task<AuthUserResult> VerifyEmailOtpAsync(VerifyEmailChangeRequest request, long userId, CancellationToken cancellationToken);

    /// <summary>Verifies a mobile change OTP and commits the staged mobile number.</summary>
    Task<AuthUserResult> VerifyMobileChangeOtpAsync(VerifyOtpRequest request, long userId, CancellationToken cancellationToken);

    /// <summary>Verifies a PasswordReset OTP without creating a session.</summary>
    Task VerifyResetOtpAsync(VerifyResetOtpRequest request, CancellationToken cancellationToken);
}

public interface IAuthenticationService
{
    Task<AuthSessionResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<AuthSessionResult> LoginAsync(PasswordLoginRequest request, CancellationToken cancellationToken);
    Task<AuthSessionResult> RefreshAsync(string refreshToken, DeviceInfo device, CancellationToken cancellationToken);
    Task LogoutAsync(Guid sessionPublicId, long userId, CancellationToken cancellationToken);
    Task<AuthUserResult> GetCurrentUserAsync(long userId, CancellationToken cancellationToken);

    /// <summary>Sets a password on an account that currently has none.</summary>
    Task SetPasswordAsync(long userId, SetPasswordRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Completes OTP-driven customer onboarding for a provider-verified mobile with no existing
    /// account, then starts a session for the new Customer.
    /// </summary>
    Task<AuthSessionResult> CompleteOtpRegistrationAsync(CompleteOtpRegistrationRequest request, CancellationToken cancellationToken);

    /// <summary>Verifies the current password, sets a new one, and revokes all other sessions.</summary>
    Task ChangePasswordAsync(long userId, Guid currentSessionPublicId, ChangePasswordRequest request, CancellationToken cancellationToken);

    /// <summary>Resolves the account and sends a password-reset OTP to its verified contact.</summary>
    Task<SendOtpResult> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken);

    /// <summary>Verifies the password-reset OTP, sets a new password, and starts a fresh session.</summary>
    Task<AuthSessionResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);

    /// <summary>Stages a new email and sends an EmailVerification OTP to it.</summary>
    Task<EmailChangeRequestedResult> RequestEmailChangeAsync(long userId, RequestEmailChangeRequest request, CancellationToken cancellationToken);
    Task<MobileChangeRequestedResult> RequestMobileChangeAsync(long userId, RequestMobileChangeRequest request, CancellationToken cancellationToken);
}

public static class AuthorizationCodes
{
    public const string Customer = "CUSTOMER";
    public const string DeliveryStaff = "DELIVERY_STAFF";
    public const string DairyManager = "DAIRY_MANAGER";
    public const string DeliveryManager = "DELIVERY_MANAGER";
    public const string CustomerSupport = "CUSTOMER_SUPPORT";
    public const string Accountant = "ACCOUNTANT";
    public const string SystemAdmin = "SYSTEM_ADMIN";
    public const string Owner = "OWNER";

    public const string GlobalAccess = "ACCESS.GLOBAL";
    public const string CustomerProfilesRead = "CUSTOMERS.PROFILES.READ";
    public const string CustomerProfilesManage = "CUSTOMERS.PROFILES.MANAGE";
    public const string ProfileReadOwn = "IDENTITY.PROFILE.READ_OWN";
    public const string ProfileUpdateOwn = "IDENTITY.PROFILE.UPDATE_OWN";
    public const string SessionsManageOwn = "IDENTITY.SESSIONS.MANAGE_OWN";
    public const string UsersRead = "IDENTITY.USERS.READ";
    public const string UsersManage = "IDENTITY.USERS.MANAGE";
    public const string RolesRead = "IDENTITY.ROLES.READ";
    public const string RolesManage = "IDENTITY.ROLES.MANAGE";
    public const string EmployeesRead = "EMPLOYEES.READ";
    public const string EmployeesManage = "EMPLOYEES.MANAGE";
    public const string IdentityAdministratorsManage = "IDENTITY.ADMINISTRATORS.MANAGE";
    public const string BranchAccess = "IDENTITY.BRANCH.ACCESS";
    public const string CatalogueRead = "CATALOGUE.READ";
    public const string CatalogueManage = "CATALOGUE.MANAGE";
    public const string OrdersCreateOwn = "ORDERS.CREATE_OWN";
    public const string OrdersReadOwn = "ORDERS.READ_OWN";
    public const string OrdersCancelOwn = "ORDERS.CANCEL_OWN";
    public const string OrdersRead = "ORDERS.READ";
    public const string SubscriptionsCreateOwn = "SUBSCRIPTIONS.CREATE_OWN";
    public const string SubscriptionsReadOwn = "SUBSCRIPTIONS.READ_OWN";
    public const string SubscriptionsManageOwn = "SUBSCRIPTIONS.MANAGE_OWN";
    public const string PaymentsCreateOwn = "PAYMENTS.CREATE_OWN";
    public const string PaymentsReadOwn = "PAYMENTS.READ_OWN";
    public const string PaymentsRefund = "PAYMENTS.REFUND";
    public const string DeliveriesReadOwn = "DELIVERIES.READ_OWN";
    public const string DeliveriesOperateAssigned = "DELIVERIES.OPERATE_ASSIGNED";
    public const string DeliveriesTrackAssigned = "DELIVERIES.TRACK_ASSIGNED";
    public const string DeliveriesReadBranch = "DELIVERIES.READ_BRANCH";
    public const string DeliveriesAssignBranch = "DELIVERIES.ASSIGN_BRANCH";
    public const string MilkTestsRequestOwn = "MILK_TESTS.REQUEST_OWN";
    public const string MilkTestsReadOwn = "MILK_TESTS.READ_OWN";
    public const string MilkTestsDecideOwn = "MILK_TESTS.DECIDE_OWN";
    public const string MilkTestsOperateAssigned = "MILK_TESTS.OPERATE_ASSIGNED";
    public const string WalletReadOwn = "WALLET.READ_OWN";
    public const string WalletTopUpOwn = "WALLET.TOPUP_OWN";
    public const string WalletAdjust = "WALLET.ADJUST";
    public const string DairyRead = "DAIRY.READ";
    public const string DairyManage = "DAIRY.MANAGE";
    public const string CamerasViewPublic = "CAMERAS.VIEW_PUBLIC";
    public const string CamerasRead = "CAMERAS.READ";
    public const string CamerasManage = "CAMERAS.MANAGE";
    public const string NotificationTemplatesRead = "NOTIFICATIONS.TEMPLATES.READ";
    public const string NotificationTemplatesManage = "NOTIFICATIONS.TEMPLATES.MANAGE";
    public const string ReportsDashboardRead = ReportPermissions.DashboardRead;
    public const string ReportsAdministrationRead = ReportPermissions.AdministrationRead;
    public const string ReportsFinancialRead = ReportPermissions.FinancialRead;
    public const string ReportsOperationsRead = ReportPermissions.OperationsRead;
    public const string ReportsMilkTestsRead = ReportPermissions.MilkTestsRead;
    public const string ReportsAuditRead = ReportPermissions.AuditRead;
    public const string ReportsExport = ReportPermissions.Export;
    public const string SetupNumberSeriesRead = "SETUP.NUMBER_SERIES.READ";
    public const string SetupNumberSeriesManage = "SETUP.NUMBER_SERIES.MANAGE";
    public const string BranchesRead = "BRANCHES.READ";
    public const string BranchesManage = "BRANCHES.MANAGE";
    public const string SetupOtpProviderRead = "SETUP.OTP_PROVIDER.READ";
    public const string SetupOtpProviderManage = "SETUP.OTP_PROVIDER.MANAGE";
    public const string SetupIntegrationsRead = "SETUP.INTEGRATIONS.READ";
    public const string SetupIntegrationsManage = "SETUP.INTEGRATIONS.MANAGE";

    public const string PermissionClaim = "permission";
    public const string BranchClaim = "branch_id";

    public static readonly IReadOnlyDictionary<string, string> Roles = new Dictionary<string, string>
    {
        [Customer] = "Customer",
        [DeliveryStaff] = "Delivery staff",
        [DairyManager] = "Dairy manager",
        [DeliveryManager] = "Delivery manager",
        [CustomerSupport] = "Customer support",
        [Accountant] = "Accountant",
        [SystemAdmin] = "System administrator",
        [Owner] = "Owner"
    };

    public static readonly IReadOnlyDictionary<string, string> Permissions = new Dictionary<string, string>
    {
        [GlobalAccess] = "Global access",
        [CustomerProfilesRead] = "Read customer profiles and addresses",
        [CustomerProfilesManage] = "Manage customer profiles and addresses",
        [ProfileReadOwn] = "Read own identity profile",
        [ProfileUpdateOwn] = "Update own identity profile",
        [SessionsManageOwn] = "Manage own sessions",
        [UsersRead] = "Read users",
        [UsersManage] = "Manage users",
        [RolesRead] = "Read roles and permissions",
        [RolesManage] = "Manage roles and permissions",
        [EmployeesRead] = "Read employees and employee invitations",
        [EmployeesManage] = "Create, invite, update, deactivate, and reactivate employees",
        [IdentityAdministratorsManage] = "Create and manage system administrators (ownership level)",
        [BranchAccess] = "Access assigned branch",
        [CatalogueRead] = "Read product catalogue",
        [CatalogueManage] = "Manage products, categories, and branch availability",
        [OrdersCreateOwn] = "Preview checkout and create own orders",
        [OrdersReadOwn] = "Read own orders",
        [OrdersCancelOwn] = "Cancel own eligible orders",
        [OrdersRead] = "Read customer orders for administration",
        [SubscriptionsCreateOwn] = "Create own prepaid subscriptions",
        [SubscriptionsReadOwn] = "Read own subscriptions and delivery calendars",
        [SubscriptionsManageOwn] = "Update, pause, resume, cancel, and skip own subscriptions",
        [PaymentsCreateOwn] = "Create and verify own payments",
        [PaymentsReadOwn] = "Read own payments",
        [PaymentsRefund] = "Refund successful payments",
        [DeliveriesReadOwn] = "Read own delivery status and active tracking",
        [DeliveriesOperateAssigned] = "Operate assigned deliveries",
        [DeliveriesTrackAssigned] = "Publish location for active assigned deliveries",
        [DeliveriesReadBranch] = "Read and monitor branch deliveries",
        [DeliveriesAssignBranch] = "Assign and reassign branch deliveries",
        [MilkTestsRequestOwn] = "Request a doorstep milk test for an own eligible delivery",
        [MilkTestsReadOwn] = "Read own doorstep milk-test status and completed evidence",
        [MilkTestsDecideOwn] = "Confirm or reject an own completed doorstep milk test",
        [MilkTestsOperateAssigned] = "Upload evidence and complete doorstep tests for assigned deliveries",
        [WalletReadOwn] = "Read own wallet and ledger",
        [WalletTopUpOwn] = "Top up own wallet through a verified payment",
        [WalletAdjust] = "Adjust customer wallets",
        [DairyRead] = "Read branch dairy operations",
        [DairyManage] = "Record branch dairy production and usage",
        [CamerasViewPublic] = "View active public dairy cameras",
        [CamerasRead] = "Read branch camera metadata",
        [CamerasManage] = "Manage branch camera metadata",
        [NotificationTemplatesRead] = "Read notification templates",
        [NotificationTemplatesManage] = "Manage notification templates",
        [ReportsDashboardRead] = "Read authorized administration dashboard metrics",
        [ReportsAdministrationRead] = "Read authorized customer, employee, order, and subscription reports",
        [ReportsFinancialRead] = "Read authorized payment and wallet reports",
        [ReportsOperationsRead] = "Read authorized delivery, dairy, camera, and notification reports",
        [ReportsMilkTestsRead] = "Read authorized milk-test reports without protected media storage data",
        [ReportsAuditRead] = "Read audit-log metadata",
        [ReportsExport] = "Export authorized reports as CSV or XLSX",
        [SetupNumberSeriesRead] = "View numbering series configuration and live previews",
        [SetupNumberSeriesManage] = "Create and manage numbering series configuration",
        [BranchesRead] = "Read branch records and branch metadata",
        [BranchesManage] = "Create, update, activate, and deactivate branch records",
        [SetupOtpProviderRead] = "View OTP provider configuration and status",
        [SetupOtpProviderManage] = "Configure and test the OTP provider",
        [SetupIntegrationsRead] = "View integration configuration and status",
        [SetupIntegrationsManage] = "Configure and test integrations"
    };
}
