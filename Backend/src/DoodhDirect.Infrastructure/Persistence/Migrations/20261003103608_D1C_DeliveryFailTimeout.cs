using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D1C_DeliveryFailTimeout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_Status_DeadlineUtc",
                schema: "dbo",
                table: "RefundReplacementRequest",
                columns: new[] { "Status", "DeadlineUtc" },
                filter: "[Status] = 'AwaitingCustomerChoice'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefundReplacementRequest_Status_DeadlineUtc",
                schema: "dbo",
                table: "RefundReplacementRequest");
        }
    }
}
