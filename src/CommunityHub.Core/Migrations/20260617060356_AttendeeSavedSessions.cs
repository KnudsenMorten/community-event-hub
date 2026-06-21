using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AttendeeSavedSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SavedSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedSessions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedSessions_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SavedSessions_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedSessions_EventId_ParticipantId",
                table: "SavedSessions",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedSessions_EventId_ParticipantId_SessionId",
                table: "SavedSessions",
                columns: new[] { "EventId", "ParticipantId", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedSessions_ParticipantId",
                table: "SavedSessions",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSessions_SessionId",
                table: "SavedSessions",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedSessions");
        }
    }
}
