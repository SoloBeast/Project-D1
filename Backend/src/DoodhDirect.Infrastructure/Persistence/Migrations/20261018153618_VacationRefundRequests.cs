using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VacationRefundRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SkipCause",
                schema: "dbo",
                table: "SubscriptionDelivery",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VacationRefundProcessedAtUtc",
                schema: "dbo",
                table: "Subscription",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VacationRefundRequest",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SubscriptionId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DeductionPercentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    GrossPerOccurrence = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OccurrenceCount = table.Column<int>(type: "int", nullable: false),
                    PayableAmountSnapshot = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalEntitlementSnapshot = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    PaymentId = table.Column<long>(type: "bigint", nullable: false),
                    CalculationBasisJson = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionRemarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CompletedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletionRemarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VacationRefundRequest", x => x.Id);
                    table.CheckConstraint("CK_VacationRefundRequest_Amounts", "[GrossPerOccurrence] > 0 AND [TotalAmount] > 0 AND [OccurrenceCount] > 0 AND [PayableAmountSnapshot] > 0 AND [TotalEntitlementSnapshot] > 0 AND [DeductionPercentage] = 0");
                    table.CheckConstraint("CK_VacationRefundRequest_Lifecycle", "([Status] = 'Pending' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Approved' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Rejected' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Completed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_VacationRefundRequest_Subscription_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "dbo",
                        principalTable: "Subscription",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VacationRefundOccurrence",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VacationRefundRequestId = table.Column<long>(type: "bigint", nullable: false),
                    SubscriptionDeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    AllocationAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VacationRefundOccurrence", x => x.Id);
                    table.CheckConstraint("CK_VacationRefundOccurrence_Allocation", "[AllocationAmount] >= 0");
                    table.ForeignKey(
                        name: "FK_VacationRefundOccurrence_SubscriptionDelivery_SubscriptionDeliveryId",
                        column: x => x.SubscriptionDeliveryId,
                        principalSchema: "dbo",
                        principalTable: "SubscriptionDelivery",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VacationRefundOccurrence_VacationRefundRequest_VacationRefundRequestId",
                        column: x => x.VacationRefundRequestId,
                        principalSchema: "dbo",
                        principalTable: "VacationRefundRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionDelivery_SkipCause",
                schema: "dbo",
                table: "SubscriptionDelivery",
                column: "SkipCause",
                filter: "[SkipCause] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VacationRefundOccurrence_SubscriptionDeliveryId",
                schema: "dbo",
                table: "VacationRefundOccurrence",
                column: "SubscriptionDeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VacationRefundOccurrence_VacationRefundRequestId",
                schema: "dbo",
                table: "VacationRefundOccurrence",
                column: "VacationRefundRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_VacationRefundRequest_BranchId_Status",
                schema: "dbo",
                table: "VacationRefundRequest",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VacationRefundRequest_CustomerId_Status",
                schema: "dbo",
                table: "VacationRefundRequest",
                columns: new[] { "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VacationRefundRequest_PublicId",
                schema: "dbo",
                table: "VacationRefundRequest",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VacationRefundRequest_RequestNumber",
                schema: "dbo",
                table: "VacationRefundRequest",
                column: "RequestNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VacationRefundRequest_SubscriptionId",
                schema: "dbo",
                table: "VacationRefundRequest",
                column: "SubscriptionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VacationRefundOccurrence",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "VacationRefundRequest",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "IX_SubscriptionDelivery_SkipCause",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.DropColumn(
                name: "SkipCause",
                schema: "dbo",
                table: "SubscriptionDelivery");

            migrationBuilder.DropColumn(
                name: "VacationRefundProcessedAtUtc",
                schema: "dbo",
                table: "Subscription");
        }
    }
}
