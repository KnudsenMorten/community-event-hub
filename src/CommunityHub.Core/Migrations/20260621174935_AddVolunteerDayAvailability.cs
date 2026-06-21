using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddVolunteerDayAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VolunteerDayAvailabilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerDayAvailabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerDayAvailabilities_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerDayAvailabilities_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerDayAvailabilities_EventId_ParticipantId_Day",
                table: "VolunteerDayAvailabilities",
                columns: new[] { "EventId", "ParticipantId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerDayAvailabilities_ParticipantId",
                table: "VolunteerDayAvailabilities",
                column: "ParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VolunteerDayAvailabilities");
        }
    }
}
