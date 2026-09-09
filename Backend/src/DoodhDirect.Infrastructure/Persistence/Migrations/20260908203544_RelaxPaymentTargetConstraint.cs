using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RelaxPaymentTargetConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Payment_Target",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Payment_Target",
                schema: "dbo",
                table: "Payment",
                sql: "([OrderId] IS NOT NULL AND [SubscriptionId] IS NULL) OR ([OrderId] IS NULL AND [SubscriptionId] IS NOT NULL) OR ([OrderId] IS NULL AND [SubscriptionId] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Payment_Target",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Payment_Target",
                schema: "dbo",
                table: "Payment",
                sql: "([OrderId] IS NOT NULL AND [SubscriptionId] IS NULL) OR ([OrderId] IS NULL AND [SubscriptionId] IS NOT NULL)");
        }
    }
}
