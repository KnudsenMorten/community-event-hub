using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionEvaluationRoomsAndSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RoomId",
                table: "EvaluationDevices",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EvaluationRooms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationRooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationRooms_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CehSessionId = table.Column<int>(type: "int", nullable: true),
                    RoomId = table.Column<int>(type: "int", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TrackName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ScheduledStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScheduledEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CollectionWindowOpensAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CollectionWindowClosesAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScheduledLengthMinutes = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationSessions_EvaluationRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "EvaluationRooms",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationSessions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDevices_RoomId",
                table: "EvaluationDevices",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationRooms_EventId_NameKey",
                table: "EvaluationRooms",
                columns: new[] { "EventId", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSessions_EventId_CehSessionId",
                table: "EvaluationSessions",
                columns: new[] { "EventId", "CehSessionId" },
                unique: true,
                filter: "[CehSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSessions_RoomId_CollectionWindowOpensAt",
                table: "EvaluationSessions",
                columns: new[] { "RoomId", "CollectionWindowOpensAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_EvaluationDevices_EvaluationRooms_RoomId",
                table: "EvaluationDevices",
                column: "RoomId",
                principalTable: "EvaluationRooms",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EvaluationDevices_EvaluationRooms_RoomId",
                table: "EvaluationDevices");

            migrationBuilder.DropTable(
                name: "EvaluationSessions");

            migrationBuilder.DropTable(
                name: "EvaluationRooms");

            migrationBuilder.DropIndex(
                name: "IX_EvaluationDevices_RoomId",
                table: "EvaluationDevices");

            migrationBuilder.DropColumn(
                name: "RoomId",
                table: "EvaluationDevices");
        }
    }
}
