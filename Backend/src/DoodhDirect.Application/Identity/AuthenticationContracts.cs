using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Application.Identity;

public sealed record AuthUserResult(
    Guid PublicUserId,
    string? DisplayName,
    string? Email,
    string? Mobile,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyCollection<long> BranchIds);

public sealed record TokenPair(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAtUtc, DateTime RefreshTokenExpiresAtUtc);

public sealed record AuthSessionResult(AuthUserResult User, TokenPair Tokens);

public sealed record DeviceInfo(string DeviceIdentifier, string? DeviceName, string? Platform, string? IpAddress, string? UserAgent);

public sealed record RegisterRequest(string DisplayName, string? Email, string? Mobile, string Password, DeviceInfo Device);
public sealed record PasswordLoginRequest(string Login, string Password, DeviceInfo Device);
public sealed record SendOtpRequest(string Mobile, OtpPurpose Purpose, string? IpAddress);
public sealed record VerifyOtpRequest(string Mobile, string Code, OtpPurpose Purpose, DeviceInfo Device);

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
        DateTime utcNow);

    string HashRefreshToken(string token);
}

public interface IOtpDeliveryService
{
    Task SendAsync(string destination, string code, CancellationToken cancellationToken);
}

public interface IOtpService
{
    Task SendAsync(SendOtpRequest request, CancellationToken cancellationToken);
    Task<AuthSessionResult> VerifyAsync(VerifyOtpRequest request, CancellationToken cancellationToken);
}

public interface IAuthenticationService
{
    Task<AuthSessionResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<AuthSessionResult> LoginAsync(PasswordLoginRequest request, CancellationToken cancellationToken);
    Task<AuthSessionResult> RefreshAsync(string refreshToken, DeviceInfo device, CancellationToken cancellationToken);
    Task LogoutAsync(Guid sessionPublicId, long userId, CancellationToken cancellationToken);
    Task<AuthUserResult> GetCurrentUserAsync(long userId, CancellationToken cancellationToken);
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
    public const string ProfileReadOwn = "IDENTITY.PROFILE.READ_OWN";
    public const string ProfileUpdateOwn = "IDENTITY.PROFILE.UPDATE_OWN";
    public const string SessionsManageOwn = "IDENTITY.SESSIONS.MANAGE_OWN";
    public const string UsersRead = "IDENTITY.USERS.READ";
    public const string UsersManage = "IDENTITY.USERS.MANAGE";
    public const string RolesRead = "IDENTITY.ROLES.READ";
    public const string RolesManage = "IDENTITY.ROLES.MANAGE";
    public const string BranchAccess = "IDENTITY.BRANCH.ACCESS";

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
        [ProfileReadOwn] = "Read own identity profile",
        [ProfileUpdateOwn] = "Update own identity profile",
        [SessionsManageOwn] = "Manage own sessions",
        [UsersRead] = "Read users",
        [UsersManage] = "Manage users",
        [RolesRead] = "Read roles and permissions",
        [RolesManage] = "Manage roles and permissions",
        [BranchAccess] = "Access assigned branch"
    };
}
