using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerBankDetailsAndWithdrawalRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerBankDetail",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    AccountHolderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProtectedAccountNumber = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AccountLast4 = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Ifsc = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerBankDetail", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerBankDetail_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WithdrawalRequest",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    RequestNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    BankDetailId = table.Column<long>(type: "bigint", nullable: false),
                    AccountHolderNameSnapshot = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BankNameSnapshot = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IfscSnapshot = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    AccountLast4Snapshot = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DecidedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionRemarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReversalCredited = table.Column<bool>(type: "bit", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WithdrawalRequest", x => x.Id);
                    table.CheckConstraint("CK_WithdrawalRequest_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_WithdrawalRequest_CustomerBankDetail_BankDetailId",
                        column: x => x.BankDetailId,
                        principalSchema: "dbo",
                        principalTable: "CustomerBankDetail",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WithdrawalRequest_User_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WithdrawalRequest_User_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerBankDetail_PublicId",
                schema: "dbo",
                table: "CustomerBankDetail",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerBankDetail_UserId",
                schema: "dbo",
                table: "CustomerBankDetail",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalRequest_BankDetailId",
                schema: "dbo",
                table: "WithdrawalRequest",
                column: "BankDetailId");

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalRequest_CustomerId_IdempotencyKey",
                schema: "dbo",
                table: "WithdrawalRequest",
                columns: new[] { "CustomerId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalRequest_DecidedByUserId",
                schema: "dbo",
                table: "WithdrawalRequest",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalRequest_PublicId",
                schema: "dbo",
                table: "WithdrawalRequest",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalRequest_RequestNumber",
                schema: "dbo",
                table: "WithdrawalRequest",
                column: "RequestNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WithdrawalRequest_Status_CreatedAtUtc",
                schema: "dbo",
                table: "WithdrawalRequest",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WithdrawalRequest",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "CustomerBankDetail",
                schema: "dbo");
        }
    }
}
