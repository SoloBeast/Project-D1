using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SplitPaymentPlanGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PaymentGroupId",
                schema: "dbo",
                table: "Payment",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payment_PaymentGroupId",
                schema: "dbo",
                table: "Payment",
                column: "PaymentGroupId",
                filter: "[PaymentGroupId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_PaymentGroupId_Method",
                schema: "dbo",
                table: "Payment",
                columns: new[] { "PaymentGroupId", "Method" },
                unique: true,
                filter: "[PaymentGroupId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payment_PaymentGroupId",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.DropIndex(
                name: "IX_Payment_PaymentGroupId_Method",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "PaymentGroupId",
                schema: "dbo",
                table: "Payment");
        }
    }
}
