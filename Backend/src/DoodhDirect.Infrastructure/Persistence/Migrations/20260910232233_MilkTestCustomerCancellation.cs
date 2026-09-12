using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MilkTestCustomerCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MilkTest_Lifecycle",
                schema: "dbo",
                table: "MilkTest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MilkTest_TimestampOrder",
                schema: "dbo",
                table: "MilkTest");

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAtUtc",
                schema: "dbo",
                table: "MilkTest",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MilkTest_Lifecycle",
                schema: "dbo",
                table: "MilkTest",
                sql: "([Status] = 'Requested' AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL AND [CustomerDecision] = 'Pending' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NULL AND [CancelledAtUtc] IS NULL) OR ([Status] = 'Requested' AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL AND [CustomerDecision] = 'CustomerCancelled' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NULL AND [CancelledAtUtc] IS NOT NULL) OR ([Status] = 'Completed' AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL AND [CancelledAtUtc] IS NULL AND (([CustomerDecision] = 'Pending' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NULL) OR ([CustomerDecision] = 'Confirmed' AND [ConfirmedAtUtc] IS NOT NULL AND [RejectedAtUtc] IS NULL) OR ([CustomerDecision] = 'Rejected' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NOT NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MilkTest_TimestampOrder",
                schema: "dbo",
                table: "MilkTest",
                sql: "[CompletedAtUtc] IS NULL OR ([CompletedAtUtc] >= [RequestedAtUtc] AND ([ConfirmedAtUtc] IS NULL OR [ConfirmedAtUtc] >= [CompletedAtUtc]) AND ([RejectedAtUtc] IS NULL OR [RejectedAtUtc] >= [CompletedAtUtc])) AND ([CancelledAtUtc] IS NULL OR [CancelledAtUtc] >= [RequestedAtUtc])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MilkTest_Lifecycle",
                schema: "dbo",
                table: "MilkTest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MilkTest_TimestampOrder",
                schema: "dbo",
                table: "MilkTest");

            migrationBuilder.DropColumn(
                name: "CancelledAtUtc",
                schema: "dbo",
                table: "MilkTest");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MilkTest_Lifecycle",
                schema: "dbo",
                table: "MilkTest",
                sql: "([Status] = 'Requested' AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL AND [CustomerDecision] = 'Pending' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NULL) OR ([Status] = 'Completed' AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL AND (([CustomerDecision] = 'Pending' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NULL) OR ([CustomerDecision] = 'Confirmed' AND [ConfirmedAtUtc] IS NOT NULL AND [RejectedAtUtc] IS NULL) OR ([CustomerDecision] = 'Rejected' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NOT NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MilkTest_TimestampOrder",
                schema: "dbo",
                table: "MilkTest",
                sql: "[CompletedAtUtc] IS NULL OR ([CompletedAtUtc] >= [RequestedAtUtc] AND ([ConfirmedAtUtc] IS NULL OR [ConfirmedAtUtc] >= [CompletedAtUtc]) AND ([RejectedAtUtc] IS NULL OR [RejectedAtUtc] >= [CompletedAtUtc]))");
        }
    }
}
