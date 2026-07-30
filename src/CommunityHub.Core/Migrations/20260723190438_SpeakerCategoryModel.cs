using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §299 b13 (C3/C5) — the speaker-category remodel. PURELY ADDITIVE: two new
    /// nullable int columns on SpeakerProfiles (SpeakerCategory = the canonical
    /// organizer-set category; GuestFundedNights = organizer-entered ELDK-funded
    /// hotel nights for Guest speakers) plus a one-time data backfill from the
    /// retired SpeakerFunding column (kept — additive-only rule; display/audit
    /// only): Supported(0)→Community(0), SponsorSelfFunded(1)→Sponsor(1),
    /// Organizer(2)→NULL (the legacy organizer double-count handling stays FROZEN
    /// pending ❓OPEN-22 SamePersonAsId linking; a null category contributes
    /// nothing, exactly as Organizer funding did).
    /// </summary>
    public partial class SpeakerCategoryModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GuestFundedNights",
                table: "SpeakerProfiles",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpeakerCategory",
                table: "SpeakerProfiles",
                type: "int",
                nullable: true);

            // Backfill from the retired funding value (see class doc). Idempotent:
            // only rows still NULL are touched.
            migrationBuilder.Sql(
                "UPDATE SpeakerProfiles SET SpeakerCategory = CASE SpeakerFunding "
                + "WHEN 0 THEN 0 WHEN 1 THEN 1 ELSE NULL END "
                + "WHERE SpeakerCategory IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GuestFundedNights",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "SpeakerCategory",
                table: "SpeakerProfiles");
        }
    }
}
