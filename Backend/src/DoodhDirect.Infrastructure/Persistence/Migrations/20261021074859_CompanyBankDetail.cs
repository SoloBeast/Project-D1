using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompanyBankDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyBankDetail",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SingletonGuard = table.Column<int>(type: "int", nullable: false),
                    AccountHolderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProtectedAccountNumber = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AccountLast4 = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Ifsc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyBankDetail", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyBankDetail_PublicId",
                schema: "dbo",
                table: "CompanyBankDetail",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyBankDetail_SingletonGuard",
                schema: "dbo",
                table: "CompanyBankDetail",
                column: "SingletonGuard",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyBankDetail",
                schema: "dbo");
        }
    }
}
