using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderCancellationRefundSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AppliedDeductionPercentage",
                schema: "dbo",
                table: "OrderCancellation",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeductionAmount",
                schema: "dbo",
                table: "OrderCancellation",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GrossBasis",
                schema: "dbo",
                table: "OrderCancellation",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RefundAmount",
                schema: "dbo",
                table: "OrderCancellation",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedDeductionPercentage",
                schema: "dbo",
                table: "OrderCancellation");

            migrationBuilder.DropColumn(
                name: "DeductionAmount",
                schema: "dbo",
                table: "OrderCancellation");

            migrationBuilder.DropColumn(
                name: "GrossBasis",
                schema: "dbo",
                table: "OrderCancellation");

            migrationBuilder.DropColumn(
                name: "RefundAmount",
                schema: "dbo",
                table: "OrderCancellation");
        }
    }
}
