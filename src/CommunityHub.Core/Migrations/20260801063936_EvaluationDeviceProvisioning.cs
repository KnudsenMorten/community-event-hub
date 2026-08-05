using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class EvaluationDeviceProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HealthTelemetryEnabled",
                table: "EvaluationDevices",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SerialNumber",
                table: "EvaluationDevices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EvaluationDeviceProvisionRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SerialNumber = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastRequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RequestCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    EvaluationDeviceId = table.Column<int>(type: "int", nullable: true),
                    KeyIssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationDeviceProvisionRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationDeviceProvisionRequests_EvaluationDevices_EvaluationDeviceId",
                        column: x => x.EvaluationDeviceId,
                        principalTable: "EvaluationDevices",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationDeviceProvisionRequests_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationTelemetryWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    EvaluationDeviceId = table.Column<int>(type: "int", nullable: true),
                    FromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ToUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationTelemetryWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationTelemetryWindows_EvaluationDevices_EvaluationDeviceId",
                        column: x => x.EvaluationDeviceId,
                        principalTable: "EvaluationDevices",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationTelemetryWindows_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDeviceProvisionRequests_EvaluationDeviceId",
                table: "EvaluationDeviceProvisionRequests",
                column: "EvaluationDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDeviceProvisionRequests_EventId_DeviceId",
                table: "EvaluationDeviceProvisionRequests",
                columns: new[] { "EventId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationTelemetryWindows_EvaluationDeviceId",
                table: "EvaluationTelemetryWindows",
                column: "EvaluationDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationTelemetryWindows_EventId_IsActive_FromUtc",
                table: "EvaluationTelemetryWindows",
                columns: new[] { "EventId", "IsActive", "FromUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvaluationDeviceProvisionRequests");

            migrationBuilder.DropTable(
                name: "EvaluationTelemetryWindows");

            migrationBuilder.DropColumn(
                name: "HealthTelemetryEnabled",
                table: "EvaluationDevices");

            migrationBuilder.DropColumn(
                name: "SerialNumber",
                table: "EvaluationDevices");
        }
    }
}
