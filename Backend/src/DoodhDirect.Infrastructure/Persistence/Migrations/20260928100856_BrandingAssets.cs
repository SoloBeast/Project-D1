using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BrandingAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrandingAsset",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetKind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    UploadedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandingAsset", x => x.Id);
                    table.CheckConstraint("CK_BrandingAsset_FileSize", "[FileSize] > 0");
                    table.ForeignKey(
                        name: "FK_BrandingAsset_User_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrandingAsset_AssetKind",
                schema: "dbo",
                table: "BrandingAsset",
                column: "AssetKind",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BrandingAsset_PublicId",
                schema: "dbo",
                table: "BrandingAsset",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BrandingAsset_StorageKey",
                schema: "dbo",
                table: "BrandingAsset",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BrandingAsset_UploadedByUserId",
                schema: "dbo",
                table: "BrandingAsset",
                column: "UploadedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandingAsset",
                schema: "dbo");
        }
    }
}
