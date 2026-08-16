using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4OneTimeOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Order",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerAddressId = table.Column<long>(type: "bigint", nullable: false),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OrderNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PayableAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BranchCodeSnapshot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BranchNameSnapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddressLabelSnapshot = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    AddressLine1Snapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddressLine2Snapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LocalitySnapshot = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CitySnapshot = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StateSnapshot = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PinCodeSnapshot = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    LandmarkSnapshot = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    DeliveryInstructionsSnapshot = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ContactNameSnapshot = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ContactMobileSnapshot = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LatitudeSnapshot = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    LongitudeSnapshot = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Order", x => x.Id);
                    table.CheckConstraint("CK_Order_DiscountAmount", "[DiscountAmount] >= 0 AND [DiscountAmount] <= [Subtotal]");
                    table.CheckConstraint("CK_Order_Latitude", "[LatitudeSnapshot] >= -90 AND [LatitudeSnapshot] <= 90");
                    table.CheckConstraint("CK_Order_Longitude", "[LongitudeSnapshot] >= -180 AND [LongitudeSnapshot] <= 180");
                    table.CheckConstraint("CK_Order_PayableAmount", "[PayableAmount] >= 0");
                    table.CheckConstraint("CK_Order_Subtotal", "[Subtotal] >= 0");
                    table.ForeignKey(
                        name: "FK_Order_Branch_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "dbo",
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Order_CustomerAddress_CustomerAddressId",
                        column: x => x.CustomerAddressId,
                        principalSchema: "dbo",
                        principalTable: "CustomerAddress",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Order_User_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderItem",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SKU_Snapshot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductNameSnapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UnitOfMeasureSnapshot = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItem", x => x.Id);
                    table.CheckConstraint("CK_OrderItem_LineTotal", "[LineTotal] >= 0");
                    table.CheckConstraint("CK_OrderItem_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_OrderItem_UnitPrice", "[UnitPrice] >= 0");
                    table.ForeignKey(
                        name: "FK_OrderItem_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "dbo",
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrderItem_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "dbo",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Order_BranchId_Status_CreatedAtUtc",
                schema: "dbo",
                table: "Order",
                columns: new[] { "BranchId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Order_CustomerAddressId",
                schema: "dbo",
                table: "Order",
                column: "CustomerAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_Order_CustomerId_CreatedAtUtc",
                schema: "dbo",
                table: "Order",
                columns: new[] { "CustomerId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Order_CustomerId_IdempotencyKey",
                schema: "dbo",
                table: "Order",
                columns: new[] { "CustomerId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Order_OrderNumber",
                schema: "dbo",
                table: "Order",
                column: "OrderNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Order_PublicId",
                schema: "dbo",
                table: "Order",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItem_OrderId_ProductId",
                schema: "dbo",
                table: "OrderItem",
                columns: new[] { "OrderId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItem_ProductId",
                schema: "dbo",
                table: "OrderItem",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderItem",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Order",
                schema: "dbo");
        }
    }
}
