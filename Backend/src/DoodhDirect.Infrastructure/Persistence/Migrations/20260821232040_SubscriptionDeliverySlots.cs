using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionDeliverySlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slot",
                schema: "dbo",
                table: "SubscriptionSchedule",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: false,
                defaultValue: "Morning");

            migrationBuilder.AddColumn<string>(
                name: "Slot",
                schema: "dbo",
                table: "SubscriptionDelivery",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: false,
                defaultValue: "Morning");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Slot",
                schema: "dbo",
                table: "SubscriptionSchedule");

            migrationBuilder.DropColumn(
                name: "Slot",
                schema: "dbo",
                table: "SubscriptionDelivery");
        }
    }
}
