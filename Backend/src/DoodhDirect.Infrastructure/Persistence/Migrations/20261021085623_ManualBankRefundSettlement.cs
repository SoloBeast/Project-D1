using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualBankRefundSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ManualBankAccountLast4Snapshot",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualBankHolderNameSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualBankIfscSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualBankNameSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualBankProtectedAccountNumber",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ManualBankReservedAmount",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualBankReservedCurrency",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualBankVoidReason",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ManualBankVoidedAtUtc",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ManualBankVoidedByUserId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementMethod",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Unspecified");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManualBankAccountLast4Snapshot",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "ManualBankHolderNameSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "ManualBankIfscSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "ManualBankNameSnapshot",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "ManualBankProtectedAccountNumber",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "ManualBankReservedAmount",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "ManualBankReservedCurrency",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "ManualBankVoidReason",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "ManualBankVoidedAtUtc",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "ManualBankVoidedByUserId",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "SettlementMethod",
                schema: "dbo",
                table: "RefundReplacementRequest");
        }
    }
}
