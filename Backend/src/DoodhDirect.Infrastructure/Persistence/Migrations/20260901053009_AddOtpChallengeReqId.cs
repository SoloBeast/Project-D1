using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOtpChallengeReqId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReqId",
                schema: "dbo",
                table: "OtpChallenge",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenge_ReqId",
                schema: "dbo",
                table: "OtpChallenge",
                column: "ReqId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OtpChallenge_ReqId",
                schema: "dbo",
                table: "OtpChallenge");

            migrationBuilder.DropColumn(
                name: "ReqId",
                schema: "dbo",
                table: "OtpChallenge");
        }
    }
}
