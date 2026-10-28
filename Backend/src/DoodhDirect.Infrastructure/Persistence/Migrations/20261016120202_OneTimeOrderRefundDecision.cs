using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OneTimeOrderRefundDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AppliedDeductionPercentage",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeductionWaived",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DeductionWaiverReason",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedDeductionPercentage",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "DeductionWaived",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "DeductionWaiverReason",
                schema: "dbo",
                table: "RefundReplacementRequest");
        }
    }
}
