using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class PartyRsvpHeadCountParticipant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HeadCount",
                table: "PartyRsvps",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParticipantId",
                table: "PartyRsvps",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartyRsvps_ParticipantId",
                table: "PartyRsvps",
                column: "ParticipantId");

            migrationBuilder.AddForeignKey(
                name: "FK_PartyRsvps_Participants_ParticipantId",
                table: "PartyRsvps",
                column: "ParticipantId",
                principalTable: "Participants",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PartyRsvps_Participants_ParticipantId",
                table: "PartyRsvps");

            migrationBuilder.DropIndex(
                name: "IX_PartyRsvps_ParticipantId",
                table: "PartyRsvps");

            migrationBuilder.DropColumn(
                name: "HeadCount",
                table: "PartyRsvps");

            migrationBuilder.DropColumn(
                name: "ParticipantId",
                table: "PartyRsvps");
        }
    }
}
