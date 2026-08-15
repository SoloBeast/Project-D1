using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DoodhDirect.Infrastructure.Persistence;

public sealed class DoodhDirectDbContext(DbContextOptions<DoodhDirectDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemConfiguration> SystemConfigurations => Set<SystemConfiguration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dbo");
        ConfigureUser(modelBuilder);
        ConfigureRole(modelBuilder);
        ConfigurePermission(modelBuilder);
        ConfigureUserRole(modelBuilder);
        ConfigureRolePermission(modelBuilder);
        ConfigureOtpChallenge(modelBuilder);
        ConfigureUserSession(modelBuilder);
        ConfigureRefreshToken(modelBuilder);
        ConfigureAuditLog(modelBuilder);
        ConfigureSystemConfiguration(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyAuditTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (EntityEntry entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not DoodhDirect.Domain.Common.AuditableEntity entity)
            {
                continue;
            }

            if (entry.State == EntityState.Added)
            {
                entity.SetCreated(now);
            }
            else if (entry.State == EntityState.Modified)
            {
                entity.SetUpdated(now);
            }
        }
    }

    private static void ConfigureUser(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<User>();
        entity.ToTable("User");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.HasIndex(x => x.Mobile).IsUnique().HasFilter("[Mobile] IS NOT NULL");
        entity.HasIndex(x => x.Email).IsUnique().HasFilter("[Email] IS NOT NULL");
        entity.Property(x => x.UserType).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.DisplayName).HasMaxLength(160);
        entity.Property(x => x.Mobile).HasMaxLength(20);
        entity.Property(x => x.Email).HasMaxLength(320);
        entity.Property(x => x.PasswordHash).HasMaxLength(500);
    }

    private static void ConfigureRole(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Role>();
        entity.ToTable("Role");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.HasIndex(x => x.Code).IsUnique();
        entity.Property(x => x.Code).HasMaxLength(80).IsRequired();
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
    }

    private static void ConfigurePermission(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Permission>();
        entity.ToTable("Permission");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.HasIndex(x => x.Code).IsUnique();
        entity.Property(x => x.Code).HasMaxLength(120).IsRequired();
        entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
    }

    private static void ConfigureUserRole(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<UserRole>();
        entity.ToTable("UserRole");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        entity.HasIndex(x => new { x.UserId, x.RoleId })
            .IsUnique()
            .HasFilter("[BranchId] IS NULL");
        entity.HasIndex(x => new { x.UserId, x.RoleId, x.BranchId })
            .IsUnique()
            .HasFilter("[BranchId] IS NOT NULL");
        entity.HasOne(x => x.User).WithMany(x => x.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Role).WithMany(x => x.UserRoles).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRolePermission(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RolePermission>();
        entity.ToTable("RolePermission");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        entity.HasIndex(x => new { x.RoleId, x.PermissionId }).IsUnique();
        entity.HasOne(x => x.Role).WithMany(x => x.RolePermissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Permission).WithMany(x => x.RolePermissions).HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOtpChallenge(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<OtpChallenge>();
        entity.ToTable("OtpChallenge");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Destination).HasMaxLength(320).IsRequired();
        entity.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.CodeHash).HasMaxLength(128).IsRequired();
        entity.Property(x => x.RequestedFromIp).HasMaxLength(64);
        entity.HasIndex(x => new { x.Destination, x.Purpose, x.CreatedAtUtc });
        entity.HasIndex(x => new { x.ExpiresAtUtc, x.ConsumedAtUtc });
    }

    private static void ConfigureUserSession(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<UserSession>();
        entity.ToTable("UserSession");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.DeviceIdentifierHash).HasMaxLength(128).IsRequired();
        entity.Property(x => x.DeviceName).HasMaxLength(160);
        entity.Property(x => x.Platform).HasMaxLength(40);
        entity.Property(x => x.IPAddress).HasMaxLength(64);
        entity.Property(x => x.UserAgent).HasMaxLength(1000);
        entity.Property(x => x.RevocationReason).HasMaxLength(200);
        entity.HasIndex(x => new { x.UserId, x.RevokedAtUtc, x.LastSeenAtUtc });
        entity.HasIndex(x => new { x.UserId, x.DeviceIdentifierHash, x.RevokedAtUtc });
        entity.HasOne(x => x.User).WithMany(x => x.Sessions).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRefreshToken(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<RefreshToken>();
        entity.ToTable("RefreshToken");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        entity.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        entity.Property(x => x.ReplacedByTokenHash).HasMaxLength(128);
        entity.HasIndex(x => x.TokenHash).IsUnique();
        entity.HasIndex(x => new { x.UserId, x.ExpiresAtUtc });
        entity.HasIndex(x => new { x.SessionId, x.ExpiresAtUtc });
        entity.HasOne(x => x.User).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Session).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAuditLog(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AuditLog>();
        entity.ToTable("AuditLog");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        entity.Property(x => x.Action).HasMaxLength(100).IsRequired();
        entity.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        entity.Property(x => x.EntityId).HasMaxLength(100).IsRequired();
        entity.Property(x => x.OldValueJson).HasColumnType("nvarchar(max)");
        entity.Property(x => x.NewValueJson).HasColumnType("nvarchar(max)");
        entity.Property(x => x.IPAddress).HasMaxLength(64);
        entity.Property(x => x.UserAgent).HasMaxLength(1000);
        entity.Property(x => x.Reason).HasMaxLength(1000);
        entity.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAtUtc });
    }

    private static void ConfigureSystemConfiguration(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SystemConfiguration>();
        entity.ToTable("SystemConfiguration");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Key).HasMaxLength(150).IsRequired();
        entity.HasIndex(x => x.Key).IsUnique();
        entity.Property(x => x.Value).HasMaxLength(4000).IsRequired();
        entity.Property(x => x.ValueType).HasMaxLength(30).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(500);
    }

    private static void ConfigurePublicEntity<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : DoodhDirect.Domain.Common.PublicEntity
    {
        entity.Property(x => x.PublicId).HasDefaultValueSql("NEWSEQUENTIALID()");
        entity.HasIndex(x => x.PublicId).IsUnique();
    }
}
