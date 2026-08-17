using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase12ReportIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_UserRole_BranchId_UserId",
                schema: "dbo",
                table: "UserRole",
                columns: new[] { "BranchId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_User_UserType_CreatedAtUtc_Id",
                schema: "dbo",
                table: "User",
                columns: new[] { "UserType", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscription_BranchId_CreatedAtUtc_Id",
                schema: "dbo",
                table: "Subscription",
                columns: new[] { "BranchId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Payment_CustomerId_CreatedAtUtc_Id",
                schema: "dbo",
                table: "Payment",
                columns: new[] { "CustomerId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Payment_Status_CreatedAtUtc_Id",
                schema: "dbo",
                table: "Payment",
                columns: new[] { "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_CreatedAtUtc_Id",
                schema: "dbo",
                table: "AuditLog",
                columns: new[] { "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_UserId_CreatedAtUtc_Id",
                schema: "dbo",
                table: "AuditLog",
                columns: new[] { "UserId", "CreatedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserRole_BranchId_UserId",
                schema: "dbo",
                table: "UserRole");

            migrationBuilder.DropIndex(
                name: "IX_User_UserType_CreatedAtUtc_Id",
                schema: "dbo",
                table: "User");

            migrationBuilder.DropIndex(
                name: "IX_Subscription_BranchId_CreatedAtUtc_Id",
                schema: "dbo",
                table: "Subscription");

            migrationBuilder.DropIndex(
                name: "IX_Payment_CustomerId_CreatedAtUtc_Id",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.DropIndex(
                name: "IX_Payment_Status_CreatedAtUtc_Id",
                schema: "dbo",
                table: "Payment");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_CreatedAtUtc_Id",
                schema: "dbo",
                table: "AuditLog");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_UserId_CreatedAtUtc_Id",
                schema: "dbo",
                table: "AuditLog");
        }
    }
}
