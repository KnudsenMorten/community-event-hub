using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SignageAgendaActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgendaActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    BackstageSessionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Room = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Track = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ActivityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Speakers = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DayIndex = table.Column<int>(type: "int", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgendaActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgendaActivities_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgendaActivities_EventId_BackstageSessionId",
                table: "AgendaActivities",
                columns: new[] { "EventId", "BackstageSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgendaActivities_EventId_StartsAt_EndsAt",
                table: "AgendaActivities",
                columns: new[] { "EventId", "StartsAt", "EndsAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgendaActivities");
        }
    }
}
