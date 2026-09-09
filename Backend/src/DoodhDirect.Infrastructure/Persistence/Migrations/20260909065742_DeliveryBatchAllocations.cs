using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryBatchAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MilkUsage_DeliveryId_OrderItemId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropIndex(
                name: "IX_MilkUsage_DeliveryId_SubscriptionDeliveryId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.AddColumn<long>(
                name: "DeliveryBatchAllocationId",
                schema: "dbo",
                table: "MilkUsage",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeliveryBatchAllocation",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    QuantityAllocated = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryBatchAllocation", x => x.Id);
                    table.CheckConstraint("CK_DeliveryBatchAllocation_QuantityAllocated", "[QuantityAllocated] > 0");
                    table.ForeignKey(
                        name: "FK_DeliveryBatchAllocation_Delivery_DeliveryId",
                        column: x => x.DeliveryId,
                        principalSchema: "dbo",
                        principalTable: "Delivery",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeliveryBatchAllocation_MilkBatch_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "dbo",
                        principalTable: "MilkBatch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_DeliveryBatchAllocationId",
                schema: "dbo",
                table: "MilkUsage",
                column: "DeliveryBatchAllocationId",
                unique: true,
                filter: "DeliveryBatchAllocationId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_DeliveryId",
                schema: "dbo",
                table: "MilkUsage",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryBatchAllocation_BatchId_DeliveryId",
                schema: "dbo",
                table: "DeliveryBatchAllocation",
                columns: new[] { "BatchId", "DeliveryId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryBatchAllocation_DeliveryId_BatchId",
                schema: "dbo",
                table: "DeliveryBatchAllocation",
                columns: new[] { "DeliveryId", "BatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryBatchAllocation_PublicId",
                schema: "dbo",
                table: "DeliveryBatchAllocation",
                column: "PublicId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MilkUsage_DeliveryBatchAllocation_DeliveryBatchAllocationId",
                schema: "dbo",
                table: "MilkUsage",
                column: "DeliveryBatchAllocationId",
                principalSchema: "dbo",
                principalTable: "DeliveryBatchAllocation",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MilkUsage_DeliveryBatchAllocation_DeliveryBatchAllocationId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropTable(
                name: "DeliveryBatchAllocation",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "IX_MilkUsage_DeliveryBatchAllocationId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropIndex(
                name: "IX_MilkUsage_DeliveryId",
                schema: "dbo",
                table: "MilkUsage");

            migrationBuilder.DropColumn(
                name: "DeliveryBatchAllocationId",
                schema: "dbo",
                table: "MilkUsage");

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
        }
    }
}
