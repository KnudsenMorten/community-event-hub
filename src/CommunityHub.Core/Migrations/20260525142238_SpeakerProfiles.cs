using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SpeakerProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpeakerProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Accreditation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsFirstTimeSpeaker = table.Column<bool>(type: "bit", nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Tagline = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Biography = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Blog = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LinkedIn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Twitter = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSessionizeImportAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeakerProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpeakerProfiles_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpeakerProfiles_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerProfiles_EventId_ParticipantId",
                table: "SpeakerProfiles",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerProfiles_ParticipantId",
                table: "SpeakerProfiles",
                column: "ParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpeakerProfiles");
        }
    }
}
