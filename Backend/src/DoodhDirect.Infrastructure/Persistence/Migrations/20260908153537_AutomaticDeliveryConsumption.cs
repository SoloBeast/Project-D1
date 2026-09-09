using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticDeliveryConsumption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "BatchId",
                schema: "dbo",
                table: "MilkUsage",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "DeliveryId",
                schema: "dbo",
                table: "MilkUsage",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryNumber",
                schema: "dbo",
                table: "MilkUsage",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OrderId",
                schema: "dbo",
                table: "MilkUsage",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OrderItemId",
                schema: "dbo",
                table: "MilkUsage",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrderNumber",
                schema: "dbo",
                table: "MilkUsage",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductName",
                schema: "dbo",
                table: "MilkUsage",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "dbo",
                table: "MilkUsage",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<long>(
                name: "SubscriptionDeliveryId",
                schema: "dbo",
                table: "MilkUsage",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                schema: "dbo",
                table: "MilkUsage",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "L");

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_DeliveryId_OrderItemId",
                schema: "dbo",
                table: "MilkUsage",
                columns: new[] { "DeliveryId", "OrderItemId" },
                unique: true,
                filter: "DeliveryId IS NOT NULL AND OrderItemId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_DeliveryId_SubscriptionDeliveryId",
                schema: "dbo",
                table: "MilkUsage",
                columns: new[] { "DeliveryId", "SubscriptionDeliveryId" },
                unique: true,
                filter: "DeliveryId IS NOT NULL AND SubscriptionDeliveryId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_OrderId",
                schema: "dbo",
                table: "MilkUsage",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_OrderItemId",
                schema: "dbo",
                table: "MilkUsage",
                column: "OrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_Source",
                schema: "dbo",
                table: "MilkUsage",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_SubscriptionDeliveryId",
                schema: "dbo",
                table: "MilkUsage",
                column: "SubscriptionDeliveryId");

            migrationBuilder.AddForeignKey(
                name: "FK_MilkUsage_Delivery_DeliveryId",
                schema: "dbo",
                table: "MilkUsage",
                column: "DeliveryId",
                principalSchema: "dbo",
                principalTable: "Delivery",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MilkUsage_OrderItem_OrderItemId",
                schema: "dbo",
                table: "MilkUsage",
                column: "OrderItemId",
                principalSchema: "dbo",
                principalTable: "OrderItem",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MilkUsage_Order_OrderId",
                schema: "dbo",
                table: "MilkUsage",
                column: "OrderId",
                principalSchema: "dbo",
                principalTable: "Order",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MilkUsage_SubscriptionDelivery_SubscriptionDeliveryId",
                schema: "dbo",
                table: "MilkUsage",
                column: "SubscriptionDeliveryId",
                principalSchema: "dbo",
                principalTable: "SubscriptionDelivery",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MilkUsage_Delivery_DeliveryId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropForeignKey(
                name: "FK_MilkUsage_OrderItem_OrderItemId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropForeignKey(
                name: "FK_MilkUsage_Order_OrderId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropForeignKey(
                name: "FK_MilkUsage_SubscriptionDelivery_SubscriptionDeliveryId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropIndex(
                name: "IX_MilkUsage_DeliveryId_OrderItemId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropIndex(
                name: "IX_MilkUsage_DeliveryId_SubscriptionDeliveryId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropIndex(
                name: "IX_MilkUsage_OrderId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropIndex(
                name: "IX_MilkUsage_OrderItemId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropIndex(
                name: "IX_MilkUsage_Source",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropIndex(
                name: "IX_MilkUsage_SubscriptionDeliveryId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropColumn(
                name: "DeliveryId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropColumn(
                name: "DeliveryNumber",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropColumn(
                name: "OrderId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropColumn(
                name: "OrderItemId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropColumn(
                name: "OrderNumber",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropColumn(
                name: "ProductName",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropColumn(
                name: "SubscriptionDeliveryId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropColumn(
                name: "Unit",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.AlterColumn<long>(
                name: "BatchId",
                schema: "dbo",
                table: "MilkUsage",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}
