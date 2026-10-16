using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D1B_CustomerChoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerChoice",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CustomerChoiceAtUtc",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "dbo",
                table: "RefundReplacementRequest",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomerChoice",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "CustomerChoiceAtUtc",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "dbo",
                table: "RefundReplacementRequest");
        }
    }
}
