using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionCancelRefundAndOrderCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderCancellation",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CancelledByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderCancellation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderCancellation_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "dbo",
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionCancelRefundOccurrence",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<long>(type: "bigint", nullable: false),
                    SubscriptionDeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    AllocationAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    WalletIdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionCancelRefundOccurrence", x => x.Id);
                    table.CheckConstraint("CK_SubscriptionCancelRefundOccurrence_Allocation", "[AllocationAmount] >= 0");
                    table.ForeignKey(
                        name: "FK_SubscriptionCancelRefundOccurrence_SubscriptionDelivery_SubscriptionDeliveryId",
                        column: x => x.SubscriptionDeliveryId,
                        principalSchema: "dbo",
                        principalTable: "SubscriptionDelivery",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubscriptionCancelRefundOccurrence_Subscription_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "dbo",
                        principalTable: "Subscription",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderCancellation_OrderId",
                schema: "dbo",
                table: "OrderCancellation",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionCancelRefundOccurrence_SubscriptionDeliveryId",
                schema: "dbo",
                table: "SubscriptionCancelRefundOccurrence",
                column: "SubscriptionDeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionCancelRefundOccurrence_SubscriptionId",
                schema: "dbo",
                table: "SubscriptionCancelRefundOccurrence",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionCancelRefundOccurrence_WalletIdempotencyKey",
                schema: "dbo",
                table: "SubscriptionCancelRefundOccurrence",
                column: "WalletIdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderCancellation",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SubscriptionCancelRefundOccurrence",
                schema: "dbo");
        }
    }
}
