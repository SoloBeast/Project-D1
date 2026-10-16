using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D1A_DeliveryFailTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefundReplacementRequest_DeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.AlterColumn<long>(
                name: "OrderId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFailDeductionPercentageSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveryFailWindowHoursSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FailedAtUtc",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureReasonSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SubscriptionDeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_DeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "DeliveryId",
                unique: true,
                filter: "[Status] IN ('Pending', 'Approved', 'AwaitingCustomerChoice')");

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_SubscriptionDeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "SubscriptionDeliveryId",
                filter: "[SubscriptionDeliveryId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundReplacementRequest_ExactlyOneSource",
                schema: "dbo",
                table: "RefundReplacementRequest",
                sql: "(([OrderId] IS NULL AND [SubscriptionDeliveryId] IS NOT NULL) OR ([OrderId] IS NOT NULL AND [SubscriptionDeliveryId] IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest",
                sql: "([Status] = 'Pending' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'AwaitingCustomerChoice' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Approved' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Rejected' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Completed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_RefundReplacementRequest_SubscriptionDelivery_SubscriptionDeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest",
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
                name: "FK_RefundReplacementRequest_SubscriptionDelivery_SubscriptionDeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropIndex(
                name: "IX_RefundReplacementRequest_DeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropIndex(
                name: "IX_RefundReplacementRequest_SubscriptionDeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundReplacementRequest_ExactlyOneSource",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "DeliveryFailDeductionPercentageSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "DeliveryFailWindowHoursSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "FailedAtUtc",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "FailureReasonSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "SubscriptionDeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.AlterColumn<long>(
                name: "OrderId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_DeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "DeliveryId",
                unique: true,
                filter: "[Status] IN ('Pending', 'Approved')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest",
                sql: "([Status] = 'Pending' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Approved' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Rejected' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Completed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL)");
        }
    }
}
