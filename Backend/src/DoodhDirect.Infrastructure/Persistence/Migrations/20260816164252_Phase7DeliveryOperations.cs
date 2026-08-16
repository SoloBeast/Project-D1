using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7DeliveryOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Delivery",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OrderId = table.Column<long>(type: "bigint", nullable: true),
                    SubscriptionDeliveryId = table.Column<long>(type: "bigint", nullable: true),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    BranchId = table.Column<long>(type: "bigint", nullable: false),
                    AssignedEmployeeId = table.Column<long>(type: "bigint", nullable: true),
                    ScheduledDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CustomerNameSnapshot = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CustomerMobileSnapshot = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DestinationAddressSnapshot = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DeliveryInstructionsSnapshot = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DestinationLatitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    DestinationLongitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PickedUpAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OutForDeliveryAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ArrivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OtpVerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OperationalNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FailureLatitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    FailureLongitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Delivery", x => x.Id);
                    table.CheckConstraint("CK_Delivery_DestinationLatitude", "[DestinationLatitude] >= -90 AND [DestinationLatitude] <= 90");
                    table.CheckConstraint("CK_Delivery_DestinationLongitude", "[DestinationLongitude] >= -180 AND [DestinationLongitude] <= 180");
                    table.CheckConstraint("CK_Delivery_FailureCoordinates", "([FailureLatitude] IS NULL AND [FailureLongitude] IS NULL) OR ([FailureLatitude] BETWEEN -90 AND 90 AND [FailureLongitude] BETWEEN -180 AND 180)");
                    table.CheckConstraint("CK_Delivery_Source", "([OrderId] IS NOT NULL AND [SubscriptionDeliveryId] IS NULL AND [SourceType] = 'OneTimeOrder') OR ([OrderId] IS NULL AND [SubscriptionDeliveryId] IS NOT NULL AND [SourceType] = 'SubscriptionOccurrence')");
                    table.ForeignKey(
                        name: "FK_Delivery_Branch_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "dbo",
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Delivery_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "dbo",
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Delivery_SubscriptionDelivery_SubscriptionDeliveryId",
                        column: x => x.SubscriptionDeliveryId,
                        principalSchema: "dbo",
                        principalTable: "SubscriptionDelivery",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Delivery_User_AssignedEmployeeId",
                        column: x => x.AssignedEmployeeId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Delivery_User_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryAssignment",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    PreviousEmployeeId = table.Column<long>(type: "bigint", nullable: true),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    AssignedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryAssignment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryAssignment_Delivery_DeliveryId",
                        column: x => x.DeliveryId,
                        principalSchema: "dbo",
                        principalTable: "Delivery",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeliveryAssignment_User_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeliveryAssignment_User_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeliveryAssignment_User_PreviousEmployeeId",
                        column: x => x.PreviousEmployeeId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryLocation",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    AccuracyMetres = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryLocation", x => x.Id);
                    table.CheckConstraint("CK_DeliveryLocation_Accuracy", "[AccuracyMetres] IS NULL OR [AccuracyMetres] >= 0");
                    table.CheckConstraint("CK_DeliveryLocation_Latitude", "[Latitude] >= -90 AND [Latitude] <= 90");
                    table.CheckConstraint("CK_DeliveryLocation_Longitude", "[Longitude] >= -180 AND [Longitude] <= 180");
                    table.ForeignKey(
                        name: "FK_DeliveryLocation_Delivery_DeliveryId",
                        column: x => x.DeliveryId,
                        principalSchema: "dbo",
                        principalTable: "Delivery",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeliveryLocation_User_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryOtp",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    MaximumAttempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryOtp", x => x.Id);
                    table.CheckConstraint("CK_DeliveryOtp_Attempts", "[MaximumAttempts] > 0 AND [AttemptCount] >= 0 AND [AttemptCount] <= [MaximumAttempts]");
                    table.ForeignKey(
                        name: "FK_DeliveryOtp_Delivery_DeliveryId",
                        column: x => x.DeliveryId,
                        principalSchema: "dbo",
                        principalTable: "Delivery",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Delivery_AssignedEmployeeId_ScheduledDate_Status",
                schema: "dbo",
                table: "Delivery",
                columns: new[] { "AssignedEmployeeId", "ScheduledDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Delivery_BranchId_ScheduledDate_Status",
                schema: "dbo",
                table: "Delivery",
                columns: new[] { "BranchId", "ScheduledDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Delivery_CustomerId_ScheduledDate",
                schema: "dbo",
                table: "Delivery",
                columns: new[] { "CustomerId", "ScheduledDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Delivery_OrderId",
                schema: "dbo",
                table: "Delivery",
                column: "OrderId",
                unique: true,
                filter: "[OrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Delivery_PublicId",
                schema: "dbo",
                table: "Delivery",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Delivery_SubscriptionDeliveryId",
                schema: "dbo",
                table: "Delivery",
                column: "SubscriptionDeliveryId",
                unique: true,
                filter: "[SubscriptionDeliveryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAssignment_AssignedByUserId",
                schema: "dbo",
                table: "DeliveryAssignment",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAssignment_DeliveryId_AssignedAtUtc",
                schema: "dbo",
                table: "DeliveryAssignment",
                columns: new[] { "DeliveryId", "AssignedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAssignment_EmployeeId_AssignedAtUtc",
                schema: "dbo",
                table: "DeliveryAssignment",
                columns: new[] { "EmployeeId", "AssignedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAssignment_PreviousEmployeeId",
                schema: "dbo",
                table: "DeliveryAssignment",
                column: "PreviousEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLocation_DeliveryId_RecordedAtUtc",
                schema: "dbo",
                table: "DeliveryLocation",
                columns: new[] { "DeliveryId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLocation_EmployeeId",
                schema: "dbo",
                table: "DeliveryLocation",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLocation_RecordedAtUtc",
                schema: "dbo",
                table: "DeliveryLocation",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryOtp_DeliveryId_CreatedAtUtc",
                schema: "dbo",
                table: "DeliveryOtp",
                columns: new[] { "DeliveryId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryOtp_ExpiresAtUtc_ConsumedAtUtc",
                schema: "dbo",
                table: "DeliveryOtp",
                columns: new[] { "ExpiresAtUtc", "ConsumedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryOtp_PublicId",
                schema: "dbo",
                table: "DeliveryOtp",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveryAssignment",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "DeliveryLocation",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "DeliveryOtp",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Delivery",
                schema: "dbo");
        }
    }
}
