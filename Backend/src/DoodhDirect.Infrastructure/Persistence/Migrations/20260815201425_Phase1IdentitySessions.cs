using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase1IdentitySessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                schema: "dbo",
                table: "User",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SessionId",
                schema: "dbo",
                table: "RefreshToken",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OtpChallenge",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Destination = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RequestedFromIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtpChallenge", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserSession",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    DeviceIdentifierHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    Platform = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    IPAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevocationReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSession_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshToken_SessionId_ExpiresAtUtc",
                schema: "dbo",
                table: "RefreshToken",
                columns: new[] { "SessionId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenge_Destination_Purpose_CreatedAtUtc",
                schema: "dbo",
                table: "OtpChallenge",
                columns: new[] { "Destination", "Purpose", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenge_ExpiresAtUtc_ConsumedAtUtc",
                schema: "dbo",
                table: "OtpChallenge",
                columns: new[] { "ExpiresAtUtc", "ConsumedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenge_PublicId",
                schema: "dbo",
                table: "OtpChallenge",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSession_PublicId",
                schema: "dbo",
                table: "UserSession",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSession_UserId_DeviceIdentifierHash_RevokedAtUtc",
                schema: "dbo",
                table: "UserSession",
                columns: new[] { "UserId", "DeviceIdentifierHash", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSession_UserId_RevokedAtUtc_LastSeenAtUtc",
                schema: "dbo",
                table: "UserSession",
                columns: new[] { "UserId", "RevokedAtUtc", "LastSeenAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_RefreshToken_UserSession_SessionId",
                schema: "dbo",
                table: "RefreshToken",
                column: "SessionId",
                principalSchema: "dbo",
                principalTable: "UserSession",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RefreshToken_UserSession_SessionId",
                schema: "dbo",
                table: "RefreshToken");

            migrationBuilder.DropTable(
                name: "OtpChallenge",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "UserSession",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "IX_RefreshToken_SessionId_ExpiresAtUtc",
                schema: "dbo",
                table: "RefreshToken");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                schema: "dbo",
                table: "User");

            migrationBuilder.DropColumn(
                name: "SessionId",
                schema: "dbo",
                table: "RefreshToken");
        }
    }
}
