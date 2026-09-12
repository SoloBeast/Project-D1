using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefundReplacementRequestsAndImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RefundReplacementRequest",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    DeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    MilkTestId = table.Column<long>(type: "bigint", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeadlineUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecidedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionRemarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CompletedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletionRemarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundReplacementRequest", x => x.Id);
                    table.CheckConstraint("CK_RefundReplacementRequest_Lifecycle", "([Status] = 'Pending' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Approved' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Rejected' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = 'Completed' AND [DecidedByUserId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_RefundReplacementRequest_TimestampOrder", "([DeadlineUtc] IS NULL OR [DeadlineUtc] > [SubmittedAtUtc]) AND ([DecidedAtUtc] IS NULL OR [DecidedAtUtc] >= [SubmittedAtUtc]) AND ([CompletedAtUtc] IS NULL OR ([DecidedAtUtc] IS NOT NULL AND [CompletedAtUtc] >= [DecidedAtUtc]))");
                    table.ForeignKey(
                        name: "FK_RefundReplacementRequest_Branch_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "dbo",
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundReplacementRequest_Delivery_DeliveryId",
                        column: x => x.DeliveryId,
                        principalSchema: "dbo",
                        principalTable: "Delivery",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundReplacementRequest_MilkTest_MilkTestId",
                        column: x => x.MilkTestId,
                        principalSchema: "dbo",
                        principalTable: "MilkTest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundReplacementRequest_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "dbo",
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundReplacementRequest_User_CompletedByUserId",
                        column: x => x.CompletedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundReplacementRequest_User_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundReplacementRequest_User_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RefundReplacementImage",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    UploadedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundReplacementImage", x => x.Id);
                    table.CheckConstraint("CK_RefundReplacementImage_FileSize", "[FileSize] > 0");
                    table.ForeignKey(
                        name: "FK_RefundReplacementImage_RefundReplacementRequest_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "dbo",
                        principalTable: "RefundReplacementRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundReplacementImage_User_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementImage_PublicId",
                schema: "dbo",
                table: "RefundReplacementImage",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementImage_RequestId_UploadedAtUtc",
                schema: "dbo",
                table: "RefundReplacementImage",
                columns: new[] { "RequestId", "UploadedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementImage_StorageKey",
                schema: "dbo",
                table: "RefundReplacementImage",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementImage_UploadedByUserId",
                schema: "dbo",
                table: "RefundReplacementImage",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_BranchId_Status_SubmittedAtUtc",
                schema: "dbo",
                table: "RefundReplacementRequest",
                columns: new[] { "BranchId", "Status", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_CompletedByUserId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "CompletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_CustomerId_SubmittedAtUtc",
                schema: "dbo",
                table: "RefundReplacementRequest",
                columns: new[] { "CustomerId", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_DecidedByUserId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_DeliveryId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "DeliveryId",
                unique: true,
                filter: "[Status] IN ('Pending', 'Approved')");

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_MilkTestId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "MilkTestId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_OrderId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_PublicId",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundReplacementRequest_RequestNumber",
                schema: "dbo",
                table: "RefundReplacementRequest",
                column: "RequestNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RefundReplacementImage",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "RefundReplacementRequest",
                schema: "dbo");
        }
    }
}
