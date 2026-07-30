using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class MasterClassSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MasterClassSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    AttendeeId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    UnsubscribedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterClassSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterClassSubscriptions_Attendees_AttendeeId",
                        column: x => x.AttendeeId,
                        principalTable: "Attendees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassSubscriptions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MasterClassSubscriptions_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassSubscriptions_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_AttendeeId",
                table: "MasterClassSubscriptions",
                column: "AttendeeId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_EventId",
                table: "MasterClassSubscriptions",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_ParticipantId",
                table: "MasterClassSubscriptions",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_SessionId_Kind",
                table: "MasterClassSubscriptions",
                columns: new[] { "SessionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_SessionId_Kind_AttendeeId",
                table: "MasterClassSubscriptions",
                columns: new[] { "SessionId", "Kind", "AttendeeId" },
                unique: true,
                filter: "[AttendeeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_SessionId_Kind_ParticipantId",
                table: "MasterClassSubscriptions",
                columns: new[] { "SessionId", "Kind", "ParticipantId" },
                unique: true,
                filter: "[ParticipantId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MasterClassSubscriptions");
        }
    }
}
