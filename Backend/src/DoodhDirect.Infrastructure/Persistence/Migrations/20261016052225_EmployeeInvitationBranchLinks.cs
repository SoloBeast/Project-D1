using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeInvitationBranchLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmployeeInvitationBranch",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InvitationId = table.Column<long>(type: "bigint", nullable: false),
                    BranchId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeInvitationBranch", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeInvitationBranch_EmployeeInvitation_InvitationId",
                        column: x => x.InvitationId,
                        principalSchema: "dbo",
                        principalTable: "EmployeeInvitation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeInvitationBranch_BranchId",
                schema: "dbo",
                table: "EmployeeInvitationBranch",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeInvitationBranch_InvitationId_BranchId",
                schema: "dbo",
                table: "EmployeeInvitationBranch",
                columns: new[] { "InvitationId", "BranchId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeInvitationBranch",
                schema: "dbo");
        }
    }
}
