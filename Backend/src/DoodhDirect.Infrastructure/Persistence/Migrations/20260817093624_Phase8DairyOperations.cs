using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8DairyOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MilkProduction",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    ProductionAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BuffaloCount = table.Column<int>(type: "int", nullable: false),
                    QuantityProduced = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RecordedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    Shift = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MilkProduction", x => x.Id);
                    table.CheckConstraint("CK_MilkProduction_BuffaloCount", "[BuffaloCount] > 0");
                    table.CheckConstraint("CK_MilkProduction_QuantityProduced", "[QuantityProduced] > 0");
                    table.ForeignKey(
                        name: "FK_MilkProduction_Branch_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "dbo",
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MilkProduction_User_RecordedByUserId",
                        column: x => x.RecordedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MilkBatch",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    ProductionId = table.Column<long>(type: "bigint", nullable: false),
                    BatchNumber = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProductionAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    QuantityProduced = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MilkBatch", x => x.Id);
                    table.CheckConstraint("CK_MilkBatch_QuantityProduced", "[QuantityProduced] > 0");
                    table.ForeignKey(
                        name: "FK_MilkBatch_Branch_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "dbo",
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MilkBatch_MilkProduction_ProductionId",
                        column: x => x.ProductionId,
                        principalSchema: "dbo",
                        principalTable: "MilkProduction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MilkUsage",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    QuantityUsed = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    RecordedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MilkUsage", x => x.Id);
                    table.CheckConstraint("CK_MilkUsage_QuantityUsed", "[QuantityUsed] > 0");
                    table.ForeignKey(
                        name: "FK_MilkUsage_Branch_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "dbo",
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MilkUsage_MilkBatch_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "dbo",
                        principalTable: "MilkBatch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MilkUsage_User_RecordedByUserId",
                        column: x => x.RecordedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MilkBatch_BranchId_BatchNumber",
                schema: "dbo",
                table: "MilkBatch",
                columns: new[] { "BranchId", "BatchNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MilkBatch_BranchId_ProductionAtUtc_Status",
                schema: "dbo",
                table: "MilkBatch",
                columns: new[] { "BranchId", "ProductionAtUtc", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MilkBatch_ProductionId",
                schema: "dbo",
                table: "MilkBatch",
                column: "ProductionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MilkBatch_PublicId",
                schema: "dbo",
                table: "MilkBatch",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MilkProduction_BranchId_ProductionAtUtc",
                schema: "dbo",
                table: "MilkProduction",
                columns: new[] { "BranchId", "ProductionAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MilkProduction_PublicId",
                schema: "dbo",
                table: "MilkProduction",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MilkProduction_RecordedByUserId",
                schema: "dbo",
                table: "MilkProduction",
                column: "RecordedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_BatchId_UsedAtUtc",
                schema: "dbo",
                table: "MilkUsage",
                columns: new[] { "BatchId", "UsedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_BranchId_UsedAtUtc",
                schema: "dbo",
                table: "MilkUsage",
                columns: new[] { "BranchId", "UsedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_PublicId",
                schema: "dbo",
                table: "MilkUsage",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MilkUsage_RecordedByUserId",
                schema: "dbo",
                table: "MilkUsage",
                column: "RecordedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MilkUsage",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "MilkBatch",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "MilkProduction",
                schema: "dbo");
        }
    }
}
