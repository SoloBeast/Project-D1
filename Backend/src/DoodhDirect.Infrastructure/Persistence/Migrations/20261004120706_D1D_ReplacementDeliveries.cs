using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D1D_ReplacementDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReplacement",
                schema: "dbo",
                table: "SubscriptionDelivery",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "RefundReplacementRequestId",
                schema: "dbo",
                table: "SubscriptionDelivery",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplacementReason",
                schema: "dbo",
                table: "SubscriptionDelivery",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReplacesSubscriptionDeliveryId",
                schema: "dbo",
                table: "SubscriptionDelivery",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionDelivery_RefundReplacementRequestId",
                schema: "dbo",
                table: "SubscriptionDelivery",
                column: "RefundReplacementRequestId",
                filter: "[RefundReplacementRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionDelivery_ReplacesSubscriptionDeliveryId",
                schema: "dbo",
                table: "SubscriptionDelivery",
                column: "ReplacesSubscriptionDeliveryId");

            migrationBuilder.AddForeignKey(
                name: "FK_SubscriptionDelivery_RefundReplacementRequest_RefundReplacementRequestId",
                schema: "dbo",
                table: "SubscriptionDelivery",
                column: "RefundReplacementRequestId",
                principalSchema: "dbo",
                principalTable: "RefundReplacementRequest",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubscriptionDelivery_SubscriptionDelivery_ReplacesSubscriptionDeliveryId",
                schema: "dbo",
                table: "SubscriptionDelivery",
                column: "ReplacesSubscriptionDeliveryId",
                principalSchema: "dbo",
                principalTable: "SubscriptionDelivery",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SubscriptionDelivery_RefundReplacementRequest_RefundReplacementRequestId",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.DropForeignKey(
                name: "FK_SubscriptionDelivery_SubscriptionDelivery_ReplacesSubscriptionDeliveryId",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.DropIndex(
                name: "IX_SubscriptionDelivery_RefundReplacementRequestId",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.DropIndex(
                name: "IX_SubscriptionDelivery_ReplacesSubscriptionDeliveryId",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.DropColumn(
                name: "IsReplacement",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.DropColumn(
                name: "RefundReplacementRequestId",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.DropColumn(
                name: "ReplacementReason",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.DropColumn(
                name: "ReplacesSubscriptionDeliveryId",
                schema: "dbo",
                table: "SubscriptionDelivery");
        }
    }
}
