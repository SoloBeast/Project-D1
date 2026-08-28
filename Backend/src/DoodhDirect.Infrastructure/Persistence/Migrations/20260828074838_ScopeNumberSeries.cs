using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeNumberSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NumberSeries_Code",
                schema: "dbo",
                table: "NumberSeries");

            migrationBuilder.AddColumn<string>(
                name: "ScopeKey",
                schema: "dbo",
                table: "NumberSeries",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_NumberSeries_Code_ScopeKey",
                schema: "dbo",
                table: "NumberSeries",
                columns: new[] { "Code", "ScopeKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NumberSeries_Code_ScopeKey",
                schema: "dbo",
                table: "NumberSeries");

            migrationBuilder.DropColumn(
                name: "ScopeKey",
                schema: "dbo",
                table: "NumberSeries");

            migrationBuilder.CreateIndex(
                name: "IX_NumberSeries_Code",
                schema: "dbo",
                table: "NumberSeries",
                column: "Code",
                unique: true);
        }
    }
}
