using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoodhDirect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationEvent",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EventKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    IsCritical = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationEvent_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreference",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreference", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationPreference_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationTemplate",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TitleTemplate = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    BodyTemplate = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationTemplate", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserDevice",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    DeviceIdentifierHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProtectedToken = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RegisteredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InvalidatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDevice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserDevice_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notification",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NotificationEventId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DeepLink = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notification", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notification_NotificationEvent_NotificationEventId",
                        column: x => x.NotificationEventId,
                        principalSchema: "dbo",
                        principalTable: "NotificationEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notification_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDelivery",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NotificationId = table.Column<long>(type: "bigint", nullable: false),
                    UserDeviceId = table.Column<long>(type: "bigint", nullable: true),
                    Channel = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ProviderCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DestinationReference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDelivery", x => x.Id);
                    table.CheckConstraint("CK_NotificationDelivery_AttemptCount", "[AttemptCount] >= 0");
                    table.ForeignKey(
                        name: "FK_NotificationDelivery_Notification_NotificationId",
                        column: x => x.NotificationId,
                        principalSchema: "dbo",
                        principalTable: "Notification",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotificationDelivery_UserDevice_UserDeviceId",
                        column: x => x.UserDeviceId,
                        principalSchema: "dbo",
                        principalTable: "UserDevice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationAttempt",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NotificationDeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AttemptedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationAttempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationAttempt_NotificationDelivery_NotificationDeliveryId",
                        column: x => x.NotificationDeliveryId,
                        principalSchema: "dbo",
                        principalTable: "NotificationDelivery",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notification_NotificationEventId",
                schema: "dbo",
                table: "Notification",
                column: "NotificationEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notification_PublicId",
                schema: "dbo",
                table: "Notification",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notification_UserId_ReadAtUtc_CreatedAtUtc",
                schema: "dbo",
                table: "Notification",
                columns: new[] { "UserId", "ReadAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationAttempt_NotificationDeliveryId_AttemptNumber",
                schema: "dbo",
                table: "NotificationAttempt",
                columns: new[] { "NotificationDeliveryId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationAttempt_PublicId",
                schema: "dbo",
                table: "NotificationAttempt",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_NotificationId_Channel_UserDeviceId",
                schema: "dbo",
                table: "NotificationDelivery",
                columns: new[] { "NotificationId", "Channel", "UserDeviceId" },
                unique: true,
                filter: "[UserDeviceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_PublicId",
                schema: "dbo",
                table: "NotificationDelivery",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_Status_NextAttemptAtUtc",
                schema: "dbo",
                table: "NotificationDelivery",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_UserDeviceId",
                schema: "dbo",
                table: "NotificationDelivery",
                column: "UserDeviceId");

            migrationBuilder.CreateIndex(
                name: "UX_NotificationDelivery_NonDeviceChannel",
                schema: "dbo",
                table: "NotificationDelivery",
                columns: new[] { "NotificationId", "Channel" },
                unique: true,
                filter: "[UserDeviceId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvent_EventKey",
                schema: "dbo",
                table: "NotificationEvent",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvent_PublicId",
                schema: "dbo",
                table: "NotificationEvent",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvent_Status_OccurredAtUtc",
                schema: "dbo",
                table: "NotificationEvent",
                columns: new[] { "Status", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvent_UserId_OccurredAtUtc",
                schema: "dbo",
                table: "NotificationEvent",
                columns: new[] { "UserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreference_PublicId",
                schema: "dbo",
                table: "NotificationPreference",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreference_UserId_EventType_Channel",
                schema: "dbo",
                table: "NotificationPreference",
                columns: new[] { "UserId", "EventType", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplate_EventType_Channel_Language",
                schema: "dbo",
                table: "NotificationTemplate",
                columns: new[] { "EventType", "Channel", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplate_IsActive_EventType",
                schema: "dbo",
                table: "NotificationTemplate",
                columns: new[] { "IsActive", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplate_PublicId",
                schema: "dbo",
                table: "NotificationTemplate",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserDevice_PublicId",
                schema: "dbo",
                table: "UserDevice",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserDevice_UserId_DeviceIdentifierHash",
                schema: "dbo",
                table: "UserDevice",
                columns: new[] { "UserId", "DeviceIdentifierHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserDevice_UserId_IsActive",
                schema: "dbo",
                table: "UserDevice",
                columns: new[] { "UserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "UX_UserDevice_ActiveTokenHash",
                schema: "dbo",
                table: "UserDevice",
                column: "TokenHash",
                unique: true,
                filter: "[IsActive] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationAttempt",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "NotificationPreference",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "NotificationTemplate",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "NotificationDelivery",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Notification",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "UserDevice",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "NotificationEvent",
                schema: "dbo");
        }
    }
}
