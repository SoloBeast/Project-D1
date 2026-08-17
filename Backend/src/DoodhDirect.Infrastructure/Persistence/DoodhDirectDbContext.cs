using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Cameras;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Domain.Subscriptions;
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
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentWebhook> PaymentWebhooks => Set<PaymentWebhook>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionSchedule> SubscriptionSchedules => Set<SubscriptionSchedule>();
    public DbSet<SubscriptionDelivery> SubscriptionDeliveries => Set<SubscriptionDelivery>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryAssignment> DeliveryAssignments => Set<DeliveryAssignment>();
    public DbSet<MilkProduction> MilkProductions => Set<MilkProduction>();
    public DbSet<MilkBatch> MilkBatches => Set<MilkBatch>();
    public DbSet<MilkUsage> MilkUsages => Set<MilkUsage>();
    public DbSet<MilkTest> MilkTests => Set<MilkTest>();
    public DbSet<MilkTestParameter> MilkTestParameters => Set<MilkTestParameter>();
    public DbSet<MilkTestImage> MilkTestImages => Set<MilkTestImage>();
    public DbSet<Camera> Cameras => Set<Camera>();
    public DbSet<CameraStream> CameraStreams => Set<CameraStream>();
    public DbSet<NotificationEvent> NotificationEvents => Set<NotificationEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<NotificationAttempt> NotificationAttempts => Set<NotificationAttempt>();
    public DbSet<DeliveryOtp> DeliveryOtps => Set<DeliveryOtp>();
    public DbSet<DeliveryLocation> DeliveryLocations => Set<DeliveryLocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var usesSqlite = Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";
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
        ConfigureBranch(modelBuilder, usesSqlite);
        ConfigureProductBranch(modelBuilder);
        ConfigureOrder(modelBuilder, usesSqlite);
        ConfigureOrderItem(modelBuilder);
        ConfigurePayment(modelBuilder);
        ConfigurePaymentWebhook(modelBuilder);
        ConfigureRefund(modelBuilder);
        ConfigureWallet(modelBuilder, usesSqlite);
        ConfigureWalletTransaction(modelBuilder);
        ConfigureSubscription(modelBuilder);
        ConfigureSubscriptionSchedule(modelBuilder);
        ConfigureSubscriptionDelivery(modelBuilder);
        ConfigureDelivery(modelBuilder, usesSqlite);
        ConfigureDeliveryAssignment(modelBuilder);
        ConfigureMilkProduction(modelBuilder, usesSqlite);
        ConfigureMilkBatch(modelBuilder, usesSqlite);
        ConfigureMilkUsage(modelBuilder, usesSqlite);
        ConfigureMilkTest(modelBuilder, usesSqlite);
        ConfigureMilkTestParameter(modelBuilder);
        ConfigureMilkTestImage(modelBuilder, usesSqlite);
        ConfigureCamera(modelBuilder, usesSqlite);
        ConfigureCameraStream(modelBuilder);
        ConfigureNotificationEvent(modelBuilder);
        ConfigureNotification(modelBuilder);
        ConfigureNotificationTemplate(modelBuilder);
        ConfigureNotificationPreference(modelBuilder);
        ConfigureUserDevice(modelBuilder, usesSqlite);
        ConfigureNotificationDelivery(modelBuilder, usesSqlite);
        ConfigureNotificationAttempt(modelBuilder);
        ConfigureDeliveryOtp(modelBuilder);
        ConfigureDeliveryLocation(modelBuilder, usesSqlite);
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
        entity.Property(x => x.OldValueJson);
        entity.Property(x => x.NewValueJson);
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

    private static void ConfigureBranch(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<Branch>();
        entity.ToTable("Branch", table =>
        {
            if (!usesSqlite)
            {
                table.HasCheckConstraint("CK_Branch_Latitude", "[Latitude] >= -90 AND [Latitude] <= 90");
                table.HasCheckConstraint("CK_Branch_Longitude", "[Longitude] >= -180 AND [Longitude] <= 180");
                table.HasCheckConstraint("CK_Branch_ServiceRadiusKm", "[ServiceRadiusKm] IS NULL OR [ServiceRadiusKm] > 0");
            }
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

    private static void ConfigureOrder(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<Order>();
        entity.ToTable("Order", table =>
        {
            table.HasCheckConstraint("CK_Order_Subtotal", "[Subtotal] >= 0");
            table.HasCheckConstraint("CK_Order_DiscountAmount", "[DiscountAmount] >= 0 AND [DiscountAmount] <= [Subtotal]");
            table.HasCheckConstraint("CK_Order_PayableAmount", "[PayableAmount] >= 0");
            if (!usesSqlite)
            {
                table.HasCheckConstraint("CK_Order_Latitude", "[LatitudeSnapshot] >= -90 AND [LatitudeSnapshot] <= 90");
                table.HasCheckConstraint("CK_Order_Longitude", "[LongitudeSnapshot] >= -180 AND [LongitudeSnapshot] <= 180");
            }
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

    private static void ConfigurePayment(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Payment>();
        entity.ToTable("Payment", table =>
        {
            table.HasCheckConstraint("CK_Payment_Amount", "[Amount] > 0");
            table.HasCheckConstraint("CK_Payment_RefundedAmount", "[RefundedAmount] >= 0 AND [RefundedAmount] <= [Amount]");
            table.HasCheckConstraint(
                "CK_Payment_Target",
                "([OrderId] IS NOT NULL AND [SubscriptionId] IS NULL) OR " +
                "([OrderId] IS NULL AND [SubscriptionId] IS NOT NULL)");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Method).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.Amount).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.RefundedAmount).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        entity.Property(x => x.GatewayOrderId).HasMaxLength(100);
        entity.Property(x => x.GatewayPaymentId).HasMaxLength(100);
        entity.Property(x => x.GatewayStatus).HasMaxLength(50);
        entity.Property(x => x.FailureCode).HasMaxLength(100);
        entity.Property(x => x.FailureMessage).HasMaxLength(500);
        entity.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique();
        entity.HasIndex(x => x.OrderId).HasFilter("[OrderId] IS NOT NULL");
        entity.HasIndex(x => x.SubscriptionId).HasFilter("[SubscriptionId] IS NOT NULL");
        entity.HasIndex(x => x.GatewayOrderId).IsUnique().HasFilter("[GatewayOrderId] IS NOT NULL");
        entity.HasIndex(x => x.GatewayPaymentId).IsUnique().HasFilter("[GatewayPaymentId] IS NOT NULL");
        entity.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePaymentWebhook(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PaymentWebhook>();
        entity.ToTable("PaymentWebhook");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Provider).HasMaxLength(30).IsRequired();
        entity.Property(x => x.EventId).HasMaxLength(150).IsRequired();
        entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.ErrorCode).HasMaxLength(100);
        entity.Property(x => x.ErrorMessage).HasMaxLength(1000);
        entity.HasIndex(x => new { x.Provider, x.EventId }).IsUnique();
        entity.HasIndex(x => new { x.Status, x.ReceivedAtUtc });
    }

    private static void ConfigureRefund(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Refund>();
        entity.ToTable("Refund", table =>
            table.HasCheckConstraint("CK_Refund_Amount", "[Amount] > 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.Amount).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        entity.Property(x => x.GatewayRefundId).HasMaxLength(100);
        entity.Property(x => x.FailureCode).HasMaxLength(100);
        entity.Property(x => x.FailureMessage).HasMaxLength(500);
        entity.HasIndex(x => new { x.PaymentId, x.IdempotencyKey }).IsUnique();
        entity.HasIndex(x => x.GatewayRefundId).IsUnique().HasFilter("[GatewayRefundId] IS NOT NULL");
        entity.HasOne(x => x.Payment).WithMany(x => x.Refunds).HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.RequestedByUser).WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureWallet(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<Wallet>();
        entity.ToTable("Wallet", table =>
            table.HasCheckConstraint("CK_Wallet_Balance", "[Balance] >= 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Balance).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();

        var rowVersion = entity.Property(x => x.RowVersion);
        if (usesSqlite)
        {
            rowVersion.IsConcurrencyToken().ValueGeneratedNever();
        }
        else
        {
            rowVersion.IsRowVersion();
        }

        entity.HasIndex(x => x.CustomerId).IsUnique();
        entity.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureWalletTransaction(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WalletTransaction>();
        entity.ToTable("WalletTransaction", table =>
        {
            table.HasCheckConstraint("CK_WalletTransaction_Amount", "[Amount] <> 0");
            table.HasCheckConstraint("CK_WalletTransaction_Balances", "[BalanceBefore] >= 0 AND [BalanceAfter] >= 0 AND [BalanceAfter] = [BalanceBefore] + [Amount]");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.BalanceBefore).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.Amount).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.BalanceAfter).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(500).IsRequired();
        entity.HasIndex(x => new { x.WalletId, x.IdempotencyKey }).IsUnique();
        entity.HasIndex(x => new { x.WalletId, x.OccurredAtUtc });
        entity.HasIndex(x => x.SubscriptionId).HasFilter("[SubscriptionId] IS NOT NULL");
        entity.HasOne(x => x.Wallet).WithMany(x => x.Transactions).HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Payment).WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.PerformedByUser).WithMany().HasForeignKey(x => x.PerformedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSubscription(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Subscription>();
        entity.ToTable("Subscription", table =>
        {
            table.HasCheckConstraint("CK_Subscription_Dates", "[EndDate] >= [StartDate]");
            table.HasCheckConstraint("CK_Subscription_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_Subscription_Entitlement", "[TotalEntitlement] > 0 AND [UsedEntitlement] >= 0 AND [UsedEntitlement] <= [TotalEntitlement]");
            table.HasCheckConstraint("CK_Subscription_PayableAmount", "[PayableAmount] > 0");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        entity.Ignore(x => x.RemainingEntitlement);
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.StartDate).HasColumnType("date").IsRequired();
        entity.Property(x => x.EndDate).HasColumnType("date").IsRequired();
        entity.Property(x => x.Quantity).HasPrecision(18, 3).IsRequired();
        entity.Property(x => x.UnitPrice).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.PayableAmount).HasPrecision(18, 2).IsRequired();
        entity.Property(x => x.ProductSkuSnapshot).HasMaxLength(50).IsRequired();
        entity.Property(x => x.ProductNameSnapshot).HasMaxLength(200).IsRequired();
        entity.Property(x => x.UnitOfMeasureSnapshot).HasMaxLength(20).IsRequired();
        entity.Property(x => x.BranchCodeSnapshot).HasMaxLength(50).IsRequired();
        entity.Property(x => x.BranchNameSnapshot).HasMaxLength(200).IsRequired();
        entity.Property(x => x.AddressSnapshot).HasMaxLength(2000).IsRequired();
        entity.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique();
        entity.HasIndex(x => new { x.CustomerId, x.Status, x.CreatedAtUtc });
        entity.HasIndex(x => new { x.BranchId, x.Status, x.StartDate });
        entity.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CustomerAddress).WithMany().HasForeignKey(x => x.CustomerAddressId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSubscriptionSchedule(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SubscriptionSchedule>();
        entity.ToTable("SubscriptionSchedule");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        entity.Property(x => x.DayOfWeek).HasConversion<string>().HasMaxLength(15).IsRequired();
        entity.HasIndex(x => new { x.SubscriptionId, x.DayOfWeek }).IsUnique();
        entity.HasOne(x => x.Subscription).WithMany(x => x.Schedules).HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSubscriptionDelivery(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SubscriptionDelivery>();
        entity.ToTable("SubscriptionDelivery", table =>
            table.HasCheckConstraint("CK_SubscriptionDelivery_Quantity", "[Quantity] > 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.ScheduledDate).HasColumnType("date").IsRequired();
        entity.Property(x => x.Quantity).HasPrecision(18, 3).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.BranchCodeSnapshot).HasMaxLength(50).IsRequired();
        entity.Property(x => x.BranchNameSnapshot).HasMaxLength(200).IsRequired();
        entity.Property(x => x.AddressSnapshot).HasMaxLength(2000).IsRequired();
        entity.HasIndex(x => new { x.SubscriptionId, x.ScheduledDate }).IsUnique();
        entity.HasIndex(x => new { x.Status, x.ScheduledDate });
        entity.HasOne(x => x.Subscription).WithMany(x => x.Deliveries).HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDelivery(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<Delivery>();
        entity.ToTable("Delivery", table =>
        {
            table.HasCheckConstraint(
                "CK_Delivery_Source",
                "([OrderId] IS NOT NULL AND [SubscriptionDeliveryId] IS NULL AND [SourceType] = 'OneTimeOrder') OR " +
                "([OrderId] IS NULL AND [SubscriptionDeliveryId] IS NOT NULL AND [SourceType] = 'SubscriptionOccurrence')");
            if (!usesSqlite)
            {
                table.HasCheckConstraint("CK_Delivery_DestinationLatitude", "[DestinationLatitude] >= -90 AND [DestinationLatitude] <= 90");
                table.HasCheckConstraint("CK_Delivery_DestinationLongitude", "[DestinationLongitude] >= -180 AND [DestinationLongitude] <= 180");
                table.HasCheckConstraint("CK_Delivery_FailureCoordinates", "([FailureLatitude] IS NULL AND [FailureLongitude] IS NULL) OR ([FailureLatitude] BETWEEN -90 AND 90 AND [FailureLongitude] BETWEEN -180 AND 180)");
            }
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.ScheduledDate).HasColumnType("date").IsRequired();
        entity.Property(x => x.ReferenceNumber).HasMaxLength(80).IsRequired();
        entity.Property(x => x.CustomerNameSnapshot).HasMaxLength(160).IsRequired();
        entity.Property(x => x.CustomerMobileSnapshot).HasMaxLength(20).IsRequired();
        entity.Property(x => x.DestinationAddressSnapshot).HasMaxLength(2000).IsRequired();
        entity.Property(x => x.DeliveryInstructionsSnapshot).HasMaxLength(500);
        entity.Property(x => x.DestinationLatitude).HasPrecision(9, 6).IsRequired();
        entity.Property(x => x.DestinationLongitude).HasPrecision(9, 6).IsRequired();
        entity.Property(x => x.FailureLatitude).HasPrecision(9, 6);
        entity.Property(x => x.FailureLongitude).HasPrecision(9, 6);
        entity.Property(x => x.FailureReason).HasMaxLength(120);
        entity.Property(x => x.Remarks).HasMaxLength(1000);
        entity.Property(x => x.OperationalNotes).HasMaxLength(1000);
        entity.Ignore(x => x.IsTrackingActive);
        entity.HasIndex(x => x.OrderId).IsUnique().HasFilter("[OrderId] IS NOT NULL");
        entity.HasIndex(x => x.SubscriptionDeliveryId).IsUnique().HasFilter("[SubscriptionDeliveryId] IS NOT NULL");
        entity.HasIndex(x => new { x.BranchId, x.ScheduledDate, x.Status });
        entity.HasIndex(x => new { x.AssignedEmployeeId, x.ScheduledDate, x.Status });
        entity.HasIndex(x => new { x.CustomerId, x.ScheduledDate });
        entity.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.SubscriptionDelivery).WithMany().HasForeignKey(x => x.SubscriptionDeliveryId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.AssignedEmployee).WithMany().HasForeignKey(x => x.AssignedEmployeeId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDeliveryAssignment(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DeliveryAssignment>();
        entity.ToTable("DeliveryAssignment");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        entity.Property(x => x.Reason).HasMaxLength(500);
        entity.HasIndex(x => new { x.DeliveryId, x.AssignedAtUtc });
        entity.HasIndex(x => new { x.EmployeeId, x.AssignedAtUtc });
        entity.HasOne(x => x.Delivery).WithMany(x => x.Assignments).HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.PreviousEmployee).WithMany().HasForeignKey(x => x.PreviousEmployeeId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.AssignedByUser).WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDeliveryOtp(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DeliveryOtp>();
        entity.ToTable("DeliveryOtp", table =>
            table.HasCheckConstraint("CK_DeliveryOtp_Attempts", "[MaximumAttempts] > 0 AND [AttemptCount] >= 0 AND [AttemptCount] <= [MaximumAttempts]"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.CodeHash).HasMaxLength(128).IsRequired();
        entity.HasIndex(x => new { x.DeliveryId, x.CreatedAtUtc });
        entity.HasIndex(x => new { x.ExpiresAtUtc, x.ConsumedAtUtc });
        entity.HasOne(x => x.Delivery).WithMany(x => x.Otps).HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDeliveryLocation(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<DeliveryLocation>();
        entity.ToTable("DeliveryLocation", table =>
        {
            if (!usesSqlite)
            {
                table.HasCheckConstraint("CK_DeliveryLocation_Latitude", "[Latitude] >= -90 AND [Latitude] <= 90");
                table.HasCheckConstraint("CK_DeliveryLocation_Longitude", "[Longitude] >= -180 AND [Longitude] <= 180");
                table.HasCheckConstraint("CK_DeliveryLocation_Accuracy", "[AccuracyMetres] IS NULL OR [AccuracyMetres] >= 0");
            }
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        entity.Property(x => x.Latitude).HasPrecision(9, 6).IsRequired();
        entity.Property(x => x.Longitude).HasPrecision(9, 6).IsRequired();
        entity.Property(x => x.AccuracyMetres).HasPrecision(8, 2);
        entity.HasIndex(x => new { x.DeliveryId, x.RecordedAtUtc });
        entity.HasIndex(x => x.RecordedAtUtc);
        entity.HasOne(x => x.Delivery).WithMany(x => x.Locations).HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureMilkProduction(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<MilkProduction>();
        entity.ToTable("MilkProduction", table =>
        {
            if (!usesSqlite)
            {
                table.HasCheckConstraint("CK_MilkProduction_BuffaloCount", "[BuffaloCount] > 0");
                table.HasCheckConstraint("CK_MilkProduction_QuantityProduced", "[QuantityProduced] > 0");
            }
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.ProductionAtUtc).IsRequired();
        entity.Property(x => x.BuffaloCount).IsRequired();
        entity.Property(x => x.QuantityProduced).HasPrecision(18, 3).IsRequired();
        entity.Property(x => x.Unit).HasMaxLength(20).IsRequired();
        entity.Property(x => x.Shift).HasMaxLength(40);
        entity.Property(x => x.Remarks).HasMaxLength(1000);
        entity.HasIndex(x => new { x.BranchId, x.ProductionAtUtc });
        entity.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.RecordedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureMilkBatch(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<MilkBatch>();
        entity.ToTable("MilkBatch", table =>
        {
            if (!usesSqlite)
            {
                table.HasCheckConstraint("CK_MilkBatch_QuantityProduced", "[QuantityProduced] > 0");
            }
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.BatchNumber).HasMaxLength(80).IsRequired();
        entity.Property(x => x.ProductionAtUtc).IsRequired();
        entity.Property(x => x.QuantityProduced).HasPrecision(18, 3).IsRequired();
        entity.Property(x => x.Unit).HasMaxLength(20).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.HasIndex(x => new { x.BranchId, x.BatchNumber }).IsUnique();
        entity.HasIndex(x => x.ProductionId).IsUnique();
        entity.HasIndex(x => new { x.BranchId, x.ProductionAtUtc, x.Status });
        entity.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Production).WithMany(x => x.Batches).HasForeignKey(x => x.ProductionId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureMilkUsage(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<MilkUsage>();
        entity.ToTable("MilkUsage", table =>
        {
            if (!usesSqlite)
            {
                table.HasCheckConstraint("CK_MilkUsage_QuantityUsed", "[QuantityUsed] > 0");
            }
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.UsedAtUtc).IsRequired();
        entity.Property(x => x.QuantityUsed).HasPrecision(18, 3).IsRequired();
        entity.Property(x => x.Purpose).HasMaxLength(120).IsRequired();
        entity.Property(x => x.Remarks).HasMaxLength(1000);
        entity.HasIndex(x => new { x.BranchId, x.UsedAtUtc });
        entity.HasIndex(x => new { x.BatchId, x.UsedAtUtc });
        entity.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Batch).WithMany(x => x.Usages).HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.RecordedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureMilkTest(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<MilkTest>();
        entity.ToTable("MilkTest", table =>
        {
            if (!usesSqlite)
            {
                table.HasCheckConstraint(
                    "CK_MilkTest_Lifecycle",
                    "([Status] = 'Requested' AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL AND [CustomerDecision] = 'Pending' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NULL) OR " +
                    "([Status] = 'Completed' AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL AND " +
                    "(([CustomerDecision] = 'Pending' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NULL) OR " +
                    "([CustomerDecision] = 'Confirmed' AND [ConfirmedAtUtc] IS NOT NULL AND [RejectedAtUtc] IS NULL) OR " +
                    "([CustomerDecision] = 'Rejected' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NOT NULL)))");
                table.HasCheckConstraint(
                    "CK_MilkTest_TimestampOrder",
                    "[CompletedAtUtc] IS NULL OR ([CompletedAtUtc] >= [RequestedAtUtc] AND ([ConfirmedAtUtc] IS NULL OR [ConfirmedAtUtc] >= [CompletedAtUtc]) AND ([RejectedAtUtc] IS NULL OR [RejectedAtUtc] >= [CompletedAtUtc]))");
            }
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.CustomerDecision).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.RequestedAtUtc).IsRequired();
        entity.Property(x => x.StaffRemarks).HasMaxLength(1000);
        entity.Property(x => x.CustomerRemarks).HasMaxLength(1000);
        entity.HasIndex(x => x.DeliveryId).IsUnique();
        entity.HasIndex(x => new { x.CustomerId, x.RequestedAtUtc });
        entity.HasIndex(x => new { x.BranchId, x.Status, x.RequestedAtUtc });
        entity.HasOne(x => x.Delivery).WithMany().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.RequestedByUser).WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CompletedByUser).WithMany().HasForeignKey(x => x.CompletedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureMilkTestParameter(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<MilkTestParameter>();
        entity.ToTable("MilkTestParameter");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        entity.Property(x => x.Code).HasMaxLength(80).IsRequired();
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Value).HasPrecision(18, 6).IsRequired();
        entity.Property(x => x.Unit).HasMaxLength(40).IsRequired();
        entity.HasIndex(x => new { x.MilkTestId, x.Code }).IsUnique();
        entity.HasOne(x => x.MilkTest).WithMany(x => x.Parameters).HasForeignKey(x => x.MilkTestId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureMilkTestImage(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<MilkTestImage>();
        entity.ToTable("MilkTestImage", table =>
        {
            if (!usesSqlite)
            {
                table.HasCheckConstraint("CK_MilkTestImage_FileSize", "[FileSize] > 0");
            }
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
        entity.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        entity.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        entity.Property(x => x.FileSize).IsRequired();
        entity.Property(x => x.UploadedAtUtc).IsRequired();
        entity.HasIndex(x => x.StorageKey).IsUnique();
        entity.HasIndex(x => new { x.MilkTestId, x.UploadedAtUtc });
        entity.HasOne(x => x.MilkTest).WithMany(x => x.Images).HasForeignKey(x => x.MilkTestId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.UploadedByUser).WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
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

    private static void ConfigureCamera(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<Camera>();
        entity.ToTable("Camera", table =>
        {
            if (!usesSqlite)
            {
                table.HasCheckConstraint("CK_Camera_DisplayOrder", "[DisplayOrder] >= 0");
            }
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.InternalIdentifier).HasMaxLength(100).IsRequired();
        entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
        entity.HasIndex(x => new { x.BranchId, x.InternalIdentifier }).IsUnique();
        entity.HasIndex(x => new { x.IsActive, x.IsPublic, x.DisplayOrder });
        entity.HasIndex(x => new { x.BranchId, x.IsActive, x.DisplayOrder });
        entity.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureCameraStream(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CameraStream>();
        entity.ToTable("CameraStream");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Protocol).HasConversion<string>().HasMaxLength(20).IsRequired();
        entity.Property(x => x.ProviderCode).HasMaxLength(80).IsRequired();
        entity.Property(x => x.ProviderStreamReference).HasMaxLength(240).IsRequired();
        entity.HasIndex(x => x.CameraId).IsUnique();
        entity.HasOne(x => x.Camera).WithOne(x => x.Stream).HasForeignKey<CameraStream>(x => x.CameraId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureNotificationEvent(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<NotificationEvent>();
        entity.ToTable("NotificationEvent");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        entity.Property(x => x.EventKey).HasMaxLength(200).IsRequired();
        entity.Property(x => x.PayloadJson).HasMaxLength(8000).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.OccurredAtUtc).IsRequired();
        entity.Property(x => x.FailureCode).HasMaxLength(100);
        entity.Property(x => x.FailureMessage).HasMaxLength(1000);
        entity.HasIndex(x => x.EventKey).IsUnique();
        entity.HasIndex(x => new { x.Status, x.OccurredAtUtc });
        entity.HasIndex(x => new { x.UserId, x.OccurredAtUtc });
        entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureNotification(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Notification>();
        entity.ToTable("Notification");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Title).HasMaxLength(240).IsRequired();
        entity.Property(x => x.Body).HasMaxLength(2000).IsRequired();
        entity.Property(x => x.DeepLink).HasMaxLength(500);
        entity.HasIndex(x => x.NotificationEventId).IsUnique();
        entity.HasIndex(x => new { x.UserId, x.ReadAtUtc, x.CreatedAtUtc });
        entity.HasOne(x => x.Event).WithMany(x => x.Notifications).HasForeignKey(x => x.NotificationEventId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureNotificationTemplate(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<NotificationTemplate>();
        entity.ToTable("NotificationTemplate");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Channel).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.Language).HasMaxLength(10).IsRequired();
        entity.Property(x => x.TitleTemplate).HasMaxLength(240);
        entity.Property(x => x.BodyTemplate).HasMaxLength(2000).IsRequired();
        entity.HasIndex(x => new { x.EventType, x.Channel, x.Language }).IsUnique();
        entity.HasIndex(x => new { x.IsActive, x.EventType });
    }

    private static void ConfigureNotificationPreference(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<NotificationPreference>();
        entity.ToTable("NotificationPreference");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Channel).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.HasIndex(x => new { x.UserId, x.EventType, x.Channel }).IsUnique();
        entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureUserDevice(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<UserDevice>();
        entity.ToTable("UserDevice");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.DeviceIdentifierHash).HasMaxLength(64).IsRequired();
        entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        entity.Property(x => x.ProtectedToken).HasMaxLength(2000).IsRequired();
        entity.Property(x => x.Platform).HasMaxLength(30).IsRequired();
        entity.Property(x => x.DeviceName).HasMaxLength(160);
        entity.Property(x => x.RegisteredAtUtc).IsRequired();
        entity.HasIndex(x => new { x.UserId, x.DeviceIdentifierHash }).IsUnique();
        entity.HasIndex(x => x.TokenHash)
            .IsUnique()
            .HasDatabaseName("UX_UserDevice_ActiveTokenHash")
            .HasFilter(usesSqlite ? "\"IsActive\" = 1" : "[IsActive] = 1");
        entity.HasIndex(x => new { x.UserId, x.IsActive });
        entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureNotificationDelivery(ModelBuilder modelBuilder, bool usesSqlite)
    {
        var entity = modelBuilder.Entity<NotificationDelivery>();
        entity.ToTable("NotificationDelivery", table =>
        {
            if (!usesSqlite)
            {
                table.HasCheckConstraint("CK_NotificationDelivery_AttemptCount", "[AttemptCount] >= 0");
            }
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Channel).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.ProviderCode).HasMaxLength(80).IsRequired();
        entity.Property(x => x.DestinationReference).HasMaxLength(500).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.ProviderMessageId).HasMaxLength(240);
        entity.Property(x => x.FailureCode).HasMaxLength(100);
        entity.Property(x => x.FailureMessage).HasMaxLength(1000);
        entity.HasIndex(x => new { x.NotificationId, x.Channel, x.UserDeviceId })
            .IsUnique()
            .HasFilter("[UserDeviceId] IS NOT NULL");
        entity.HasIndex(x => new { x.NotificationId, x.Channel })
            .IsUnique()
            .HasDatabaseName("UX_NotificationDelivery_NonDeviceChannel")
            .HasFilter("[UserDeviceId] IS NULL");
        entity.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
        entity.HasOne(x => x.Notification).WithMany(x => x.Deliveries).HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.UserDevice).WithMany(x => x.Deliveries).HasForeignKey(x => x.UserDeviceId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureNotificationAttempt(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<NotificationAttempt>();
        entity.ToTable("NotificationAttempt");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).UseIdentityColumn();
        ConfigurePublicEntity(entity);
        entity.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(x => x.ProviderMessageId).HasMaxLength(240);
        entity.Property(x => x.FailureCode).HasMaxLength(100);
        entity.Property(x => x.FailureMessage).HasMaxLength(1000);
        entity.Property(x => x.AttemptedAtUtc).IsRequired();
        entity.HasIndex(x => new { x.NotificationDeliveryId, x.AttemptNumber }).IsUnique();
        entity.HasOne(x => x.Delivery).WithMany(x => x.Attempts).HasForeignKey(x => x.NotificationDeliveryId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePublicEntity<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : DoodhDirect.Domain.Common.PublicEntity
    {
        entity.Property(x => x.PublicId).HasDefaultValueSql("NEWSEQUENTIALID()");
        entity.HasIndex(x => x.PublicId).IsUnique();
    }
}
