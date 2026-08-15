using DoodhDirect.Domain.Common;

namespace DoodhDirect.Domain.Identity;

public enum UserType
{
    Customer,
    Employee,
    Owner,
    SystemAdministrator
}

public sealed class User : AuditableEntity
{
    private User() { }

    public User(UserType userType)
    {
        UserType = userType;
    }

    public UserType UserType { get; private set; }
    public string? DisplayName { get; private set; }
    public string? Mobile { get; private set; }
    public string? Email { get; private set; }
    public string? PasswordHash { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime? LastLoginAtUtc { get; private set; }

    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();
    public ICollection<UserSession> Sessions { get; } = new List<UserSession>();
    public ICollection<RefreshToken> RefreshTokens { get; } = new List<RefreshToken>();

    public void SetProfile(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
    }

    public void SetContact(string? mobile, string? email)
    {
        Mobile = string.IsNullOrWhiteSpace(mobile) ? null : mobile.Trim();
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
    }

    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    public void AssignRole(Role role, long? branchId = null)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (UserRoles.Any(x => x.RoleId == role.Id && x.BranchId == branchId))
            return;

        UserRoles.Add(new UserRole(this, role, branchId));
    }

    public void RecordLogin(DateTime utcNow)
    {
        LastLoginAtUtc = utcNow;
        SetUpdated(utcNow);
    }
}

public sealed class Role : AuditableEntity
{
    private Role() { }

    public Role(string code, string name)
    {
        Code = code;
        Name = name;
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();
    public ICollection<RolePermission> RolePermissions { get; } = new List<RolePermission>();
}

public sealed class Permission : AuditableEntity
{
    private Permission() { }

    public Permission(string code, string name)
    {
        Code = code;
        Name = name;
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public ICollection<RolePermission> RolePermissions { get; } = new List<RolePermission>();
}

public sealed class UserRole : Entity
{
    private UserRole() { }

    public UserRole(long userId, long roleId, long? branchId = null)
    {
        UserId = userId;
        RoleId = roleId;
        BranchId = branchId;
    }

    internal UserRole(User user, Role role, long? branchId = null)
    {
        User = user;
        Role = role;
        UserId = user.Id;
        RoleId = role.Id;
        BranchId = branchId;
    }

    public long UserId { get; private set; }
    public long RoleId { get; private set; }
    public long? BranchId { get; private set; }
    public User User { get; private set; } = null!;
    public Role Role { get; private set; } = null!;
}

public sealed class RolePermission : Entity
{
    private RolePermission() { }

    public RolePermission(long roleId, long permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }

    public long RoleId { get; private set; }
    public long PermissionId { get; private set; }
    public Role Role { get; private set; } = null!;
    public Permission Permission { get; private set; } = null!;
}

public enum OtpPurpose
{
    Login,
    Registration,
    PasswordReset
}

public sealed class OtpChallenge : PublicEntity
{
    private OtpChallenge() { }

    public OtpChallenge(
        string destination,
        OtpPurpose purpose,
        string codeHash,
        DateTime createdAtUtc,
        DateTime expiresAtUtc,
        int maxAttempts,
        string? requestedFromIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        if (expiresAtUtc <= createdAtUtc) throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        Destination = destination.Trim();
        Purpose = purpose;
        CodeHash = codeHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        MaxAttempts = maxAttempts;
        RequestedFromIp = requestedFromIp;
    }

    public string Destination { get; private set; } = string.Empty;
    public OtpPurpose Purpose { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public int FailedAttempts { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }
    public string? RequestedFromIp { get; private set; }

    public bool CanAttempt(DateTime utcNow) =>
        ConsumedAtUtc is null && ExpiresAtUtc > utcNow && FailedAttempts < MaxAttempts;

    public void RecordFailedAttempt() => FailedAttempts++;

    public void Consume(DateTime utcNow)
    {
        if (!CanAttempt(utcNow)) throw new InvalidOperationException("OTP challenge cannot be consumed.");
        ConsumedAtUtc = utcNow;
    }
}

public sealed class UserSession : PublicEntity
{
    private UserSession() { }

    public UserSession(
        long userId,
        string deviceIdentifierHash,
        string? deviceName,
        string? platform,
        string? ipAddress,
        string? userAgent,
        DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentifierHash);
        UserId = userId;
        DeviceIdentifierHash = deviceIdentifierHash;
        DeviceName = deviceName;
        Platform = platform;
        IPAddress = ipAddress;
        UserAgent = userAgent;
        CreatedAtUtc = createdAtUtc;
        LastSeenAtUtc = createdAtUtc;
    }

    public long UserId { get; private set; }
    public string DeviceIdentifierHash { get; private set; } = string.Empty;
    public string? DeviceName { get; private set; }
    public string? Platform { get; private set; }
    public string? IPAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime LastSeenAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public string? RevocationReason { get; private set; }
    public User User { get; private set; } = null!;
    public ICollection<RefreshToken> RefreshTokens { get; } = new List<RefreshToken>();

    public bool IsActive => RevokedAtUtc is null;

    public void Touch(DateTime utcNow, string? ipAddress)
    {
        LastSeenAtUtc = utcNow;
        IPAddress = ipAddress;
    }

    public void Revoke(DateTime utcNow, string reason)
    {
        RevokedAtUtc = utcNow;
        RevocationReason = reason;
    }
}

public sealed class RefreshToken : Entity
{
    private RefreshToken() { }

    public RefreshToken(long userId, string tokenHash, DateTime expiresAtUtc, long? sessionId = null, DateTime? createdAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        UserId = userId;
        SessionId = sessionId;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow;
    }

    public long UserId { get; private set; }
    public long? SessionId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }
    public User User { get; private set; } = null!;
    public UserSession? Session { get; private set; }

    public bool IsActive(DateTime utcNow) => RevokedAtUtc is null && ExpiresAtUtc > utcNow;

    public void Revoke(DateTime utcNow, string? replacedByTokenHash = null)
    {
        if (RevokedAtUtc is not null) return;
        RevokedAtUtc = utcNow;
        ReplacedByTokenHash = replacedByTokenHash;
    }
}
