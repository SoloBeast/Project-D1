using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class D1D_CancelledRemedyStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest",
                sql: "([Status] = 'Pending' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'AwaitingCustomerChoice' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Approved' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Rejected' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Completed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = 'Failed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = 'Cancelled' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundReplacementRequest_Lifecycle",
                schema: "dbo",
                table: "RefundReplacementRequest",
                sql: "([Status] = 'Pending' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'AwaitingCustomerChoice' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Approved' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Rejected' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Completed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = 'Failed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL)");
        }
    }
}
