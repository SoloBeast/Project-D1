using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionChargesAndChargesTotal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ChargesTotal",
                schema: "dbo",
                table: "Subscription",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "SubscriptionCharge",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubscriptionId = table.Column<long>(type: "bigint", nullable: false),
                    ChargeType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ChargeCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Percentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    BaseAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionCharge", x => x.Id);
                    table.CheckConstraint("CK_SubscriptionCharge_Amount", "[Amount] >= 0");
                    table.CheckConstraint("CK_SubscriptionCharge_BaseAmount", "[BaseAmount] >= 0");
                    table.CheckConstraint("CK_SubscriptionCharge_Percentage", "[Percentage] > 0");
                    table.ForeignKey(
                        name: "FK_SubscriptionCharge_Subscription_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "dbo",
                        principalTable: "Subscription",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionCharge_SubscriptionId",
                schema: "dbo",
                table: "SubscriptionCharge",
                column: "SubscriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriptionCharge",
                schema: "dbo");

            migrationBuilder.DropColumn(
                name: "ChargesTotal",
                schema: "dbo",
                table: "Subscription");
        }
    }
}
