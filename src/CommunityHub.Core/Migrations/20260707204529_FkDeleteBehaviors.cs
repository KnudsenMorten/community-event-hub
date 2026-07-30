using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §253 G15 — participant hard-delete FK holes. The volunteer-availability
    /// Participant FKs move from <c>Restrict</c> to <c>ClientCascade</c> in the EF
    /// model (tracked dependents are deleted client-side instead of EF throwing at
    /// <c>Remove()</c> time); at the database this only re-creates the two FKs with
    /// the default <c>NO ACTION</c>, which is functionally identical to the previous
    /// <c>Restrict</c> on SQL Server (both non-cascading). No data change, no new
    /// column — additive/safe. The untracked-dependents cleanup (PartyRsvp +
    /// availability rows) lives in <c>CommunityHubDbContext.SaveChanges*</c>.
    /// </summary>
    public partial class FkDeleteBehaviors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VolunteerAvailabilities_Participants_ParticipantId",
                table: "VolunteerAvailabilities");

            migrationBuilder.DropForeignKey(
                name: "FK_VolunteerDayAvailabilities_Participants_ParticipantId",
                table: "VolunteerDayAvailabilities");

            migrationBuilder.AddForeignKey(
                name: "FK_VolunteerAvailabilities_Participants_ParticipantId",
                table: "VolunteerAvailabilities",
                column: "ParticipantId",
                principalTable: "Participants",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_VolunteerDayAvailabilities_Participants_ParticipantId",
                table: "VolunteerDayAvailabilities",
                column: "ParticipantId",
                principalTable: "Participants",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VolunteerAvailabilities_Participants_ParticipantId",
                table: "VolunteerAvailabilities");

            migrationBuilder.DropForeignKey(
                name: "FK_VolunteerDayAvailabilities_Participants_ParticipantId",
                table: "VolunteerDayAvailabilities");

            migrationBuilder.AddForeignKey(
                name: "FK_VolunteerAvailabilities_Participants_ParticipantId",
                table: "VolunteerAvailabilities",
                column: "ParticipantId",
                principalTable: "Participants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_VolunteerDayAvailabilities_Participants_ParticipantId",
                table: "VolunteerDayAvailabilities",
                column: "ParticipantId",
                principalTable: "Participants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
