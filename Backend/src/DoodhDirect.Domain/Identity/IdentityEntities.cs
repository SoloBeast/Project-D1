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
    public string? Mobile { get; private set; }
    public string? Email { get; private set; }
    public string? PasswordHash { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime? LastLoginAtUtc { get; private set; }

    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();
    public ICollection<RefreshToken> RefreshTokens { get; } = new List<RefreshToken>();

    public void SetContact(string? mobile, string? email)
    {
        Mobile = string.IsNullOrWhiteSpace(mobile) ? null : mobile.Trim();
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
    }

    public void SetPasswordHash(string passwordHash) => PasswordHash = passwordHash;

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

public sealed class RefreshToken : Entity
{
    private RefreshToken() { }

    public RefreshToken(long userId, string tokenHash, DateTime expiresAtUtc)
    {
        UserId = userId;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public long UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }
    public User User { get; private set; } = null!;

    public bool IsActive(DateTime utcNow) => RevokedAtUtc is null && ExpiresAtUtc > utcNow;

    public void Revoke(DateTime utcNow, string? replacedByTokenHash = null)
    {
        RevokedAtUtc = utcNow;
        ReplacedByTokenHash = replacedByTokenHash;
    }
}
