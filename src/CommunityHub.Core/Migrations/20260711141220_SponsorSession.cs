using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasSponsorSession",
                table: "SponsorInfos",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SponsorSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Abstract = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SyncedToZohoAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorSessions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SponsorSessionSpeakers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SponsorSessionId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorSessionSpeakers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorSessionSpeakers_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SponsorSessionSpeakers_SponsorSessions_SponsorSessionId",
                        column: x => x.SponsorSessionId,
                        principalTable: "SponsorSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorSessions_EventId_SponsorCompanyId",
                table: "SponsorSessions",
                columns: new[] { "EventId", "SponsorCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorSessionSpeakers_ParticipantId",
                table: "SponsorSessionSpeakers",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SponsorSessionSpeakers_SponsorSessionId_Email",
                table: "SponsorSessionSpeakers",
                columns: new[] { "SponsorSessionId", "Email" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SponsorSessionSpeakers");

            migrationBuilder.DropTable(
                name: "SponsorSessions");

            migrationBuilder.DropColumn(
                name: "HasSponsorSession",
                table: "SponsorInfos");
        }
    }
}
