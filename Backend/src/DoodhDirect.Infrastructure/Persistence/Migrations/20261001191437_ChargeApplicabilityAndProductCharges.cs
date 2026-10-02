using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChargeApplicabilityAndProductCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ApplicableOnAll",
                schema: "dbo",
                table: "Charge",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "ProductCharge",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    ChargeId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCharge", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCharge_Charge_ChargeId",
                        column: x => x.ChargeId,
                        principalSchema: "dbo",
                        principalTable: "Charge",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductCharge_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "dbo",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductCharge_ChargeId",
                schema: "dbo",
                table: "ProductCharge",
                column: "ChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCharge_ProductId_ChargeId",
                schema: "dbo",
                table: "ProductCharge",
                columns: new[] { "ProductId", "ChargeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductCharge",
                schema: "dbo");

            migrationBuilder.DropColumn(
                name: "ApplicableOnAll",
                schema: "dbo",
                table: "Charge");
        }
    }
}
