using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase10LiveDairyCameras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Camera",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    InternalIdentifier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Camera", x => x.Id);
                    table.CheckConstraint("CK_Camera_DisplayOrder", "[DisplayOrder] >= 0");
                    table.ForeignKey(
                        name: "FK_Camera_Branch_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "dbo",
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CameraStream",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CameraId = table.Column<long>(type: "bigint", nullable: false),
                    Protocol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProviderCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProviderStreamReference = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraStream", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CameraStream_Camera_CameraId",
                        column: x => x.CameraId,
                        principalSchema: "dbo",
                        principalTable: "Camera",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Camera_BranchId_InternalIdentifier",
                schema: "dbo",
                table: "Camera",
                columns: new[] { "BranchId", "InternalIdentifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Camera_BranchId_IsActive_DisplayOrder",
                schema: "dbo",
                table: "Camera",
                columns: new[] { "BranchId", "IsActive", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Camera_IsActive_IsPublic_DisplayOrder",
                schema: "dbo",
                table: "Camera",
                columns: new[] { "IsActive", "IsPublic", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Camera_PublicId",
                schema: "dbo",
                table: "Camera",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CameraStream_CameraId",
                schema: "dbo",
                table: "CameraStream",
                column: "CameraId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CameraStream_PublicId",
                schema: "dbo",
                table: "CameraStream",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CameraStream",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Camera",
                schema: "dbo");
        }
    }
}
