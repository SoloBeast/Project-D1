using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase9DoorstepTesting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MilkTest",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    RequestedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CompletedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StaffRemarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CustomerDecision = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CustomerRemarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MilkTest", x => x.Id);
                    table.CheckConstraint("CK_MilkTest_Lifecycle", "([Status] = 'Requested' AND [CompletedByUserId] IS NULL AND [CompletedAtUtc] IS NULL AND [CustomerDecision] = 'Pending' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NULL) OR ([Status] = 'Completed' AND [CompletedByUserId] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL AND (([CustomerDecision] = 'Pending' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NULL) OR ([CustomerDecision] = 'Confirmed' AND [ConfirmedAtUtc] IS NOT NULL AND [RejectedAtUtc] IS NULL) OR ([CustomerDecision] = 'Rejected' AND [ConfirmedAtUtc] IS NULL AND [RejectedAtUtc] IS NOT NULL)))");
                    table.CheckConstraint("CK_MilkTest_TimestampOrder", "[CompletedAtUtc] IS NULL OR ([CompletedAtUtc] >= [RequestedAtUtc] AND ([ConfirmedAtUtc] IS NULL OR [ConfirmedAtUtc] >= [CompletedAtUtc]) AND ([RejectedAtUtc] IS NULL OR [RejectedAtUtc] >= [CompletedAtUtc]))");
                    table.ForeignKey(
                        name: "FK_MilkTest_Branch_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "dbo",
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MilkTest_Delivery_DeliveryId",
                        column: x => x.DeliveryId,
                        principalSchema: "dbo",
                        principalTable: "Delivery",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MilkTest_User_CompletedByUserId",
                        column: x => x.CompletedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MilkTest_User_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MilkTest_User_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MilkTestImage",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MilkTestId = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_MilkTestImage", x => x.Id);
                    table.CheckConstraint("CK_MilkTestImage_FileSize", "[FileSize] > 0");
                    table.ForeignKey(
                        name: "FK_MilkTestImage_MilkTest_MilkTestId",
                        column: x => x.MilkTestId,
                        principalSchema: "dbo",
                        principalTable: "MilkTest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MilkTestImage_User_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MilkTestParameter",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MilkTestId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MilkTestParameter", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MilkTestParameter_MilkTest_MilkTestId",
                        column: x => x.MilkTestId,
                        principalSchema: "dbo",
                        principalTable: "MilkTest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MilkTest_BranchId_Status_RequestedAtUtc",
                schema: "dbo",
                table: "MilkTest",
                columns: new[] { "BranchId", "Status", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MilkTest_CompletedByUserId",
                schema: "dbo",
                table: "MilkTest",
                column: "CompletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MilkTest_CustomerId_RequestedAtUtc",
                schema: "dbo",
                table: "MilkTest",
                columns: new[] { "CustomerId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MilkTest_DeliveryId",
                schema: "dbo",
                table: "MilkTest",
                column: "DeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MilkTest_PublicId",
                schema: "dbo",
                table: "MilkTest",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MilkTest_RequestedByUserId",
                schema: "dbo",
                table: "MilkTest",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MilkTestImage_MilkTestId_UploadedAtUtc",
                schema: "dbo",
                table: "MilkTestImage",
                columns: new[] { "MilkTestId", "UploadedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MilkTestImage_PublicId",
                schema: "dbo",
                table: "MilkTestImage",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MilkTestImage_StorageKey",
                schema: "dbo",
                table: "MilkTestImage",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MilkTestImage_UploadedByUserId",
                schema: "dbo",
                table: "MilkTestImage",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MilkTestParameter_MilkTestId_Code",
                schema: "dbo",
                table: "MilkTestParameter",
                columns: new[] { "MilkTestId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MilkTestImage",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "MilkTestParameter",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "MilkTest",
                schema: "dbo");
        }
    }
}
