using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SpeakerSponsorCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SponsorPackage",
                table: "SponsorInfos",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SpeakerFunding",
                table: "SpeakerProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsBoothMember",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SamePersonAsId",
                table: "Participants",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ParticipantOrderOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Item = table.Column<int>(type: "int", nullable: false),
                    Include = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SetByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    SetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantOrderOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParticipantOrderOverrides_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ParticipantOrderOverrides_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Participants_SamePersonAsId",
                table: "Participants",
                column: "SamePersonAsId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantOrderOverrides_EventId_ParticipantId_Item",
                table: "ParticipantOrderOverrides",
                columns: new[] { "EventId", "ParticipantId", "Item" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantOrderOverrides_ParticipantId",
                table: "ParticipantOrderOverrides",
                column: "ParticipantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Participants_Participants_SamePersonAsId",
                table: "Participants",
                column: "SamePersonAsId",
                principalTable: "Participants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // --- Data migration: drop the MasterclassSpeaker role -------------
            // The MasterclassSpeaker role (enum value 2) was removed; it folds
            // into Speaker (value 1), with the pre-day nuance moving onto
            // SpeakerProfile.SpeakingPreDay. Convert any existing rows so a person
            // stored as MasterclassSpeaker (2) becomes a Speaker (1) AND has their
            // speaker profile marked as speaking on the pre-day. Both statements
            // are no-ops when there is no such row (fresh DBs included).
            migrationBuilder.Sql(@"
UPDATE sp
   SET sp.SpeakingPreDay = 1
  FROM SpeakerProfiles sp
  INNER JOIN Participants p
     ON p.Id = sp.ParticipantId
 WHERE p.Role = 2;");
            migrationBuilder.Sql(@"
UPDATE Participants
   SET Role = 1
 WHERE Role = 2;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Participants_Participants_SamePersonAsId",
                table: "Participants");

            migrationBuilder.DropTable(
                name: "ParticipantOrderOverrides");

            migrationBuilder.DropIndex(
                name: "IX_Participants_SamePersonAsId",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "SponsorPackage",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "SpeakerFunding",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "IsBoothMember",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "SamePersonAsId",
                table: "Participants");
        }
    }
}
