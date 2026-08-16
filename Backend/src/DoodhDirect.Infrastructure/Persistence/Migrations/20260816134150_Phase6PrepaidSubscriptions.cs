using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase6PrepaidSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payment_OrderId",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.AddColumn<long>(
                name: "SubscriptionId",
                schema: "dbo",
                table: "WalletTransaction",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "OrderId",
                schema: "dbo",
                table: "Payment",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "SubscriptionId",
                schema: "dbo",
                table: "Payment",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Subscription",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerAddressId = table.Column<long>(type: "bigint", nullable: false),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PayableAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalEntitlement = table.Column<int>(type: "int", nullable: false),
                    UsedEntitlement = table.Column<int>(type: "int", nullable: false),
                    ProductSkuSnapshot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductNameSnapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UnitOfMeasureSnapshot = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BranchCodeSnapshot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BranchNameSnapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddressSnapshot = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ActivatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PausedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscription", x => x.Id);
                    table.CheckConstraint("CK_Subscription_Dates", "[EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_Subscription_Entitlement", "[TotalEntitlement] > 0 AND [UsedEntitlement] >= 0 AND [UsedEntitlement] <= [TotalEntitlement]");
                    table.CheckConstraint("CK_Subscription_PayableAmount", "[PayableAmount] > 0");
                    table.CheckConstraint("CK_Subscription_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_Subscription_Branch_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "dbo",
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscription_CustomerAddress_CustomerAddressId",
                        column: x => x.CustomerAddressId,
                        principalSchema: "dbo",
                        principalTable: "CustomerAddress",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscription_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "dbo",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscription_User_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionDelivery",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<long>(type: "bigint", nullable: false),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    BranchCodeSnapshot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BranchNameSnapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddressSnapshot = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    StatusChangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionDelivery", x => x.Id);
                    table.CheckConstraint("CK_SubscriptionDelivery_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_SubscriptionDelivery_Branch_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "dbo",
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubscriptionDelivery_Subscription_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "dbo",
                        principalTable: "Subscription",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionSchedule",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<long>(type: "bigint", nullable: false),
                    DayOfWeek = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionSchedule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionSchedule_Subscription_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "dbo",
                        principalTable: "Subscription",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransaction_SubscriptionId",
                schema: "dbo",
                table: "WalletTransaction",
                column: "SubscriptionId",
                filter: "[SubscriptionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_OrderId",
                schema: "dbo",
                table: "Payment",
                column: "OrderId",
                filter: "[OrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_SubscriptionId",
                schema: "dbo",
                table: "Payment",
                column: "SubscriptionId",
                filter: "[SubscriptionId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Payment_Target",
                schema: "dbo",
                table: "Payment",
                sql: "([OrderId] IS NOT NULL AND [SubscriptionId] IS NULL) OR ([OrderId] IS NULL AND [SubscriptionId] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Subscription_BranchId_Status_StartDate",
                schema: "dbo",
                table: "Subscription",
                columns: new[] { "BranchId", "Status", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscription_CustomerAddressId",
                schema: "dbo",
                table: "Subscription",
                column: "CustomerAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscription_CustomerId_IdempotencyKey",
                schema: "dbo",
                table: "Subscription",
                columns: new[] { "CustomerId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscription_CustomerId_Status_CreatedAtUtc",
                schema: "dbo",
                table: "Subscription",
                columns: new[] { "CustomerId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscription_ProductId",
                schema: "dbo",
                table: "Subscription",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscription_PublicId",
                schema: "dbo",
                table: "Subscription",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionDelivery_BranchId",
                schema: "dbo",
                table: "SubscriptionDelivery",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionDelivery_PublicId",
                schema: "dbo",
                table: "SubscriptionDelivery",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionDelivery_Status_ScheduledDate",
                schema: "dbo",
                table: "SubscriptionDelivery",
                columns: new[] { "Status", "ScheduledDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionDelivery_SubscriptionId_ScheduledDate",
                schema: "dbo",
                table: "SubscriptionDelivery",
                columns: new[] { "SubscriptionId", "ScheduledDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionSchedule_SubscriptionId_DayOfWeek",
                schema: "dbo",
                table: "SubscriptionSchedule",
                columns: new[] { "SubscriptionId", "DayOfWeek" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Payment_Subscription_SubscriptionId",
                schema: "dbo",
                table: "Payment",
                column: "SubscriptionId",
                principalSchema: "dbo",
                principalTable: "Subscription",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WalletTransaction_Subscription_SubscriptionId",
                schema: "dbo",
                table: "WalletTransaction",
                column: "SubscriptionId",
                principalSchema: "dbo",
                principalTable: "Subscription",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payment_Subscription_SubscriptionId",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.DropForeignKey(
                name: "FK_WalletTransaction_Subscription_SubscriptionId",
                schema: "dbo",
                table: "WalletTransaction");

            migrationBuilder.DropTable(
                name: "SubscriptionDelivery",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SubscriptionSchedule",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Subscription",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "IX_WalletTransaction_SubscriptionId",
                schema: "dbo",
                table: "WalletTransaction");

            migrationBuilder.DropIndex(
                name: "IX_Payment_OrderId",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.DropIndex(
                name: "IX_Payment_SubscriptionId",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Payment_Target",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "SubscriptionId",
                schema: "dbo",
                table: "WalletTransaction");

            migrationBuilder.DropColumn(
                name: "SubscriptionId",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.AlterColumn<long>(
                name: "OrderId",
                schema: "dbo",
                table: "Payment",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payment_OrderId",
                schema: "dbo",
                table: "Payment",
                column: "OrderId");
        }
    }
}
