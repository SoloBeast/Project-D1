using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
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
    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();
    public DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<ProductBranch> ProductBranches => Set<ProductBranch>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

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
        ConfigureCustomerProfile(modelBuilder);
        ConfigureCustomerAddress(modelBuilder);
        ConfigureProductCategory(modelBuilder);
        ConfigureProduct(modelBuilder);
        ConfigureBranch(modelBuilder);
        ConfigureProductBranch(modelBuilder);
        ConfigureOrder(modelBuilder);
        ConfigureOrderItem(modelBuilder);
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

    private static void ConfigureCustomerProfile(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CustomerProfile>();
        entity.ToTable("CustomerProfile");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.HasIndex(x => x.UserId).IsUnique();
        entity.Property(x => x.FirstName).HasMaxLength(100);
        entity.Property(x => x.LastName).HasMaxLength(100);
        entity.Property(x => x.Gender).HasMaxLength(40);
        entity.Property(x => x.AlternateMobile).HasMaxLength(20);
        entity.HasOne(x => x.User).WithOne().HasForeignKey<CustomerProfile>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureCustomerAddress(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CustomerAddress>();
        entity.ToTable("CustomerAddress");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Label).HasMaxLength(80).IsRequired();
        entity.Property(x => x.AddressLine1).HasMaxLength(200).IsRequired();
        entity.Property(x => x.AddressLine2).HasMaxLength(200);
        entity.Property(x => x.Locality).HasMaxLength(120).IsRequired();
        entity.Property(x => x.City).HasMaxLength(100).IsRequired();
        entity.Property(x => x.State).HasMaxLength(100).IsRequired();
        entity.Property(x => x.PinCode).HasMaxLength(6).IsRequired();
        entity.Property(x => x.Landmark).HasMaxLength(160);
        entity.Property(x => x.DeliveryInstructions).HasMaxLength(500);
        entity.Property(x => x.ContactName).HasMaxLength(160).IsRequired();
        entity.Property(x => x.ContactMobile).HasMaxLength(20).IsRequired();
        entity.Property(x => x.Latitude).HasPrecision(9, 6).IsRequired();
        entity.Property(x => x.Longitude).HasPrecision(9, 6).IsRequired();
        entity.HasIndex(x => new { x.UserId, x.IsActive, x.IsDefault })
            .IsUnique()
            .HasFilter("[IsActive] = 1 AND [IsDefault] = 1");
        entity.HasIndex(x => new { x.UserId, x.IsActive });
        entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureProductCategory(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProductCategory>();
        entity.ToTable("ProductCategory");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Code).HasMaxLength(50).IsRequired();
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(500);
        entity.HasIndex(x => x.Code).IsUnique();
        entity.HasIndex(x => new { x.IsActive, x.Name });
    }

    private static void ConfigureProduct(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Product>();
        entity.ToTable("Product", table =>
        {
            table.HasCheckConstraint("CK_Product_Price", "[Price] > 0");
            table.HasCheckConstraint("CK_Product_UnitOfMeasure", "[UnitOfMeasure] IN ('litre', 'kilogram', 'gram', 'piece')");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Sku).HasColumnName("SKU").HasMaxLength(50).IsRequired();
        entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(2000);
        entity.Property(x => x.UnitOfMeasure).HasMaxLength(20).IsRequired();
        entity.Property(x => x.Price).HasPrecision(18, 2).IsRequired();
        entity.HasIndex(x => x.Sku).IsUnique();
        entity.HasIndex(x => new { x.CategoryId, x.IsActive, x.Name });
        entity.HasOne(x => x.Category).WithMany(x => x.Products).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureBranch(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Branch>();
        entity.ToTable("Branch", table =>
        {
            table.HasCheckConstraint("CK_Branch_Latitude", "[Latitude] >= -90 AND [Latitude] <= 90");
            table.HasCheckConstraint("CK_Branch_Longitude", "[Longitude] >= -180 AND [Longitude] <= 180");
            table.HasCheckConstraint("CK_Branch_ServiceRadiusKm", "[ServiceRadiusKm] IS NULL OR [ServiceRadiusKm] > 0");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Code).HasMaxLength(50).IsRequired();
        entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
        entity.Property(x => x.AddressLine1).HasMaxLength(300);
        entity.Property(x => x.AddressLine2).HasMaxLength(300);
        entity.Property(x => x.Locality).HasMaxLength(150);
        entity.Property(x => x.City).HasMaxLength(100).IsRequired();
        entity.Property(x => x.State).HasMaxLength(100).IsRequired();
        entity.Property(x => x.PinCode).HasMaxLength(10);
        entity.Property(x => x.Latitude).HasPrecision(9, 6).IsRequired();
        entity.Property(x => x.Longitude).HasPrecision(9, 6).IsRequired();
        entity.Property(x => x.ServiceRadiusKm).HasPrecision(8, 2);
        entity.HasIndex(x => x.Code).IsUnique();
        entity.HasIndex(x => new { x.IsActive, x.Name });
    }

    private static void ConfigureProductBranch(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProductBranch>();
        entity.ToTable("ProductBranch", table =>
            table.HasCheckConstraint("CK_ProductBranch_MaxDailyQuantity", "[MaxDailyQuantity] IS NULL OR [MaxDailyQuantity] > 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        entity.Property(x => x.MaxDailyQuantity).HasPrecision(18, 3);
        entity.HasIndex(x => new { x.ProductId, x.BranchId }).IsUnique();
        entity.HasIndex(x => new { x.BranchId, x.IsAvailable });
        entity.HasOne(x => x.Product).WithMany(x => x.ProductBranches).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Branch).WithMany(x => x.ProductBranches).HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOrder(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Order>();
        entity.ToTable("Order", table =>
        {
            table.HasCheckConstraint("CK_Order_Subtotal", "[Subtotal] >= 0");
            table.HasCheckConstraint("CK_Order_DiscountAmount", "[DiscountAmount] >= 0 AND [DiscountAmount] <= [Subtotal]");
            table.HasCheckConstraint("CK_Order_PayableAmount", "[PayableAmount] >= 0");
            table.HasCheckConstraint("CK_Order_Latitude", "[LatitudeSnapshot] >= -90 AND [LatitudeSnapshot] <= 90");
            table.HasCheckConstraint("CK_Order_Longitude", "[LongitudeSnapshot] >= -180 AND [LongitudeSnapshot] <= 180");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        entity.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
        entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.Subtotal).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.DiscountAmount).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.PayableAmount).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.BranchCodeSnapshot).HasMaxLength(50).IsRequired();
        entity.Property(x => x.BranchNameSnapshot).HasMaxLength(200).IsRequired();
        entity.Property(x => x.AddressLabelSnapshot).HasMaxLength(80).IsRequired();
        entity.Property(x => x.AddressLine1Snapshot).HasMaxLength(200).IsRequired();
        entity.Property(x => x.AddressLine2Snapshot).HasMaxLength(200);
        entity.Property(x => x.LocalitySnapshot).HasMaxLength(120).IsRequired();
        entity.Property(x => x.CitySnapshot).HasMaxLength(100).IsRequired();
        entity.Property(x => x.StateSnapshot).HasMaxLength(100).IsRequired();
        entity.Property(x => x.PinCodeSnapshot).HasMaxLength(10).IsRequired();
        entity.Property(x => x.LandmarkSnapshot).HasMaxLength(160);
        entity.Property(x => x.DeliveryInstructionsSnapshot).HasMaxLength(500);
        entity.Property(x => x.ContactNameSnapshot).HasMaxLength(160).IsRequired();
        entity.Property(x => x.ContactMobileSnapshot).HasMaxLength(20).IsRequired();
        entity.Property(x => x.LatitudeSnapshot).HasPrecision(9, 6).IsRequired();
        entity.Property(x => x.LongitudeSnapshot).HasPrecision(9, 6).IsRequired();
        entity.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique();
        entity.HasIndex(x => new { x.CustomerId, x.CreatedAtUtc });
        entity.HasIndex(x => new { x.BranchId, x.Status, x.CreatedAtUtc });
        entity.HasIndex(x => x.OrderNumber).IsUnique();
        entity.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CustomerAddress).WithMany().HasForeignKey(x => x.CustomerAddressId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOrderItem(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<OrderItem>();
        entity.ToTable("OrderItem", table =>
        {
            table.HasCheckConstraint("CK_OrderItem_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_OrderItem_UnitPrice", "[UnitPrice] >= 0");
            table.HasCheckConstraint("CK_OrderItem_LineTotal", "[LineTotal] >= 0");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        entity.Property(x => x.Quantity).HasPrecision(18, 3).IsRequired();
        entity.Property(x => x.UnitPrice).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.LineTotal).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.SkuSnapshot).HasColumnName("SKU_Snapshot").HasMaxLength(50).IsRequired();
        entity.Property(x => x.ProductNameSnapshot).HasMaxLength(200).IsRequired();
        entity.Property(x => x.UnitOfMeasureSnapshot).HasMaxLength(20).IsRequired();
        entity.HasIndex(x => new { x.OrderId, x.ProductId }).IsUnique();
        entity.HasOne(x => x.Order).WithMany(x => x.Items).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
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
