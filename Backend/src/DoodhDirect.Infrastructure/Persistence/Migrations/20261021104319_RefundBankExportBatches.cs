using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefundBankExportBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RefundBankExportBatch",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceAccountHolderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceBankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceProtectedAccountNumber = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SourceAccountLast4 = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SourceIfsc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundBankExportBatch", x => x.Id);
                    table.CheckConstraint("CK_RefundBankExportBatch_Lifecycle", "([Status] = 'Claimed' AND [GeneratedAtUtc] IS NULL AND [SubmittedAtUtc] IS NULL) OR ([Status] = 'Generated' AND [GeneratedAtUtc] IS NOT NULL AND [SubmittedAtUtc] IS NULL AND [ClosedAtUtc] IS NULL AND [VoidedAtUtc] IS NULL) OR ([Status] = 'Submitted' AND [GeneratedAtUtc] IS NOT NULL AND [SubmittedAtUtc] IS NOT NULL AND [ClosedAtUtc] IS NULL AND [VoidedAtUtc] IS NULL) OR ([Status] = 'Closed' AND [ClosedAtUtc] IS NOT NULL AND [VoidedAtUtc] IS NULL) OR ([Status] = 'Voided' AND [VoidedAtUtc] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "RefundBankExportItem",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    RequestNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OrderNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CustomerDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    BeneficiaryAccountHolderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BeneficiaryBankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BeneficiaryProtectedAccountNumber = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    BeneficiaryAccountLast4 = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BeneficiaryIfsc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    SourceAccountHolderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceBankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceProtectedAccountNumber = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SourceAccountLast4 = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SourceIfsc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ClaimedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ClaimedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaidByUserId = table.Column<long>(type: "bigint", nullable: true),
                    TransferReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    VoidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VoidedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ReleaseReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReleasedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleasedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundBankExportItem", x => x.Id);
                    table.CheckConstraint("CK_RefundBankExportItem_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_RefundBankExportItem_RefundBankExportBatch_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "dbo",
                        principalTable: "RefundBankExportBatch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundBankExportItem_RefundReplacementRequest_TicketId",
                        column: x => x.TicketId,
                        principalSchema: "dbo",
                        principalTable: "RefundReplacementRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RefundBankExportItemReservation",
                schema: "dbo",
                columns: table => new
                {
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    RefundId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundBankExportItemReservation", x => new { x.ItemId, x.RefundId });
                    table.ForeignKey(
                        name: "FK_RefundBankExportItemReservation_RefundBankExportItem_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "dbo",
                        principalTable: "RefundBankExportItem",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundBankExportItemReservation_Refund_RefundId",
                        column: x => x.RefundId,
                        principalSchema: "dbo",
                        principalTable: "Refund",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefundBankExportBatch_BatchNumber",
                schema: "dbo",
                table: "RefundBankExportBatch",
                column: "BatchNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundBankExportBatch_PublicId",
                schema: "dbo",
                table: "RefundBankExportBatch",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundBankExportItem_BatchId",
                schema: "dbo",
                table: "RefundBankExportItem",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundBankExportItem_PublicId",
                schema: "dbo",
                table: "RefundBankExportItem",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundBankExportItem_RequestNumber",
                schema: "dbo",
                table: "RefundBankExportItem",
                column: "RequestNumber");

            migrationBuilder.CreateIndex(
                name: "IX_RefundBankExportItem_TicketId",
                schema: "dbo",
                table: "RefundBankExportItem",
                column: "TicketId",
                unique: true,
                filter: "[Status] IN ('Claimed', 'Generated', 'Submitted')");

            migrationBuilder.CreateIndex(
                name: "IX_RefundBankExportItemReservation_RefundId",
                schema: "dbo",
                table: "RefundBankExportItemReservation",
                column: "RefundId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RefundBankExportItemReservation",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "RefundBankExportItem",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "RefundBankExportBatch",
                schema: "dbo");
        }
    }
}
