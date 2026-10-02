using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChargeMasterAndOrderChargeSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ChargesTotal",
                schema: "dbo",
                table: "Order",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "Charge",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChargeType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ChargeCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Percentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsUsed = table.Column<bool>(type: "bit", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Charge", x => x.Id);
                    table.CheckConstraint("CK_Charge_Percentage", "[Percentage] > 0 AND [Percentage] <= 100");
                });

            migrationBuilder.CreateTable(
                name: "OrderCharge",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    ChargeType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ChargeCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Percentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    BaseAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderCharge", x => x.Id);
                    table.CheckConstraint("CK_OrderCharge_Amount", "[Amount] >= 0");
                    table.CheckConstraint("CK_OrderCharge_BaseAmount", "[BaseAmount] >= 0");
                    table.CheckConstraint("CK_OrderCharge_Percentage", "[Percentage] > 0");
                    table.ForeignKey(
                        name: "FK_OrderCharge_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "dbo",
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Charge_ChargeCode",
                schema: "dbo",
                table: "Charge",
                column: "ChargeCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Charge_PublicId",
                schema: "dbo",
                table: "Charge",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderCharge_OrderId",
                schema: "dbo",
                table: "OrderCharge",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Charge",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "OrderCharge",
                schema: "dbo");

            migrationBuilder.DropColumn(
                name: "ChargesTotal",
                schema: "dbo",
                table: "Order");
        }
    }
}
