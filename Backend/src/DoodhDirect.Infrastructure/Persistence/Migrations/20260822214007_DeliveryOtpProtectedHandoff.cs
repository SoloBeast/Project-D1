using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryOtpProtectedHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProtectedCode",
                schema: "dbo",
                table: "DeliveryOtp",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentAtUtc",
                schema: "dbo",
                table: "DeliveryOtp",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectedCode",
                schema: "dbo",
                table: "DeliveryOtp");

            migrationBuilder.DropColumn(
                name: "SentAtUtc",
                schema: "dbo",
                table: "DeliveryOtp");
        }
    }
}
