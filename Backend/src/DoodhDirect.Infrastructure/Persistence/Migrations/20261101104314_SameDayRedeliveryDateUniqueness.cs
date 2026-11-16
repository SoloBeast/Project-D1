using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SameDayRedeliveryDateUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SubscriptionDelivery_SubscriptionId_ScheduledDate",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionDelivery_SubscriptionId_ScheduledDate_IsReplacement",
                schema: "dbo",
                table: "SubscriptionDelivery",
                columns: new[] { "SubscriptionId", "ScheduledDate", "IsReplacement" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SubscriptionDelivery_SubscriptionId_ScheduledDate_IsReplacement",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionDelivery_SubscriptionId_ScheduledDate",
                schema: "dbo",
                table: "SubscriptionDelivery",
                columns: new[] { "SubscriptionId", "ScheduledDate" },
                unique: true);
        }
    }
}
