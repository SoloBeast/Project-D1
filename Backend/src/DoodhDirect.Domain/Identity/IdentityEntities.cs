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
    public DateTime? LastLoginAt { get; private set; }

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

    public void RecordLogin(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        LastLoginAt = indiaLocalNow;
    }

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Identity timestamps must be India-local DateTime values with an unspecified kind.",
                parameterName);
        }
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
    PasswordReset,
    EmployeeInvitation
}

public sealed class OtpChallenge : PublicEntity
{
    private OtpChallenge() { }

    public OtpChallenge(
        string destination,
        OtpPurpose purpose,
        string codeHash,
        DateTime createdAt,
        DateTime expiresAt,
        int maxAttempts,
        string? requestedFromIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        EnsureIndiaLocal(createdAt, nameof(createdAt));
        EnsureIndiaLocal(expiresAt, nameof(expiresAt));
        if (expiresAt <= createdAt) throw new ArgumentOutOfRangeException(nameof(expiresAt));
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        Destination = destination.Trim();
        Purpose = purpose;
        CodeHash = codeHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        MaxAttempts = maxAttempts;
        RequestedFromIp = requestedFromIp;
    }

    public string Destination { get; private set; } = string.Empty;
    public OtpPurpose Purpose { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public int FailedAttempts { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTime? ConsumedAt { get; private set; }
    public string? RequestedFromIp { get; private set; }

    public bool CanAttempt(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        return ConsumedAt is null && ExpiresAt > indiaLocalNow && FailedAttempts < MaxAttempts;
    }

    public void RecordFailedAttempt() => FailedAttempts++;

    public void Consume(DateTime indiaLocalNow)
    {
        if (!CanAttempt(indiaLocalNow)) throw new InvalidOperationException("OTP challenge cannot be consumed.");
        ConsumedAt = indiaLocalNow;
    }

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Identity timestamps must be India-local DateTime values with an unspecified kind.",
                parameterName);
        }
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
        DateTime createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentifierHash);
        EnsureIndiaLocal(createdAt, nameof(createdAt));
        UserId = userId;
        DeviceIdentifierHash = deviceIdentifierHash;
        DeviceName = deviceName;
        Platform = platform;
        IPAddress = ipAddress;
        UserAgent = userAgent;
        CreatedAt = createdAt;
        LastSeenAt = createdAt;
    }

    public long UserId { get; private set; }
    public string DeviceIdentifierHash { get; private set; } = string.Empty;
    public string? DeviceName { get; private set; }
    public string? Platform { get; private set; }
    public string? IPAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime LastSeenAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }
    public User User { get; private set; } = null!;
    public ICollection<RefreshToken> RefreshTokens { get; } = new List<RefreshToken>();

    public bool IsActive => RevokedAt is null;

    public void Touch(DateTime indiaLocalNow, string? ipAddress)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        LastSeenAt = indiaLocalNow;
        IPAddress = ipAddress;
    }

    public void Revoke(DateTime indiaLocalNow, string reason)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        RevokedAt = indiaLocalNow;
        RevocationReason = reason;
    }

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Identity timestamps must be India-local DateTime values with an unspecified kind.",
                parameterName);
        }
    }
}

public sealed class RefreshToken : Entity
{
    private RefreshToken() { }

    public RefreshToken(long userId, string tokenHash, DateTime expiresAt, long? sessionId, DateTime createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        EnsureIndiaLocal(expiresAt, nameof(expiresAt));
        EnsureIndiaLocal(createdAt, nameof(createdAt));
        UserId = userId;
        SessionId = sessionId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        CreatedAt = createdAt;
    }

    public long UserId { get; private set; }
    public long? SessionId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }
    public User User { get; private set; } = null!;
    public UserSession? Session { get; private set; }

    public bool IsActive(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        return RevokedAt is null && ExpiresAt > indiaLocalNow;
    }

    public void Revoke(DateTime indiaLocalNow, string? replacedByTokenHash = null)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        if (RevokedAt is not null) return;
        RevokedAt = indiaLocalNow;
        ReplacedByTokenHash = replacedByTokenHash;
    }

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Identity timestamps must be India-local DateTime values with an unspecified kind.",
                parameterName);
        }
    }
}

public enum EmployeeInvitationStatus
{
    Invited,
    Registered,
    Cancelled,
    Expired
}

public sealed class EmployeeInvitation : AuditableEntity
{
    private EmployeeInvitation() { }

    public EmployeeInvitation(
        string inviteeName,
        string inviteeMobile,
        string? inviteeEmail,
        string roleCode,
        long? branchId,
        string tokenHash,
        DateTime createdAt,
        DateTime expiresAt,
        long createdByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteeMobile);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        EnsureIndiaLocal(createdAt, nameof(createdAt));
        EnsureIndiaLocal(expiresAt, nameof(expiresAt));
        if (expiresAt <= createdAt) throw new ArgumentOutOfRangeException(nameof(expiresAt));

        InviteeName = inviteeName.Trim();
        InviteeMobile = inviteeMobile.Trim();
        InviteeEmail = string.IsNullOrWhiteSpace(inviteeEmail) ? null : inviteeEmail.Trim().ToLowerInvariant();
        RoleCode = roleCode;
        BranchId = branchId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        Status = EmployeeInvitationStatus.Invited;
        CreatedByUserId = createdByUserId;
    }

    public string InviteeName { get; private set; } = string.Empty;
    public string InviteeMobile { get; private set; } = string.Empty;
    public string? InviteeEmail { get; private set; }
    public string RoleCode { get; private set; } = string.Empty;
    public long? BranchId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public EmployeeInvitationStatus Status { get; private set; }
    public new DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RegisteredAt { get; private set; }
    public long? RegisteredByUserId { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public long? CancelledByUserId { get; private set; }
    public DateTime? LastResentAt { get; private set; }
    public long? LastResentByUserId { get; private set; }
    public long CreatedByUserId { get; private set; }

    public bool IsUsable(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        return Status == EmployeeInvitationStatus.Invited && ExpiresAt > indiaLocalNow;
    }

    public void MarkRegistered(long registeredByUserId, DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        if (Status != EmployeeInvitationStatus.Invited)
            throw new InvalidOperationException("Only an invited employee invitation can be registered.");
        RegisteredByUserId = registeredByUserId;
        RegisteredAt = indiaLocalNow;
        Status = EmployeeInvitationStatus.Registered;
    }

    public void Cancel(long cancelledByUserId, DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        if (Status != EmployeeInvitationStatus.Invited)
            throw new InvalidOperationException("Only an invited employee invitation can be cancelled.");
        CancelledByUserId = cancelledByUserId;
        CancelledAt = indiaLocalNow;
        Status = EmployeeInvitationStatus.Cancelled;
    }

    public void RecordResend(long resentByUserId, DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        if (Status != EmployeeInvitationStatus.Invited)
            throw new InvalidOperationException("Only an invited employee invitation can be resent.");
        LastResentByUserId = resentByUserId;
        LastResentAt = indiaLocalNow;
    }

    public void ChangeRoleAndBranch(string roleCode, long? branchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleCode);
        RoleCode = roleCode;
        BranchId = branchId;
    }

    public void MarkExpired(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        if (Status == EmployeeInvitationStatus.Invited && ExpiresAt <= indiaLocalNow)
            Status = EmployeeInvitationStatus.Expired;
    }

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Identity timestamps must be India-local DateTime values with an unspecified kind.",
                parameterName);
        }
    }
}
