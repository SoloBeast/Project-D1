using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D1D_SupersededRemedyStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.AddColumn<DateTime>(
                name: "RemediatedAtUtc",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RemediatedByUserId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemediationReason",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SupersedesRequestId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_SupersedesRequestId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "SupersedesRequestId",
                unique: true,
                filter: "[SupersedesRequestId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest",
                sql: "([Status] = 'Pending' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'AwaitingCustomerChoice' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Approved' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Rejected' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Completed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = 'Failed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = 'Cancelled' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = 'Superseded' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [RemediatedByUserId] IS NOT NULL AND [RemediatedAtUtc] IS NOT NULL AND [RemediationReason] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_RefundReplacementRequest_RefundReplacementRequest_SupersedesRequestId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "SupersedesRequestId",
                principalSchema: "dbo",
                principalTable: "RefundReplacementRequest",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RefundReplacementRequest_RefundReplacementRequest_SupersedesRequestId",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropIndex(
                name: "IX_RefundReplacementRequest_SupersedesRequestId",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "RemediatedAtUtc",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "RemediatedByUserId",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "RemediationReason",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "SupersedesRequestId",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest",
                sql: "([Status] = 'Pending' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'AwaitingCustomerChoice' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Approved' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Rejected' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Completed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = 'Failed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = 'Cancelled' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL)");
        }
    }
}
