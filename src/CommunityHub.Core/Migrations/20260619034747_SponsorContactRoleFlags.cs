using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorContactRoleFlags : Migration
    {
        // REQUIREMENTS §7c — universal sponsor-email audience rule. Adds the two
        // independent per-contact role flags (IsSigner / IsEventCoordinator) to
        // Participants plus a (EventId, SponsorCompanyId) index for the shared
        // SponsorRecipientResolver's company-scoped lookup. Both flags default
        // false (no contact is a signer / coordinator until CM's default pointer
        // sets it at sync time or an organizer sets it in the hub).
        //
        // NOTE: the regenerated CommunityHubDbContextModelSnapshot also reconciles
        // pre-existing snapshot drift (the SurveyStates table + the magic-link
        // grant model from two parallel-merged migrations were missing from the
        // committed snapshot). Those tables are already created by their own
        // migrations (20260618082351_SurveyState / 20260618130321_
        // WelcomeAutoLoginSingleUseGrant), so this migration deliberately does NOT
        // re-create them — it only adds the new columns + index.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEventCoordinator",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSigner",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Participants_EventId_SponsorCompanyId",
                table: "Participants",
                columns: new[] { "EventId", "SponsorCompanyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Participants_EventId_SponsorCompanyId",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "IsEventCoordinator",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "IsSigner",
                table: "Participants");
        }
    }
}
