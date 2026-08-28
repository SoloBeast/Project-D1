using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeInvitationOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmployeeInvitation",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InviteeName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    InviteeMobile = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InviteeEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    RoleCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    BranchId = table.Column<long>(type: "bigint", nullable: true),
                    TokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RegisteredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RegisteredByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledByUserId = table.Column<long>(type: "bigint", nullable: true),
                    LastResentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastResentByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeInvitation", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeInvitation_BranchId_Status",
                schema: "dbo",
                table: "EmployeeInvitation",
                columns: new[] { "BranchId", "Status" },
                filter: "[BranchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeInvitation_CreatedByUserId_CreatedAtUtc",
                schema: "dbo",
                table: "EmployeeInvitation",
                columns: new[] { "CreatedByUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeInvitation_PublicId",
                schema: "dbo",
                table: "EmployeeInvitation",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeInvitation_Status_ExpiresAtUtc",
                schema: "dbo",
                table: "EmployeeInvitation",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeInvitation_TokenHash",
                schema: "dbo",
                table: "EmployeeInvitation",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeInvitation",
                schema: "dbo");
        }
    }
}
