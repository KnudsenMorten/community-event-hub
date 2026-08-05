using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionEvaluationIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvaluationDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PreviousKeyHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastBatteryPercent = table.Column<int>(type: "int", nullable: true),
                    LastSignalQuality = table.Column<int>(type: "int", nullable: true),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CachedRecordCount = table.Column<int>(type: "int", nullable: true),
                    ClockSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationDevices_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationResponses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SessionId = table.Column<int>(type: "int", nullable: true),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    CollectionTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeviceRecordId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ClockSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TimestampSuspect = table.Column<bool>(type: "bit", nullable: false),
                    FreeText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationResponses_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDevices_EventId_DeviceId",
                table: "EvaluationDevices",
                columns: new[] { "EventId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationResponses_DeviceId_DeviceRecordId",
                table: "EvaluationResponses",
                columns: new[] { "DeviceId", "DeviceRecordId" },
                unique: true,
                filter: "[DeviceId] IS NOT NULL AND [DeviceRecordId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationResponses_EventId_CollectionTimestamp",
                table: "EvaluationResponses",
                columns: new[] { "EventId", "CollectionTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationResponses_EventId_SessionId",
                table: "EvaluationResponses",
                columns: new[] { "EventId", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvaluationDevices");

            migrationBuilder.DropTable(
                name: "EvaluationResponses");
        }
    }
}
