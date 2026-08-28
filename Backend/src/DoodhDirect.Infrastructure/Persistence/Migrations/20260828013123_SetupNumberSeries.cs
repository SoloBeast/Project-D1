using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SetupNumberSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveryNumber",
                schema: "dbo",
                table: "Delivery",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerNumber",
                schema: "dbo",
                table: "CustomerProfile",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BranchNumber",
                schema: "dbo",
                table: "Branch",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NumberSeries",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Template = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    StartingNumber = table.Column<long>(type: "bigint", nullable: false),
                    LastUsedNumber = table.Column<long>(type: "bigint", nullable: false),
                    IncrementBy = table.Column<int>(type: "int", nullable: false),
                    ResetPolicy = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSeries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Delivery_DeliveryNumber",
                schema: "dbo",
                table: "Delivery",
                column: "DeliveryNumber",
                unique: true,
                filter: "[DeliveryNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerProfile_CustomerNumber",
                schema: "dbo",
                table: "CustomerProfile",
                column: "CustomerNumber",
                unique: true,
                filter: "[CustomerNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Branch_BranchNumber",
                schema: "dbo",
                table: "Branch",
                column: "BranchNumber",
                unique: true,
                filter: "[BranchNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NumberSeries_Code",
                schema: "dbo",
                table: "NumberSeries",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NumberSeries_PublicId",
                schema: "dbo",
                table: "NumberSeries",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NumberSeries",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "IX_Delivery_DeliveryNumber",
                schema: "dbo",
                table: "Delivery");

            migrationBuilder.DropIndex(
                name: "IX_CustomerProfile_CustomerNumber",
                schema: "dbo",
                table: "CustomerProfile");

            migrationBuilder.DropIndex(
                name: "IX_Branch_BranchNumber",
                schema: "dbo",
                table: "Branch");

            migrationBuilder.DropColumn(
                name: "DeliveryNumber",
                schema: "dbo",
                table: "Delivery");

            migrationBuilder.DropColumn(
                name: "CustomerNumber",
                schema: "dbo",
                table: "CustomerProfile");

            migrationBuilder.DropColumn(
                name: "BranchNumber",
                schema: "dbo",
                table: "Branch");
        }
    }
}
